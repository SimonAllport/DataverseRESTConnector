using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SwaggerForge;

/// <summary>
/// Design-time companion for the RestVtp data provider.
///
///   swaggerforge generate-config &lt;swagger.json&gt; [--prefix rvtp] [--out mapping.json]
///       Parse the OpenAPI doc and emit a DRAFT mapping.json for review.
///
///   swaggerforge create-tables --config mapping.json --env https://org.crm11.dynamics.com --prefix rvtp
///       Create the virtual table metadata in Dataverse, bound to the
///       RestVtp data provider / data source (run after registering the
///       provider, see README step 3).
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            switch (args.FirstOrDefault())
            {
                case "generate-config":
                    return GenerateConfig(args.Skip(1).ToArray());
                case "create-tables":
                    return MetadataBuilder.CreateTables(args.Skip(1).ToArray());
                case "list-components":
                    return SolutionTools.ListComponents(args.Skip(1).ToArray());
                case "add-component":
                    return SolutionTools.AddComponent(args.Skip(1).ToArray());
                case "create-table":
                    return SolutionTools.CreateTable(args.Skip(1).ToArray());
                case "add-column":
                    return SolutionTools.AddColumn(args.Skip(1).ToArray());
                case "register-step":
                    return SolutionTools.RegisterStep(args.Skip(1).ToArray());
                case "delete-table":
                    return SolutionTools.DeleteTable(args.Skip(1).ToArray());
                case "upsert-record":
                    return SolutionTools.UpsertRecord(args.Skip(1).ToArray());
                case "delete-record":
                    return SolutionTools.DeleteRecord(args.Skip(1).ToArray());
                default:
                    Console.Error.WriteLine(
                        "usage:\n  swaggerforge generate-config <swagger.json> [--prefix rvtp] [--out mapping.json]\n" +
                        "  swaggerforge create-tables --config mapping.json --env <url> --prefix rvtp [--solution <name>] [--datasource <logicalname>] [--datasourcerecord <name>] [--datasourceid <guid>]\n" +
                        "  swaggerforge list-components --env <url> --solution <name>\n" +
                        "  swaggerforge add-component --env <url> --solution <name> --id <guid> --type <int> [--required]\n" +
                        "  swaggerforge delete-table --env <url> --table <logicalname>\n" +
                        "  swaggerforge delete-record --env <url> --entity <logicalname> --id <guid>");
                    return 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static int GenerateConfig(string[] args)
    {
        var swaggerPath = args.FirstOrDefault(a => !a.StartsWith("--"))
            ?? throw new ArgumentException("swagger.json path required");
        var prefix = Opt(args, "--prefix") ?? "rvtp";
        var outPath = Opt(args, "--out") ?? "mapping.json";

        var swagger = JObject.Parse(File.ReadAllText(swaggerPath));
        var tables = SwaggerParser.Parse(swagger);
        if (tables.Count == 0)
            throw new InvalidOperationException(
                "No list-style GET endpoints returning object arrays were found.");

        var baseUrl = InferBaseUrl(swagger);

        var config = new JObject
        {
            ["baseUrl"] = baseUrl,
            ["auth"] = new JObject { ["type"] = "none" },
            ["timeoutSeconds"] = 30,
            ["tables"] = new JObject(),
        };

        foreach (var t in tables)
        {
            var logical = $"{prefix}_{t.Name}";
            var sanitisedKey = new string(t.KeyField.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            var cols = new JObject();
            foreach (var (col, (src, type)) in t.Columns)
                if (!col.Equals(sanitisedKey, StringComparison.OrdinalIgnoreCase))
                    cols[$"{prefix}_{col}"] = new JObject { ["sourcePath"] = src, ["type"] = type };

            var filters = new JObject();
            foreach (var (col, param) in t.FilterParams)
                filters[$"{prefix}_{col}"] = param;

            config["tables"]![logical] = new JObject
            {
                ["listPath"] = t.ListPath,
                ["getPath"] = t.GetPath,
                ["itemsPath"] = t.ItemsPath,
                ["keyField"] = t.KeyField,
                ["keyKind"] = t.KeyKind,
                ["primaryIdAttribute"] = $"{logical}id",
                ["columns"] = cols,
                ["filterParams"] = filters,
                ["paging"] = new JObject { ["mode"] = "none", ["maxPageSize"] = 250 },
                ["sortFields"] = new JObject(),
                ["strictSort"] = false,
            };
        }

        File.WriteAllText(outPath, config.ToString(Formatting.Indented));
        Console.WriteLine($"Proposed {tables.Count} table(s): {string.Join(", ", tables.Select(t => t.Name))}");
        Console.WriteLine($"DRAFT written to {outPath}. Review keyKind, paging, auth and itemsPath before create-tables.");
        return 0;
    }

    private static string InferBaseUrl(JObject swagger)
    {
        // OpenAPI 3
        var server = swagger["servers"]?.FirstOrDefault()?["url"]?.ToString();
        if (!string.IsNullOrEmpty(server)) return server!;
        // Swagger 2
        var host = swagger["host"]?.ToString();
        if (!string.IsNullOrEmpty(host))
        {
            var scheme = swagger["schemes"]?.FirstOrDefault()?.ToString() ?? "https";
            var basePath = swagger["basePath"]?.ToString() ?? "";
            return $"{scheme}://{host}{basePath}";
        }
        return "https://REVIEW-ME.example.com";
    }

    internal static string? Opt(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    internal static bool Flag(string[] args, string name) => args.Contains(name);

    /// <summary>
    /// Connects using the cached OAuth token by default.
    ///
    /// LoginPrompt is 'Never' unless --interactive is passed. With 'Auto' the
    /// client silently opens a browser when its cached token expires and then
    /// blocks forever waiting for a sign-in nobody knows to complete — which
    /// looks exactly like a hung metadata operation. Failing fast with an
    /// actionable message is far better; pass --interactive once to sign in.
    ///
    /// For pipelines, swap the connection string for ClientSecret auth.
    /// </summary>
    internal static Microsoft.PowerPlatform.Dataverse.Client.ServiceClient Connect(string[] args)
    {
        var envUrl = Opt(args, "--env")
            ?? throw new ArgumentException("--env <environment url> required");

        var prompt = Flag(args, "--interactive") ? "Auto" : "Never";
        var connStr = $"AuthType=OAuth;Url={envUrl};RedirectUri=http://localhost;LoginPrompt={prompt};" +
                      "AppId=51f81489-12ee-4a9e-aaae-a2591f45987d"; // stock dev AppId

        var client = new Microsoft.PowerPlatform.Dataverse.Client.ServiceClient(connStr);
        if (!client.IsReady)
            throw new InvalidOperationException(
                $"Connection failed: {client.LastError}"
                + (prompt == "Never"
                    ? " (no usable cached token — re-run this command once with --interactive to sign in)"
                    : ""));
        return client;
    }
}
