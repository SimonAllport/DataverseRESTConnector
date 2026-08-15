using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace RestVtp.Plugins
{
    /// <summary>
    /// Server-side port of SwaggerForge's SwaggerParser, so the K2 "DescribeSchema"
    /// step can run inside Dataverse from a pasted OpenAPI document instead of a CLI.
    ///
    /// Deliberately written for net462: no ranges, no init/required members, no
    /// nullable annotations. Keep it that way — this compiles into the sandbox
    /// plug-in assembly, not the net8.0 tool.
    ///
    /// The output is a DRAFT, exactly as the CLI's is. It always writes
    /// paging "none" and leaves filterParams empty, because neither can be
    /// inferred from a schema: whether an API honours a query parameter or
    /// silently ignores it is only knowable by calling it. That is why
    /// generation and table creation are two separate actions.
    /// </summary>
    public static class SwaggerToMapping
    {
        private sealed class ColumnDraft
        {
            public string SourcePath;
            public string Type;
        }

        private sealed class ProposedTable
        {
            public string Name;
            public string ListPath;
            public string GetPath;
            public string ItemsPath;
            public string KeyField = "id";
            public string KeyKind = "string";
            public readonly Dictionary<string, ColumnDraft> Columns =
                new Dictionary<string, ColumnDraft>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Builds a draft mapping document from an OpenAPI 2 or 3 definition.</summary>
        public static JObject Build(string swaggerJson, string prefix, string baseUrlOverride)
        {
            if (string.IsNullOrWhiteSpace(swaggerJson))
                throw new InvalidOperationException(
                    "RestVtp: no Swagger definition on the data source record to generate from.");

            JObject swagger;
            try
            {
                swagger = JObject.Parse(swaggerJson);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "RestVtp: the Swagger definition is not valid JSON: " + ex.Message);
            }

            var tables = Parse(swagger);
            if (tables.Count == 0)
                throw new InvalidOperationException(
                    "RestVtp: no list-style GET endpoints returning object arrays were found in the Swagger definition.");

            var baseUrl = !string.IsNullOrWhiteSpace(baseUrlOverride)
                ? baseUrlOverride.TrimEnd('/')
                : InferBaseUrl(swagger);

            var tablesObj = new JObject();
            foreach (var t in tables)
            {
                var logical = prefix + "_" + t.Name;
                var sanitisedKey = Sanitise(t.KeyField);

                var cols = new JObject();
                foreach (var kv in t.Columns)
                {
                    if (string.Equals(kv.Key, sanitisedKey, StringComparison.OrdinalIgnoreCase)) continue;
                    cols[prefix + "_" + kv.Key] = new JObject
                    {
                        ["sourcePath"] = kv.Value.SourcePath,
                        ["type"] = kv.Value.Type,
                    };
                }

                tablesObj[logical] = new JObject
                {
                    ["listPath"] = t.ListPath,
                    ["getPath"] = t.GetPath,
                    ["itemsPath"] = t.ItemsPath ?? "",
                    ["keyField"] = t.KeyField,
                    ["keyKind"] = t.KeyKind,
                    ["primaryIdAttribute"] = logical + "id",
                    ["columns"] = cols,
                    // Left empty on purpose: see class remarks.
                    ["filterParams"] = new JObject(),
                    ["paging"] = new JObject { ["mode"] = "none", ["maxPageSize"] = 250 },
                    ["sortFields"] = new JObject(),
                    ["strictSort"] = false,
                };
            }

            return new JObject
            {
                ["baseUrl"] = baseUrl,
                ["auth"] = new JObject { ["type"] = "none" },
                ["timeoutSeconds"] = 30,
                ["tables"] = tablesObj,
            };
        }

        private static List<ProposedTable> Parse(JObject swagger)
        {
            var result = new List<ProposedTable>();
            var paths = swagger["paths"] as JObject;
            if (paths == null)
                throw new InvalidOperationException("RestVtp: no 'paths' in the Swagger definition.");

            var isV3 = swagger["openapi"] != null;

            foreach (var pathProp in paths.Properties())
            {
                var path = pathProp.Name;
                if (path.IndexOf('{') >= 0) continue; // list endpoints only

                var get = pathProp.Value["get"];
                if (get == null) continue;

                var responseSchema = GetSuccessSchema(swagger, get, isV3);
                if (responseSchema == null) continue;

                string itemsPath;
                var itemSchema = FindItemArray(swagger, responseSchema, out itemsPath);
                if (itemSchema == null) continue;

                var segments = path.Trim('/').Split('/');
                var table = new ProposedTable
                {
                    Name = Singularise(segments[segments.Length - 1]),
                    ListPath = path,
                    ItemsPath = itemsPath,
                };

                var props = itemSchema["properties"] as JObject;
                if (props != null)
                {
                    foreach (var col in props.Properties())
                    {
                        var type = MapType(col.Value);
                        table.Columns[Sanitise(col.Name)] = new ColumnDraft
                        {
                            SourcePath = col.Name,
                            Type = type,
                        };

                        if (string.Equals(col.Name, "id", StringComparison.OrdinalIgnoreCase))
                        {
                            table.KeyField = col.Name;
                            var format = col.Value["format"] == null ? null : col.Value["format"].ToString();
                            table.KeyKind = type == "int"
                                ? "int"
                                : (format == "uuid" ? "guid" : "string");
                        }
                    }
                }

                // get-by-id sibling: /customers/{id}
                foreach (var candidate in paths.Properties())
                {
                    if (candidate.Name.StartsWith(path + "/{", StringComparison.OrdinalIgnoreCase)
                        && candidate.Value["get"] != null)
                    {
                        table.GetPath = Regex.Replace(candidate.Name, @"\{[^}]+\}", "{id}");
                        break;
                    }
                }

                result.Add(table);
            }

            return result;
        }

        private static JToken GetSuccessSchema(JObject swagger, JToken get, bool isV3)
        {
            var responses = get["responses"];
            if (responses == null) return null;
            var ok = responses["200"] ?? responses["default"];
            if (ok == null) return null;

            var schema = isV3
                ? (ok["content"] == null ? null : (ok["content"]["application/json"] == null
                    ? null : ok["content"]["application/json"]["schema"]))
                : ok["schema"];

            return Deref(swagger, schema);
        }

        private static JToken FindItemArray(JObject swagger, JToken schema, out string itemsPath)
        {
            itemsPath = null;
            if (schema == null) return null;

            var type = schema["type"] == null ? null : schema["type"].ToString();
            if (type == "array")
                return Deref(swagger, schema["items"]);

            var props = schema["properties"] as JObject;
            if (props != null)
            {
                foreach (var p in props.Properties())
                {
                    var pType = p.Value["type"] == null ? null : p.Value["type"].ToString();
                    if (pType == "array" && p.Value["items"] != null)
                    {
                        itemsPath = p.Name;
                        return Deref(swagger, p.Value["items"]);
                    }
                }
            }

            return null;
        }

        private static JToken Deref(JObject swagger, JToken schema)
        {
            var guard = 0;
            while (schema != null && schema["$ref"] != null && guard++ < 20)
            {
                var pointer = schema["$ref"].ToString().TrimStart('#', '/').Replace('/', '.');
                schema = swagger.SelectToken(pointer);
            }
            return schema;
        }

        private static string MapType(JToken prop)
        {
            var t = prop["type"] == null ? null : prop["type"].ToString();
            var f = prop["format"] == null ? null : prop["format"].ToString();

            if (t == "integer") return "int";
            if (t == "number") return f == "double" ? "double" : "decimal";
            if (t == "boolean") return "bool";
            if (t == "string" && (f == "date-time" || f == "date")) return "datetime";
            return "string";
        }

        private static string InferBaseUrl(JObject swagger)
        {
            var servers = swagger["servers"] as JArray;
            if (servers != null && servers.Count > 0 && servers[0]["url"] != null)
                return servers[0]["url"].ToString();

            var host = swagger["host"] == null ? null : swagger["host"].ToString();
            if (!string.IsNullOrEmpty(host))
            {
                var schemes = swagger["schemes"] as JArray;
                var scheme = (schemes != null && schemes.Count > 0) ? schemes[0].ToString() : "https";
                var basePath = swagger["basePath"] == null ? "" : swagger["basePath"].ToString();
                return scheme + "://" + host + basePath;
            }

            return "https://REVIEW-ME.example.com";
        }

        private static string Sanitise(string name)
        {
            var chars = name.Where(char.IsLetterOrDigit).ToArray();
            return new string(chars).ToLowerInvariant();
        }

        private static string Singularise(string s)
        {
            if (s.EndsWith("ies", StringComparison.OrdinalIgnoreCase))
                return s.Substring(0, s.Length - 3) + "y";
            if (s.EndsWith("ss", StringComparison.OrdinalIgnoreCase))
                return s;
            if (s.EndsWith("s", StringComparison.OrdinalIgnoreCase))
                return s.Substring(0, s.Length - 1);
            return s;
        }
    }
}
