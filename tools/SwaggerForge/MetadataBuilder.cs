using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Newtonsoft.Json.Linq;

namespace SwaggerForge;

/// <summary>
/// Creates the virtual tables in Dataverse from a reviewed mapping.json,
/// bound to the RestVtp data provider. Assumes the provider and a data
/// source record already exist (README steps 2 and 3).
///
/// Auth: interactive browser login via Dataverse.Client's OAuth. For
/// pipelines swap the connection string for ClientSecret auth.
/// </summary>
internal static class MetadataBuilder
{
    public static int CreateTables(string[] args)
    {
        var configPath = Program.Opt(args, "--config") ?? "mapping.json";
        var prefix = Program.Opt(args, "--prefix") ?? "rvtp";
        var solution = Program.Opt(args, "--solution");
        var dataSourceEntity = Program.Opt(args, "--datasource") ?? $"{prefix}_restdatasource";
        var dataSourceRecord = Program.Opt(args, "--datasourcerecord");
        var dataSourceIdOpt = Program.Opt(args, "--datasourceid");

        var config = JObject.Parse(File.ReadAllText(configPath));
        var tables = (JObject)config["tables"]!;

        using var client = Program.Connect(args);

        // Resolve the data provider registered for our plug-ins by its data source entity.
        var providerId = ResolveDataProviderId(client, dataSourceEntity);
        Console.WriteLine($"Data provider: {providerId}");

        // A virtual table needs BOTH: the provider selects which code runs, the
        // data source selects which config record that code receives. Setting
        // only DataProviderId leaves the table with no config, and the first
        // query fails with "no data source record available in context".
        var dataSourceId = ResolveDataSourceId(client, dataSourceEntity, dataSourceRecord, dataSourceIdOpt);
        Console.WriteLine($"Data source record: {dataSourceId}");

        foreach (var prop in tables.Properties())
        {
            var logical = prop.Name;
            var t = (JObject)prop.Value;

            // Adding one table to an existing config and re-running is the
            // normal workflow, so tables that already exist are skipped rather
            // than aborting the run on the first "already exists" fault.
            if (TableExists(client, logical))
            {
                Console.WriteLine($"Skipping {logical}: already exists (delete-table first to recreate).");
                continue;
            }

            Console.Write($"Creating {logical} ... ");

            // Dataverse mandates an External Name on virtual entities. RestVtp
            // never reads it — the API shape comes from mapping.json — but
            // ValidateEntityExternalNameForCreate rejects the create without one.
            var externalName = logical.StartsWith(prefix + "_") ? logical[(prefix.Length + 1)..] : logical;

            var entity = new EntityMetadata
            {
                SchemaName = logical,
                LogicalName = logical,
                ExternalName = externalName,
                ExternalCollectionName = externalName + "s",
                DisplayName = Label(ToDisplay(logical, prefix)),
                DisplayCollectionName = Label(ToDisplay(logical, prefix) + "s"),
                OwnershipType = OwnershipTypes.OrganizationOwned,
                IsActivity = false,
                DataProviderId = providerId,
                DataSourceId = dataSourceId,
                // Virtual entities have no local storage, so the whole family of
                // capabilities that assumes stored rows is rejected by
                // ValidateVirtualEntityCreate. Several default to enabled for new
                // entities and must be turned off explicitly, one refusal at a time,
                // or CreateEntity fails validation.
                ChangeTrackingEnabled = false,
                CanChangeTrackingBeEnabled = new BooleanManagedProperty(false),
                IsAuditEnabled = new BooleanManagedProperty(false),
                CanCreateCharts = new BooleanManagedProperty(false),
                IsConnectionsEnabled = new BooleanManagedProperty(false),
                IsDuplicateDetectionEnabled = new BooleanManagedProperty(false),
                IsMailMergeEnabled = new BooleanManagedProperty(false),
                IsBusinessProcessEnabled = false,
                IsQuickCreateEnabled = false,
            };

            // Primary name column: first string column, else the key rendered as text.
            var primaryNameLogical = ((JObject?)t["columns"])?.Properties()
                .FirstOrDefault(c => (string?)c.Value["type"] == "string")?.Name
                ?? $"{logical}_name";

            var createReq = new CreateEntityRequest
            {
                Entity = entity,
                HasActivities = false,
                HasNotes = false,
                PrimaryAttribute = new StringAttributeMetadata
                {
                    SchemaName = primaryNameLogical,
                    LogicalName = primaryNameLogical,
                    RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None),
                    MaxLength = 400,
                    DisplayName = Label("Name"),
                    ExternalName = (string?)t["columns"]?[primaryNameLogical]?["sourcePath"],
                },
            };
            if (solution != null) createReq.SolutionUniqueName = solution;
            client.Execute(createReq);

            // Remaining columns
            foreach (var col in ((JObject)t["columns"]!).Properties()
                         .Where(c => c.Name != primaryNameLogical))
            {
                var attr = BuildAttribute(col.Name, (JObject)col.Value);
                var addReq = new CreateAttributeRequest { EntityName = logical, Attribute = attr };
                if (solution != null) addReq.SolutionUniqueName = solution;
                client.Execute(addReq);
            }

            Console.WriteLine("done.");
        }

        Console.WriteLine("All tables created. Verify the data source record's mapping JSON matches this config, then open a table in a model-driven app to smoke test.");
        return 0;
    }

    private static Guid ResolveDataProviderId(ServiceClient client, string dataSourceEntity)
    {
        var q = new QueryExpression("entitydataprovider")
        {
            ColumnSet = new ColumnSet("entitydataproviderid", "datasourcelogicalname", "name"),
            Criteria = { Conditions = { new ConditionExpression(
                "datasourcelogicalname", ConditionOperator.Equal, dataSourceEntity) } },
        };
        var rows = client.RetrieveMultiple(q);
        if (rows.Entities.Count == 0)
            throw new InvalidOperationException(
                $"No data provider found with data source entity '{dataSourceEntity}'. " +
                "Register the provider first (README step 2).");
        return rows.Entities[0].Id;
    }

    private static bool TableExists(ServiceClient client, string logicalName)
    {
        try
        {
            client.Execute(new RetrieveEntityRequest
            {
                LogicalName = logicalName,
                EntityFilters = EntityFilters.Entity,
            });
            return true;
        }
        catch (System.ServiceModel.FaultException<OrganizationServiceFault>)
        {
            return false;
        }
    }

    /// <summary>
    /// Finds the data source record (the K2 "service instance" holding baseUrl,
    /// auth and the mapping JSON) whose id every created table must point at.
    /// </summary>
    private static Guid ResolveDataSourceId(
        ServiceClient client, string dataSourceEntity, string? recordName, string? explicitId)
    {
        if (!string.IsNullOrWhiteSpace(explicitId))
            return Guid.Parse(explicitId);

        var md = (RetrieveEntityResponse)client.Execute(new RetrieveEntityRequest
        {
            LogicalName = dataSourceEntity,
            EntityFilters = EntityFilters.Entity,
        });
        var nameAttr = md.EntityMetadata.PrimaryNameAttribute;

        var q = new QueryExpression(dataSourceEntity) { ColumnSet = new ColumnSet(nameAttr) };
        if (!string.IsNullOrWhiteSpace(recordName))
            q.Criteria.AddCondition(nameAttr, ConditionOperator.Equal, recordName);

        var rows = client.RetrieveMultiple(q).Entities;
        if (rows.Count == 1) return rows[0].Id;

        if (rows.Count == 0)
            throw new InvalidOperationException(
                $"No data source record found in '{dataSourceEntity}'"
                + (recordName is null ? "" : $" named '{recordName}'")
                + ". Create one holding the mapping JSON first (README step 3).");

        var names = string.Join(", ", rows.Select(r => r.GetAttributeValue<string>(nameAttr)));
        throw new InvalidOperationException(
            $"{rows.Count} data source records exist ({names}). "
            + "Pass --datasourcerecord <name> or --datasourceid <guid> to choose one.");
    }

    internal static AttributeMetadata BuildAttribute(string logical, JObject col)
    {
        var external = (string?)col["sourcePath"];
        var display = Label(ToDisplay(logical, logical.Split('_')[0]));
        switch ((string?)col["type"] ?? "string")
        {
            case "int":
                return new IntegerAttributeMetadata
                { SchemaName = logical, LogicalName = logical, DisplayName = display, ExternalName = external };
            case "decimal":
                return new DecimalAttributeMetadata
                { SchemaName = logical, LogicalName = logical, DisplayName = display, ExternalName = external, Precision = 2 };
            case "double":
                return new DoubleAttributeMetadata
                { SchemaName = logical, LogicalName = logical, DisplayName = display, ExternalName = external };
            case "money":
                return new MoneyAttributeMetadata
                { SchemaName = logical, LogicalName = logical, DisplayName = display, ExternalName = external };
            case "bool":
                return new BooleanAttributeMetadata
                {
                    SchemaName = logical, LogicalName = logical, DisplayName = display, ExternalName = external,
                    OptionSet = new BooleanOptionSetMetadata(
                        new OptionMetadata(Label("Yes"), 1), new OptionMetadata(Label("No"), 0)),
                };
            case "datetime":
                return new DateTimeAttributeMetadata
                { SchemaName = logical, LogicalName = logical, DisplayName = display, ExternalName = external, Format = DateTimeFormat.DateAndTime };
            case "memo":
                // Text beyond the 850-char ceiling of a string column. Worth
                // reaching for whenever the API returns descriptions or bodies.
                return new MemoAttributeMetadata
                {
                    SchemaName = logical, LogicalName = logical, DisplayName = display, ExternalName = external,
                    MaxLength = (int?)col["maxLength"] ?? 100000,
                    Format = StringFormat.TextArea,
                };
            default:
                return new StringAttributeMetadata
                {
                    SchemaName = logical, LogicalName = logical, DisplayName = display, ExternalName = external,
                    MaxLength = (int?)col["maxLength"] ?? 850,
                };
        }
    }

    private static Microsoft.Xrm.Sdk.Label Label(string text) =>
        new(text, 1033);

    private static string ToDisplay(string logical, string prefix)
    {
        var stripped = logical.StartsWith(prefix + "_") ? logical[(prefix.Length + 1)..] : logical;
        return char.ToUpperInvariant(stripped[0]) + stripped[1..];
    }
}
