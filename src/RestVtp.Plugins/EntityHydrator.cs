using System;
using System.Globalization;
using Microsoft.Xrm.Sdk;
using Newtonsoft.Json.Linq;

namespace RestVtp.Plugins
{
    /// <summary>Turns one JSON item from the API into a Dataverse Entity.</summary>
    public static class EntityHydrator
    {
        public static Entity Hydrate(string entityLogicalName, TableMapping map, JToken item)
        {
            var entity = new Entity(entityLogicalName);

            var rawKey = SelectPath(item, map.KeyField)?.ToString();
            var id = GuidSynthesizer.ToGuid(map.KeyKind, rawKey);
            entity.Id = id;
            entity[map.PrimaryIdAttribute] = id;

            foreach (var kv in map.Columns)
            {
                var token = SelectPath(item, kv.Value.SourcePath);
                if (token == null || token.Type == JTokenType.Null) continue;
                entity[kv.Key] = Coerce(token, kv.Value.Type);
            }
            return entity;
        }

        /// <summary>Dot-notation path walk, e.g. "address.city".</summary>
        public static JToken SelectPath(JToken root, string path)
        {
            if (string.IsNullOrEmpty(path)) return root;
            var current = root;
            foreach (var segment in path.Split('.'))
            {
                if (current == null) return null;
                current = current[segment];
            }
            return current;
        }

        private static object Coerce(JToken token, string type)
        {
            switch ((type ?? "string").ToLowerInvariant())
            {
                case "int":      return token.Value<int>();
                case "decimal":  return token.Value<decimal>();
                case "double":   return token.Value<double>();
                case "money":    return new Money(token.Value<decimal>());
                case "bool":     return token.Value<bool>();
                case "datetime":
                    return DateTime.Parse(token.Value<string>(), CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
                default:         return token.Type == JTokenType.String
                                    ? token.Value<string>()
                                    : token.ToString();
            }
        }
    }
}
