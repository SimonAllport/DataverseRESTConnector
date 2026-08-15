using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Newtonsoft.Json.Linq;

namespace RestVtp.Plugins
{
    /// <summary>
    /// Server-side port of SwaggerForge's MetadataBuilder: creates the virtual
    /// tables described by a mapping document, using the plug-in's own
    /// IOrganizationService rather than an external CLI connection.
    ///
    /// Every constraint learned the hard way against a live environment is
    /// encoded here. A virtual table needs BOTH DataProviderId (which code runs)
    /// and DataSourceId (which config record that code receives); it must have an
    /// ExternalName; and ValidateVirtualEntityCreate rejects, one refusal at a
    /// time, every capability that assumes locally stored rows.
    /// </summary>
    public static class TableForge
    {
        public static string CreateTables(
            IOrganizationService service,
            MappingConfigDocument doc,
            string dataSourceEntity,
            Guid dataSourceId,
            string prefix,
            string solutionUniqueName,
            ITracingService trace)
        {
            var providerId = ResolveDataProviderId(service, dataSourceEntity);
            trace.Trace("RestVtp: data provider " + providerId + ", data source " + dataSourceId);

            var log = new StringBuilder();
            var created = 0;
            var skipped = 0;

            foreach (var pair in doc.Tables)
            {
                var logical = pair.Key;
                var t = pair.Value;

                if (TableExists(service, logical))
                {
                    log.AppendLine("Skipped " + logical + ": already exists.");
                    skipped++;
                    continue;
                }

                var columns = t["columns"] as JObject ?? new JObject();
                var primaryName = FirstStringColumn(columns, logical);
                var externalName = logical.StartsWith(prefix + "_", StringComparison.OrdinalIgnoreCase)
                    ? logical.Substring(prefix.Length + 1)
                    : logical;

                var entity = new EntityMetadata
                {
                    SchemaName = logical,
                    LogicalName = logical,
                    ExternalName = externalName,
                    ExternalCollectionName = externalName + "s",
                    DisplayName = Label(ToDisplay(externalName)),
                    DisplayCollectionName = Label(ToDisplay(externalName) + "s"),
                    OwnershipType = OwnershipTypes.OrganizationOwned,
                    IsActivity = false,
                    DataProviderId = providerId,
                    DataSourceId = dataSourceId,
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

                var createReq = new CreateEntityRequest
                {
                    Entity = entity,
                    HasActivities = false,
                    HasNotes = false,
                    PrimaryAttribute = new StringAttributeMetadata
                    {
                        SchemaName = primaryName,
                        LogicalName = primaryName,
                        RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None),
                        MaxLength = 400,
                        DisplayName = Label("Name"),
                        ExternalName = SourcePath(columns, primaryName),
                    },
                };
                if (!string.IsNullOrEmpty(solutionUniqueName))
                    createReq.SolutionUniqueName = solutionUniqueName;

                service.Execute(createReq);

                foreach (var col in columns.Properties())
                {
                    if (string.Equals(col.Name, primaryName, StringComparison.OrdinalIgnoreCase)) continue;

                    var addReq = new CreateAttributeRequest
                    {
                        EntityName = logical,
                        Attribute = BuildAttribute(col.Name, col.Value as JObject),
                    };
                    if (!string.IsNullOrEmpty(solutionUniqueName))
                        addReq.SolutionUniqueName = solutionUniqueName;

                    service.Execute(addReq);
                }

                log.AppendLine("Created " + logical + " with " + columns.Count + " column(s).");
                created++;
            }

            log.AppendLine();
            log.AppendLine(created + " table(s) created, " + skipped + " skipped.");
            return log.ToString();
        }

        /// <summary>
        /// Existence check that does not rely on catching an exception.
        /// RetrieveEntityRequest throws when the table is absent, and swallowing
        /// that inside a plug-in trips "ISV code reduced the open transaction
        /// count". A metadata query returns an empty result instead.
        /// </summary>
        private static bool TableExists(IOrganizationService service, string logicalName)
        {
            var query = new Microsoft.Xrm.Sdk.Metadata.Query.EntityQueryExpression
            {
                Criteria = new Microsoft.Xrm.Sdk.Metadata.Query.MetadataFilterExpression(LogicalOperator.And)
                {
                    Conditions =
                    {
                        new Microsoft.Xrm.Sdk.Metadata.Query.MetadataConditionExpression(
                            "LogicalName",
                            Microsoft.Xrm.Sdk.Metadata.Query.MetadataConditionOperator.Equals,
                            logicalName),
                    },
                },
                Properties = new Microsoft.Xrm.Sdk.Metadata.Query.MetadataPropertiesExpression("LogicalName"),
            };

            var response = (RetrieveMetadataChangesResponse)service.Execute(
                new RetrieveMetadataChangesRequest { Query = query });

            return response.EntityMetadata != null && response.EntityMetadata.Count > 0;
        }

        private static Guid ResolveDataProviderId(IOrganizationService service, string dataSourceEntity)
        {
            var q = new QueryExpression("entitydataprovider")
            {
                ColumnSet = new ColumnSet("entitydataproviderid"),
            };
            q.Criteria.AddCondition("datasourcelogicalname", ConditionOperator.Equal, dataSourceEntity);

            var rows = service.RetrieveMultiple(q).Entities;
            if (rows.Count == 0)
                throw new InvalidPluginExecutionException(
                    "RestVtp: no data provider registered against data source entity '" + dataSourceEntity + "'.");
            return rows[0].Id;
        }

        /// <summary>
        /// The first string column becomes the table's primary name field, so the
        /// order of "columns" in the mapping document is significant.
        /// </summary>
        private static string FirstStringColumn(JObject columns, string logical)
        {
            foreach (var c in columns.Properties())
            {
                var type = c.Value["type"] == null ? "string" : c.Value["type"].ToString();
                if (string.Equals(type, "string", StringComparison.OrdinalIgnoreCase))
                    return c.Name;
            }
            return logical + "_name";
        }

        private static string SourcePath(JObject columns, string columnName)
        {
            var col = columns[columnName];
            return col == null || col["sourcePath"] == null ? null : col["sourcePath"].ToString();
        }

        private static AttributeMetadata BuildAttribute(string logical, JObject col)
        {
            var external = col == null || col["sourcePath"] == null ? null : col["sourcePath"].ToString();
            var type = col == null || col["type"] == null ? "string" : col["type"].ToString();
            var display = Label(ToDisplay(StripPrefix(logical)));

            switch (type.ToLowerInvariant())
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
                        SchemaName = logical,
                        LogicalName = logical,
                        DisplayName = display,
                        ExternalName = external,
                        OptionSet = new BooleanOptionSetMetadata(
                            new OptionMetadata(Label("Yes"), 1), new OptionMetadata(Label("No"), 0)),
                    };
                case "datetime":
                    return new DateTimeAttributeMetadata
                    {
                        SchemaName = logical,
                        LogicalName = logical,
                        DisplayName = display,
                        ExternalName = external,
                        Format = DateTimeFormat.DateAndTime,
                    };
                case "memo":
                    // Text beyond the 850-char ceiling of a string column.
                    return new MemoAttributeMetadata
                    {
                        SchemaName = logical,
                        LogicalName = logical,
                        DisplayName = display,
                        ExternalName = external,
                        MaxLength = 100000,
                        Format = StringFormat.TextArea,
                    };
                default:
                    return new StringAttributeMetadata
                    { SchemaName = logical, LogicalName = logical, DisplayName = display, ExternalName = external, MaxLength = 850 };
            }
        }

        private static string StripPrefix(string logical)
        {
            var i = logical.IndexOf('_');
            return i > 0 && i < logical.Length - 1 ? logical.Substring(i + 1) : logical;
        }

        private static string ToDisplay(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return char.ToUpperInvariant(s[0]) + s.Substring(1);
        }

        private static Label Label(string text)
        {
            return new Label(text, 1033);
        }
    }

    /// <summary>Thin view over the mapping document for table iteration.</summary>
    public sealed class MappingConfigDocument
    {
        public readonly Dictionary<string, JObject> Tables =
            new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);

        public static MappingConfigDocument Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new InvalidPluginExecutionException(
                    "RestVtp: the Mapping JSON on this record is empty. Generate it first.");

            JObject root;
            try
            {
                root = JObject.Parse(json);
            }
            catch (Exception ex)
            {
                throw new InvalidPluginExecutionException(
                    "RestVtp: the Mapping JSON on this record is not valid JSON: " + ex.Message);
            }

            var tables = root["tables"] as JObject;
            if (tables == null || !tables.HasValues)
                throw new InvalidPluginExecutionException(
                    "RestVtp: the Mapping JSON contains no table mappings.");

            var doc = new MappingConfigDocument();
            foreach (var p in tables.Properties())
                doc.Tables[p.Name] = p.Value as JObject;
            return doc;
        }
    }
}
