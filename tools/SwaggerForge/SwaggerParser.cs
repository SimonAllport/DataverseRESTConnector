using Newtonsoft.Json.Linq;

namespace SwaggerForge;

/// <summary>
/// Heuristic OpenAPI 2/3 parser. This is the design-time half of the K2
/// broker analogy: the "DescribeSchema" that K2 gave you for free.
///
/// For each GET path that returns an array of objects it proposes a table:
///   /customers            -> list endpoint
///   /customers/{id}       -> get-by-id endpoint (matched by prefix)
/// Object schema properties become column mappings; query parameters on the
/// list operation become filterParams candidates.
///
/// Output is a DRAFT mapping.json, always review it. Heuristics get you
/// 80% there; key kind, paging mode, and itemsPath usually need a human.
/// </summary>
public sealed class ProposedTable
{
    public required string Name { get; init; }              // e.g. "customer"
    public required string ListPath { get; init; }
    public string? GetPath { get; set; }
    public string? ItemsPath { get; set; }
    public string KeyField { get; set; } = "id";
    public string KeyKind { get; set; } = "string";
    public Dictionary<string, (string SourcePath, string Type)> Columns { get; } = new();
    public Dictionary<string, string> FilterParams { get; } = new();
}

public static class SwaggerParser
{
    public static List<ProposedTable> Parse(JObject swagger)
    {
        var tables = new List<ProposedTable>();
        var paths = (JObject?)swagger["paths"]
            ?? throw new InvalidOperationException("No 'paths' in swagger document.");
        bool isV3 = swagger["openapi"] != null;

        foreach (var pathProp in paths.Properties())
        {
            var path = pathProp.Name;
            if (path.Contains('{')) continue; // list endpoints only in this pass

            var get = pathProp.Value["get"];
            if (get == null) continue;

            var responseSchema = GetSuccessSchema(swagger, get, isV3);
            if (responseSchema == null) continue;

            var (itemSchema, itemsPath) = FindItemArray(swagger, responseSchema);
            if (itemSchema == null) continue;

            var table = new ProposedTable
            {
                Name = Singularise(path.Trim('/').Split('/').Last()),
                ListPath = path,
                ItemsPath = itemsPath,
            };

            // columns from item schema
            if (itemSchema["properties"] is JObject props)
            {
                foreach (var col in props.Properties())
                {
                    var type = MapType(col.Value);
                    table.Columns[Sanitise(col.Name)] = (col.Name, type);
                    if (col.Name.Equals("id", StringComparison.OrdinalIgnoreCase))
                    {
                        table.KeyField = col.Name;
                        table.KeyKind = type == "int" ? "int"
                            : (col.Value["format"]?.ToString() == "uuid" ? "guid" : "string");
                    }
                }
            }

            // filterable query params on the list op
            if (get["parameters"] is JArray pars)
                foreach (var p in pars)
                    if (p["in"]?.ToString() == "query")
                    {
                        var pName = p["name"]!.ToString();
                        var matching = table.Columns.Keys.FirstOrDefault(
                            c => c.Equals(Sanitise(pName), StringComparison.OrdinalIgnoreCase));
                        if (matching != null) table.FilterParams[matching] = pName;
                    }

            // get-by-id sibling: /customers/{id} etc.
            var byId = paths.Properties().FirstOrDefault(pp =>
                pp.Name.StartsWith(path + "/{", StringComparison.OrdinalIgnoreCase)
                && pp.Value["get"] != null);
            if (byId != null)
                table.GetPath = System.Text.RegularExpressions.Regex.Replace(
                    byId.Name, @"\{[^}]+\}", "{id}");

            tables.Add(table);
        }
        return tables;
    }

    private static JToken? GetSuccessSchema(JObject swagger, JToken get, bool isV3)
    {
        var responses = get["responses"];
        var ok = responses?["200"] ?? responses?["default"];
        if (ok == null) return null;
        var schema = isV3
            ? ok["content"]?["application/json"]?["schema"]
            : ok["schema"];
        return Deref(swagger, schema);
    }

    /// <summary>Locate the array of items: root array, or first array-typed property (one level deep).</summary>
    private static (JToken? itemSchema, string? itemsPath) FindItemArray(JObject swagger, JToken schema)
    {
        if (schema["type"]?.ToString() == "array")
            return (Deref(swagger, schema["items"]), null);

        if (schema["properties"] is JObject props)
            foreach (var p in props.Properties())
                if (p.Value["type"]?.ToString() == "array" && p.Value["items"] != null)
                    return (Deref(swagger, p.Value["items"]), p.Name);

        return (null, null);
    }

    private static JToken? Deref(JObject swagger, JToken? schema)
    {
        while (schema?["$ref"] != null)
        {
            // "#/components/schemas/X" or "#/definitions/X" -> dot path
            var pointer = schema["$ref"]!.ToString().TrimStart('#', '/').Replace('/', '.');
            schema = swagger.SelectToken(pointer);
        }
        return schema;
    }

    private static string MapType(JToken prop)
    {
        var t = prop["type"]?.ToString();
        var f = prop["format"]?.ToString();
        return (t, f) switch
        {
            ("integer", _)          => "int",
            ("number", "double")    => "double",
            ("number", _)           => "decimal",
            ("boolean", _)          => "bool",
            ("string", "date-time") => "datetime",
            ("string", "date")      => "datetime",
            _                        => "string",
        };
    }

    private static string Sanitise(string name) =>
        new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private static string Singularise(string s) =>
        s.EndsWith("ies") ? s[..^3] + "y"
        : s.EndsWith("s") && !s.EndsWith("ss") ? s[..^1]
        : s;
}
