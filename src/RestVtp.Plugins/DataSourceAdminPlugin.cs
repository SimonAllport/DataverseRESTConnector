using System;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Newtonsoft.Json;

namespace RestVtp.Plugins
{
    /// <summary>
    /// Design-time actions driven from an API definition record, so adding an API
    /// needs no CLI: paste a Swagger definition, tick Generate Mapping, review the
    /// draft that appears, then tick Create Tables.
    ///
    /// Register on Update (post-operation, ASYNCHRONOUS) of the API definition
    /// table, filtered to the two flag columns.
    ///
    /// Asynchronous matters. Creating tables is a long sequence of metadata
    /// calls, and this plug-in catches failures to report them on the record.
    /// Inside the synchronous pipeline both are illegal — Dataverse rejects the
    /// whole operation with "ISV code reduced the open transaction count" —
    /// and the two-minute sync timeout would truncate any sizeable run. The
    /// cost is that results appear on the record a moment later rather than
    /// immediately, so refresh the form to see Last Result.
    ///
    /// It deliberately does NOT run on the data source table. Dataverse treats
    /// that table as a virtual entity and rejects custom plug-in steps against it
    /// with "Custom plugins are not allowed for Virtual Entity", so the authoring
    /// record has to be an ordinary table that writes through to the data source
    /// record on its behalf.
    ///
    /// Generation and creation are two steps on purpose. A Swagger document
    /// cannot say whether an API honours a query parameter or silently ignores
    /// it, so filterParams and paging remain guesses until a human confirms them
    /// against the live API. Creating columns straight from a draft would bake
    /// those guesses into schema that is painful to change.
    /// </summary>
    public sealed class DataSourceAdminPlugin : IPlugin
    {
        public const string SwaggerColumn = "rvtp_swaggerjson";
        public const string BaseUrlColumn = "rvtp_baseurl";
        public const string MappingColumn = "rvtp_mappingjson";
        public const string GenerateFlag = "rvtp_generatemapping";
        public const string CreateFlag = "rvtp_createtables";
        public const string ResultColumn = "rvtp_lastresult";
        public const string SolutionColumn = "rvtp_solutionname";
        public const string DataSourceNameColumn = "rvtp_datasourcename";

        public void Execute(IServiceProvider serviceProvider)
        {
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var trace = (ITracingService)serviceProvider.GetService(typeof(ITracingService));
            var factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
            var service = factory.CreateOrganizationService(context.UserId);

            // We write back to the same record, which re-fires this step.
            if (context.Depth > 1) return;

            var target = context.InputParameters.Contains("Target")
                ? context.InputParameters["Target"] as Entity
                : null;
            if (target == null) return;

            var wantGenerate = GetFlag(target, GenerateFlag);
            var wantCreate = GetFlag(target, CreateFlag);
            if (!wantGenerate && !wantCreate) return;

            // Target carries only changed columns; read the rest.
            var record = service.Retrieve(context.PrimaryEntityName, context.PrimaryEntityId,
                new ColumnSet(SwaggerColumn, BaseUrlColumn, MappingColumn,
                              SolutionColumn, DataSourceNameColumn));

            var prefix = PrefixOf(context.PrimaryEntityName);
            var update = new Entity(context.PrimaryEntityName, context.PrimaryEntityId);
            string result;

            try
            {
                result = wantGenerate
                    ? Generate(record, prefix, update, trace)
                    : Create(service, record, prefix, trace);
            }
            catch (Exception ex)
            {
                // Report failures on the record, not only as a dialog, so the
                // reason survives long enough to act on.
                trace.Trace("RestVtp admin: " + ex);
                var failure = new Entity(context.PrimaryEntityName, context.PrimaryEntityId);
                failure[ResultColumn] = Stamp("FAILED") + Environment.NewLine + ex.Message;
                ClearFlags(failure, wantGenerate, wantCreate);
                service.Update(failure);
                throw new InvalidPluginExecutionException(ex.Message, ex);
            }

            update[ResultColumn] = result;
            ClearFlags(update, wantGenerate, wantCreate);
            service.Update(update);
        }

        private static string Generate(Entity record, string prefix, Entity update, ITracingService trace)
        {
            var swagger = record.GetAttributeValue<string>(SwaggerColumn);
            var baseUrl = record.GetAttributeValue<string>(BaseUrlColumn);

            var draft = SwaggerToMapping.Build(swagger, prefix, baseUrl);
            update[MappingColumn] = draft.ToString(Formatting.Indented);

            var tables = draft["tables"] as Newtonsoft.Json.Linq.JObject;
            var count = tables == null ? 0 : tables.Count;
            trace.Trace("RestVtp admin: generated draft for " + count + " table(s).");

            return Stamp("Generated mapping draft")
                + Environment.NewLine + count + " table(s) proposed."
                + Environment.NewLine
                + Environment.NewLine + "REVIEW BEFORE CREATING TABLES:"
                + Environment.NewLine + "- paging is always written as \"none\"; set it if the API pages."
                + Environment.NewLine + "- filterParams is empty; add only parameters you have confirmed"
                + Environment.NewLine + "  actually filter. A parameter the API ignores returns everything,"
                + Environment.NewLine + "  and Dataverse would present that as a filtered result."
                + Environment.NewLine + "- only top-level properties are mapped; add nested dot-paths by hand."
                + Environment.NewLine + "- the first string column becomes the table's primary name.";
        }

        private static string Create(
            IOrganizationService service, Entity record, string prefix, ITracingService trace)
        {
            var mapping = record.GetAttributeValue<string>(MappingColumn);
            var doc = MappingConfigDocument.Parse(mapping);

            var dataSourceEntity = prefix + "_restdatasource";
            var dataSourceName = record.GetAttributeValue<string>(DataSourceNameColumn);
            var dataSource = ResolveDataSource(service, dataSourceEntity, dataSourceName);

            // The runtime reads the mapping from the data source record, never
            // from here, so push it across before the tables start resolving.
            // Merge rather than replace: one data source record serves every
            // table defined against it, and several API definition records may
            // contribute to it. Overwriting would silently break every table
            // this record did not define.
            var existing = service.Retrieve(dataSourceEntity, dataSource.Id,
                new ColumnSet(ProviderPluginBase.MappingColumn))
                .GetAttributeValue<string>(ProviderPluginBase.MappingColumn);

            string mergeNote;
            var merged = MergeMapping(existing, mapping, out mergeNote);

            var push = new Entity(dataSourceEntity, dataSource.Id);
            push[ProviderPluginBase.MappingColumn] = merged;
            service.Update(push);

            var solution = record.GetAttributeValue<string>(SolutionColumn);
            var log = TableForge.CreateTables(
                service, doc, dataSourceEntity, dataSource.Id, prefix, solution, trace);

            return Stamp("Created tables")
                + Environment.NewLine + mergeNote
                + Environment.NewLine + log;
        }

        /// <summary>
        /// Folds this definition's tables into whatever the data source record
        /// already holds. Tables of the same name are replaced; everything else
        /// is left alone.
        /// </summary>
        /// <summary>
        /// Dataverse serialises a data source record's custom fields into the
        /// 'connectiondefinition' attribute of entitydatasource, capped at 4000
        /// characters — regardless of the mapping column's own declared length.
        ///
        /// The mapping does not get all 4000. It is stored escaped and alongside
        /// every other field on the record, and escaping roughly doubles JSON.
        /// Measured against a live environment: 1297 characters saved, 1767 was
        /// rejected. 1500 is therefore the practical ceiling, checked here so the
        /// failure names the real cause rather than surfacing Dataverse's
        /// message about an attribute nobody knew existed.
        ///
        /// Everything written back is compact rather than indented for the same
        /// reason: pretty-printing spends the budget for no benefit, since the
        /// record is not where the mapping is edited.
        /// </summary>
        private const int ConnectionDefinitionLimit = 1500;

        private static string MergeMapping(string existingJson, string incomingJson, out string note)
        {
            if (string.IsNullOrWhiteSpace(existingJson))
            {
                note = "Data source record had no mapping; wrote this definition's.";
                return Compact(incomingJson);
            }

            Newtonsoft.Json.Linq.JObject existing;
            try
            {
                existing = Newtonsoft.Json.Linq.JObject.Parse(existingJson);
            }
            catch
            {
                // Never destroy a document we cannot understand.
                throw new InvalidPluginExecutionException(
                    "RestVtp: the data source record's existing Mapping JSON is not valid JSON, "
                    + "so it cannot be merged into. Fix or clear it first.");
            }

            var incoming = Newtonsoft.Json.Linq.JObject.Parse(incomingJson);

            // A data source record carries one baseUrl and one set of credentials
            // for every table on it, so two different APIs cannot share one.
            var exBase = (string)existing["baseUrl"];
            var inBase = (string)incoming["baseUrl"];
            if (!string.IsNullOrEmpty(exBase) && !string.IsNullOrEmpty(inBase)
                && !string.Equals(exBase.TrimEnd('/'), inBase.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidPluginExecutionException(
                    "RestVtp: this data source record points at '" + exBase
                    + "' but this definition uses '" + inBase
                    + "'. A data source record holds one baseUrl and one set of credentials for all "
                    + "its tables, so a different API needs its own data source record.");
            }

            var exTables = existing["tables"] as Newtonsoft.Json.Linq.JObject;
            if (exTables == null)
            {
                exTables = new Newtonsoft.Json.Linq.JObject();
                existing["tables"] = exTables;
            }

            var added = 0;
            var replaced = 0;
            var inTables = incoming["tables"] as Newtonsoft.Json.Linq.JObject;
            if (inTables != null)
            {
                foreach (var p in inTables.Properties())
                {
                    if (exTables[p.Name] != null) replaced++; else added++;
                    exTables[p.Name] = p.Value;
                }
            }

            var result = existing.ToString(Formatting.None);

            if (result.Length > ConnectionDefinitionLimit)
                throw new InvalidPluginExecutionException(
                    "RestVtp: the merged mapping is " + result.Length + " characters, over the "
                    + ConnectionDefinitionLimit + "-character ceiling Dataverse imposes on a data source "
                    + "record (it packs the record's fields into entitydatasource.connectiondefinition). "
                    + "Trim columns you do not need, shorten sourcePaths, or split these tables across "
                    + "separate data source records.");

            note = "Merged into data source record: " + added + " table(s) added, "
                 + replaced + " replaced, " + (exTables.Count - added - replaced) + " left untouched. "
                 + "Mapping is now " + result.Length + " of " + ConnectionDefinitionLimit + " characters.";
            return result;
        }

        private static string Compact(string json)
        {
            return Newtonsoft.Json.Linq.JObject.Parse(json).ToString(Formatting.None);
        }

        private static Entity ResolveDataSource(
            IOrganizationService service, string dataSourceEntity, string name)
        {
            var q = new QueryExpression(dataSourceEntity) { ColumnSet = new ColumnSet(false) };
            if (!string.IsNullOrWhiteSpace(name))
                q.Criteria.AddCondition(PrefixOf(dataSourceEntity) + "_name", ConditionOperator.Equal, name);

            var rows = service.RetrieveMultiple(q).Entities;

            if (rows.Count == 1) return rows[0];

            if (rows.Count == 0)
                throw new InvalidPluginExecutionException(
                    "RestVtp: no data source record found in '" + dataSourceEntity + "'"
                    + (string.IsNullOrWhiteSpace(name) ? "" : " named '" + name + "'")
                    + ". Create one first, then set Data Source Name on this record.");

            throw new InvalidPluginExecutionException(
                "RestVtp: " + rows.Count + " data source records exist. "
                + "Set Data Source Name on this record to choose one.");
        }

        private static bool GetFlag(Entity target, string name)
        {
            return target.Contains(name) && target.GetAttributeValue<bool>(name);
        }

        private static void ClearFlags(Entity update, bool generate, bool create)
        {
            if (generate) update[GenerateFlag] = false;
            if (create) update[CreateFlag] = false;
        }

        private static string PrefixOf(string entityLogicalName)
        {
            var i = entityLogicalName.IndexOf('_');
            return i > 0 ? entityLogicalName.Substring(0, i) : "rvtp";
        }

        private static string Stamp(string what)
        {
            return what + " at " + DateTime.UtcNow.ToString("u") + ".";
        }
    }
}
