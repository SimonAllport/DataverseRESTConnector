using System;
using System.Collections.Generic;
using Microsoft.Xrm.Sdk;
using Newtonsoft.Json.Linq;

namespace RestVtp.Plugins
{
    /// <summary>
    /// Handles the virtual table Retrieve event: reverse the GUID back to
    /// the source key, hit the API's get-by-id path (or list+filter
    /// fallback), hydrate a single Entity into the Target output.
    /// </summary>
    public sealed class RetrievePlugin : ProviderPluginBase
    {
        protected override void ExecuteCore(
            IServiceProvider serviceProvider,
            IPluginExecutionContext context,
            ITracingService trace,
            MappingConfig cfg)
        {
            var entityName = context.PrimaryEntityName;
            var map = cfg.ForTable(entityName);
            var rawKey = GuidSynthesizer.FromGuid(map.KeyKind, context.PrimaryEntityId);
            trace.Trace($"RestVtp: Retrieve {entityName} key='{rawKey}'.");

            JToken item;
            if (!string.IsNullOrEmpty(map.GetPath))
            {
                var path = map.GetPath.Replace("{id}", Uri.EscapeDataString(rawKey));
                item = HttpExecutor.Fetch(cfg, map, path, null, map.GetMethod, trace);
            }
            else
            {
                // Fallback: list endpoint filtered on the key param.
                if (!map.FilterParams.TryGetValue(map.PrimaryIdAttribute, out var keyParam))
                    throw new InvalidPluginExecutionException(
                        $"RestVtp: table '{entityName}' has no getPath and no key filter param, so Retrieve is impossible.");

                var listResult = HttpExecutor.Fetch(cfg, map, map.ListPath,
                    new Dictionary<string, string> { [keyParam] = rawKey }, map.Method, trace);
                var items = ResolveItems(listResult, map);
                if (items.Count == 0)
                    throw new InvalidPluginExecutionException(
                        $"RestVtp: record with key '{rawKey}' not found.");
                item = items[0];
            }

            // Some APIs wrap single records too, e.g. { "data": {...} }, often at
            // a different path from the list wrapper.
            if (!string.IsNullOrEmpty(map.EffectiveGetItemsPath) && item is JObject)
            {
                var unwrapped = EntityHydrator.SelectPath(item, map.EffectiveGetItemsPath);
                if (unwrapped is JObject) item = unwrapped;
            }

            context.OutputParameters["BusinessEntity"] =
                EntityHydrator.Hydrate(entityName, map, item);
        }

        internal static JArray ResolveItems(JToken response, TableMapping map)
        {
            var token = string.IsNullOrEmpty(map.ItemsPath)
                ? response
                : EntityHydrator.SelectPath(response, map.ItemsPath);

            if (token is JArray arr) return arr;
            throw new InvalidPluginExecutionException(
                $"RestVtp: expected a JSON array at itemsPath '{map.ItemsPath ?? "(root)"}' but got {token?.Type.ToString() ?? "null"}.");
        }
    }
}
