using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Newtonsoft.Json.Linq;

namespace RestVtp.Plugins
{
    /// <summary>
    /// Handles the virtual table RetrieveMultiple event. Translates the
    /// incoming QueryExpression, calls the API, hydrates an
    /// EntityCollection into the BusinessEntityCollection output.
    /// </summary>
    public sealed class RetrieveMultiplePlugin : ProviderPluginBase
    {
        protected override void ExecuteCore(
            IServiceProvider serviceProvider,
            IPluginExecutionContext context,
            ITracingService trace,
            MappingConfig cfg)
        {
            if (!(context.InputParameters["Query"] is QueryExpression query))
                throw new InvalidPluginExecutionException(
                    "RestVtp: RetrieveMultiple received a non-QueryExpression query (FetchXML arrives pre-converted; anything else is unsupported).");

            var entityName = query.EntityName;
            var map = cfg.ForTable(entityName);
            var translated = QueryTranslator.Translate(query, map, trace);

            var collection = new EntityCollection { EntityName = entityName };

            // Primary-key equality lookup → single-record path, one-item result.
            if (translated.KeyLookup != null && !string.IsNullOrEmpty(map.GetPath))
            {
                trace.Trace($"RestVtp: RM key lookup '{translated.KeyLookup}'.");
                var path = map.GetPath.Replace("{id}", Uri.EscapeDataString(translated.KeyLookup));
                var single = HttpExecutor.Fetch(cfg, map, path, null, map.GetMethod, trace);
                if (!string.IsNullOrEmpty(map.EffectiveGetItemsPath) && single is JObject)
                {
                    var unwrapped = EntityHydrator.SelectPath(single, map.EffectiveGetItemsPath);
                    if (unwrapped is JObject) single = unwrapped;
                }
                collection.Entities.Add(EntityHydrator.Hydrate(entityName, map, single));
                context.OutputParameters["BusinessEntityCollection"] = collection;
                return;
            }

            var response = HttpExecutor.Fetch(
                cfg, map, map.ListPath, translated.QueryParams, map.Method, trace);
            var items = RetrievePlugin.ResolveItems(response, map);
            trace.Trace($"RestVtp: API returned {items.Count} items.");

            IEnumerable<JToken> page = items;

            // paging mode "none": slice locally (documented small-collection escape hatch)
            if (string.Equals(map.Paging?.Mode ?? "none", "none", StringComparison.OrdinalIgnoreCase))
                page = items.Skip((translated.PageNumber - 1) * translated.PageSize)
                            .Take(translated.PageSize);

            if (translated.TopCount.HasValue)
                page = page.Take(translated.TopCount.Value);

            foreach (var item in page)
                collection.Entities.Add(EntityHydrator.Hydrate(entityName, map, item));

            // MoreRecords heuristic: a full page implies there may be more.
            collection.MoreRecords = collection.Entities.Count >= translated.PageSize;

            context.OutputParameters["BusinessEntityCollection"] = collection;
        }
    }
}
