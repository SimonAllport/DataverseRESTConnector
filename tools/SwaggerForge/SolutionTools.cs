using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Newtonsoft.Json.Linq;

namespace SwaggerForge;

/// <summary>
/// Solution and metadata housekeeping that the Power Platform CLI cannot do.
///
/// `pac solution add-solution-component` refuses the two component types this
/// project most needs to ship: it rejects the correct EntityDataProvider type id
/// (181) as unknown, mis-resolves the name to 78, and cannot resolve PluginPackage
/// at all. Without those, a managed export succeeds but contains no plug-in
/// payload, and the provider lands in the target with no code behind it.
///
/// `list-components` exists to discover the numeric component types empirically
/// rather than guessing them, since the CLI renders the optionset as blank.
/// </summary>
internal static class SolutionTools
{
    public static int ListComponents(string[] args)
    {
        var solution = Program.Opt(args, "--solution")
            ?? throw new ArgumentException("--solution <unique name> required");

        using var client = Program.Connect(args);

        var q = new QueryExpression("solutioncomponent")
        {
            ColumnSet = new ColumnSet("componenttype", "objectid", "solutioncomponentid"),
            Criteria =
            {
                Conditions =
                {
                    new ConditionExpression("solutionid", ConditionOperator.Equal, ResolveSolutionId(client, solution)),
                },
            },
        };

        var rows = client.RetrieveMultiple(q).Entities;
        Console.WriteLine($"{rows.Count} component(s) in '{solution}':");
        foreach (var r in rows)
        {
            var type = r.GetAttributeValue<OptionSetValue>("componenttype")?.Value;
            Console.WriteLine($"  type={type,-6} objectid={r.GetAttributeValue<Guid>("objectid")}");
        }
        return 0;
    }

    public static int AddComponent(string[] args)
    {
        var solution = Program.Opt(args, "--solution")
            ?? throw new ArgumentException("--solution <unique name> required");
        var id = Guid.Parse(Program.Opt(args, "--id")
            ?? throw new ArgumentException("--id <guid> required"));
        var type = int.Parse(Program.Opt(args, "--type")
            ?? throw new ArgumentException("--type <int> required, see list-components"));

        using var client = Program.Connect(args);

        client.Execute(new AddSolutionComponentRequest
        {
            ComponentId = id,
            ComponentType = type,
            SolutionUniqueName = solution,
            AddRequiredComponents = Program.Flag(args, "--required"),
        });

        Console.WriteLine($"Added component {id} (type {type}) to '{solution}'.");
        return 0;
    }

    /// <summary>
    /// Creates an ordinary (non-virtual) custom table. Needed because Dataverse
    /// refuses to register custom plug-in steps against a virtual entity, and the
    /// data source table is one — so the design-time authoring record has to live
    /// on a normal table of its own.
    /// </summary>
    public static int CreateTable(string[] args)
    {
        var logical = Program.Opt(args, "--entity")
            ?? throw new ArgumentException("--entity <logical name> required");
        var display = Program.Opt(args, "--display") ?? logical;
        var plural = Program.Opt(args, "--plural") ?? display + "s";
        var primaryName = Program.Opt(args, "--primaryname") ?? logical + "_name";
        var solution = Program.Opt(args, "--solution");

        using var client = Program.Connect(args);

        var req = new CreateEntityRequest
        {
            Entity = new Microsoft.Xrm.Sdk.Metadata.EntityMetadata
            {
                SchemaName = logical,
                LogicalName = logical,
                DisplayName = new Microsoft.Xrm.Sdk.Label(display, 1033),
                DisplayCollectionName = new Microsoft.Xrm.Sdk.Label(plural, 1033),
                OwnershipType = Microsoft.Xrm.Sdk.Metadata.OwnershipTypes.UserOwned,
                IsActivity = false,
            },
            HasActivities = false,
            HasNotes = false,
            PrimaryAttribute = new Microsoft.Xrm.Sdk.Metadata.StringAttributeMetadata
            {
                SchemaName = primaryName,
                LogicalName = primaryName,
                RequiredLevel = new Microsoft.Xrm.Sdk.Metadata.AttributeRequiredLevelManagedProperty(
                    Microsoft.Xrm.Sdk.Metadata.AttributeRequiredLevel.ApplicationRequired),
                MaxLength = 200,
                DisplayName = new Microsoft.Xrm.Sdk.Label("Name", 1033),
            },
        };
        if (solution != null) req.SolutionUniqueName = solution;

        client.Execute(req);
        Console.WriteLine($"Created table '{logical}'.");
        return 0;
    }

    /// <summary>
    /// Registers a plug-in step, so wiring up a handler does not require the
    /// Windows-only Plugin Registration Tool.
    /// </summary>
    public static int RegisterStep(string[] args)
    {
        var typeName = Program.Opt(args, "--plugintype")
            ?? throw new ArgumentException("--plugintype <full type name> required");
        var entity = Program.Opt(args, "--entity")
            ?? throw new ArgumentException("--entity <logical name> required");
        var message = Program.Opt(args, "--message") ?? "Update";
        var stage = int.Parse(Program.Opt(args, "--stage") ?? "40");      // 40 = post-operation
        var mode = int.Parse(Program.Opt(args, "--mode") ?? "0");         // 0 = synchronous
        var filtering = Program.Opt(args, "--filteringattributes");
        var solution = Program.Opt(args, "--solution");

        using var client = Program.Connect(args);

        var pluginTypeId = LookupSingle(client, "plugintype", "typename", typeName, "plugintypeid");
        var messageId = LookupSingle(client, "sdkmessage", "name", message, "sdkmessageid");

        // The filter ties a message to a specific table; without it the step
        // would fire for that message on every table.
        var filterQuery = new QueryExpression("sdkmessagefilter")
        {
            ColumnSet = new ColumnSet("sdkmessagefilterid"),
            Criteria =
            {
                Conditions =
                {
                    new ConditionExpression("sdkmessageid", ConditionOperator.Equal, messageId),
                    new ConditionExpression("primaryobjecttypecode", ConditionOperator.Equal, entity),
                },
            },
        };
        var filters = client.RetrieveMultiple(filterQuery).Entities;
        if (filters.Count == 0)
            throw new InvalidOperationException(
                $"No sdkmessagefilter for message '{message}' on entity '{entity}'.");

        var name = $"{typeName}: {message} of {entity}";

        var existing = new QueryExpression("sdkmessageprocessingstep")
        {
            ColumnSet = new ColumnSet("sdkmessageprocessingstepid"),
            Criteria = { Conditions = { new ConditionExpression("name", ConditionOperator.Equal, name) } },
        };
        if (client.RetrieveMultiple(existing).Entities.Count > 0)
        {
            Console.WriteLine($"Step already registered: {name}");
            return 0;
        }

        var step = new Entity("sdkmessageprocessingstep")
        {
            ["name"] = name,
            ["plugintypeid"] = new EntityReference("plugintype", pluginTypeId),
            ["sdkmessageid"] = new EntityReference("sdkmessage", messageId),
            ["sdkmessagefilterid"] = new EntityReference("sdkmessagefilter", filters[0].Id),
            ["stage"] = new OptionSetValue(stage),
            ["mode"] = new OptionSetValue(mode),
            ["rank"] = 1,
            ["supporteddeployment"] = new OptionSetValue(0),
        };
        if (!string.IsNullOrWhiteSpace(filtering))
            step["filteringattributes"] = filtering;

        var id = client.Create(step);
        Console.WriteLine($"Registered step {id}: {name}");

        if (solution != null)
        {
            client.Execute(new AddSolutionComponentRequest
            {
                ComponentId = id,
                ComponentType = 92, // SdkMessageProcessingStep
                SolutionUniqueName = solution,
                AddRequiredComponents = false,
            });
            Console.WriteLine($"Added step to solution '{solution}'.");
        }

        return 0;
    }

    private static Guid LookupSingle(
        Microsoft.PowerPlatform.Dataverse.Client.ServiceClient client,
        string entity, string attribute, string value, string idAttribute)
    {
        var q = new QueryExpression(entity)
        {
            ColumnSet = new ColumnSet(idAttribute),
            Criteria = { Conditions = { new ConditionExpression(attribute, ConditionOperator.Equal, value) } },
        };
        var rows = client.RetrieveMultiple(q).Entities;
        if (rows.Count == 0)
            throw new InvalidOperationException($"No {entity} where {attribute} = '{value}'.");
        return rows[0].Id;
    }

    /// <summary>
    /// Adds a single column to an existing table. Used to extend the data source
    /// record with the design-time fields the admin plug-in drives from.
    /// </summary>
    public static int AddColumn(string[] args)
    {
        var entity = Program.Opt(args, "--entity")
            ?? throw new ArgumentException("--entity <logical name> required");
        var name = Program.Opt(args, "--name")
            ?? throw new ArgumentException("--name <logical name> required");
        var type = Program.Opt(args, "--type") ?? "string";
        var display = Program.Opt(args, "--display") ?? name;
        var solution = Program.Opt(args, "--solution");
        var maxLength = Program.Opt(args, "--maxlength");

        var col = new JObject { ["type"] = type };
        if (maxLength != null) col["maxLength"] = int.Parse(maxLength);
        // Only external-name validated tables (virtual entities, and the data
        // source table) require this; ordinary tables reject it.
        var externalName = Program.Opt(args, "--externalname");
        if (externalName != null) col["sourcePath"] = externalName;

        using var client = Program.Connect(args);

        var attr = MetadataBuilder.BuildAttribute(name, col);
        attr.DisplayName = new Microsoft.Xrm.Sdk.Label(display, 1033);

        var req = new CreateAttributeRequest { EntityName = entity, Attribute = attr };
        if (solution != null) req.SolutionUniqueName = solution;
        client.Execute(req);

        Console.WriteLine($"Added column '{name}' ({type}) to '{entity}'.");
        return 0;
    }

    /// <summary>Inverse of create-tables: drop a virtual table so a changed mapping can be reapplied.</summary>
    public static int DeleteTable(string[] args)
    {
        var table = Program.Opt(args, "--table")
            ?? throw new ArgumentException("--table <logical name> required");

        using var client = Program.Connect(args);
        client.Execute(new DeleteEntityRequest { LogicalName = table });

        Console.WriteLine($"Deleted table '{table}'.");
        return 0;
    }

    /// <summary>
    /// Creates or updates a record from a JSON file of attribute values, so
    /// design-time records can be seeded and driven without the UI. JSON types
    /// map to Dataverse types: true/false to bool, whole numbers to int,
    /// everything else to string.
    /// </summary>
    public static int UpsertRecord(string[] args)
    {
        var entity = Program.Opt(args, "--entity")
            ?? throw new ArgumentException("--entity <logical name> required");
        var jsonFile = Program.Opt(args, "--jsonfile")
            ?? throw new ArgumentException("--jsonfile <path> required");
        var idOpt = Program.Opt(args, "--id");

        var values = JObject.Parse(File.ReadAllText(jsonFile));
        var record = new Entity(entity);

        foreach (var p in values.Properties())
        {
            switch (p.Value.Type)
            {
                case JTokenType.Boolean:
                    record[p.Name] = (bool)p.Value;
                    break;
                case JTokenType.Integer:
                    record[p.Name] = (int)p.Value;
                    break;
                case JTokenType.Null:
                    break;
                default:
                    record[p.Name] = p.Value.ToString();
                    break;
            }
        }

        using var client = Program.Connect(args);

        if (idOpt != null)
        {
            record.Id = Guid.Parse(idOpt);
            client.Update(record);
            Console.WriteLine($"Updated {entity} {record.Id}.");
        }
        else
        {
            var id = client.Create(record);
            Console.WriteLine($"Created {entity} {id}.");
        }
        return 0;
    }

    public static int DeleteRecord(string[] args)
    {
        var entity = Program.Opt(args, "--entity")
            ?? throw new ArgumentException("--entity <logical name> required");
        var id = Guid.Parse(Program.Opt(args, "--id")
            ?? throw new ArgumentException("--id <guid> required"));

        using var client = Program.Connect(args);
        client.Delete(entity, id);

        Console.WriteLine($"Deleted {entity} {id}.");
        return 0;
    }

    private static Guid ResolveSolutionId(
        Microsoft.PowerPlatform.Dataverse.Client.ServiceClient client, string uniqueName)
    {
        var q = new QueryExpression("solution")
        {
            ColumnSet = new ColumnSet("solutionid"),
            Criteria =
            {
                Conditions = { new ConditionExpression("uniquename", ConditionOperator.Equal, uniqueName) },
            },
        };
        var rows = client.RetrieveMultiple(q).Entities;
        if (rows.Count == 0)
            throw new InvalidOperationException($"No solution with unique name '{uniqueName}'.");
        return rows[0].Id;
    }
}
