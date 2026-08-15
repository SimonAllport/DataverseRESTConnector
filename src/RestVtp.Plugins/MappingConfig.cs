using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace RestVtp.Plugins
{
    /// <summary>
    /// Deserialized form of the JSON mapping document stored on the custom
    /// data source record (rvtp_mappingjson column). This is the K2
    /// "Service Instance" equivalent: everything environment- and
    /// API-specific lives here, the plug-in code stays generic.
    /// </summary>
    public sealed class MappingConfig
    {
        [JsonProperty("baseUrl")]
        public string BaseUrl { get; set; }

        [JsonProperty("auth")]
        public AuthConfig Auth { get; set; }

        /// <summary>Keyed by virtual table logical name.</summary>
        [JsonProperty("tables")]
        public Dictionary<string, TableMapping> Tables { get; set; }
            = new Dictionary<string, TableMapping>(StringComparer.OrdinalIgnoreCase);

        [JsonProperty("timeoutSeconds")]
        public int TimeoutSeconds { get; set; } = 30;

        /// <summary>
        /// Static headers sent on every request, e.g. Accept or a subscription
        /// key. Applied before auth, so an auth header of the same name wins.
        /// Table-level headers override these per table.
        /// </summary>
        [JsonProperty("headers")]
        public Dictionary<string, string> Headers { get; set; }
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static MappingConfig Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new InvalidOperationException(
                    "RestVtp: mapping JSON on the data source record is empty.");
            var cfg = JsonConvert.DeserializeObject<MappingConfig>(json);
            if (cfg.Tables == null || cfg.Tables.Count == 0)
                throw new InvalidOperationException(
                    "RestVtp: mapping JSON contains no table mappings.");
            return cfg;
        }

        public TableMapping ForTable(string entityLogicalName)
        {
            if (Tables.TryGetValue(entityLogicalName, out var t)) return t;
            throw new InvalidOperationException(
                $"RestVtp: no mapping found for table '{entityLogicalName}'.");
        }
    }

    public sealed class AuthConfig
    {
        /// <summary>none | apiKey | clientCredentials</summary>
        [JsonProperty("type")]
        public string Type { get; set; } = "none";

        // --- apiKey ---
        [JsonProperty("headerName")]
        public string HeaderName { get; set; } = "X-Api-Key";

        [JsonProperty("apiKey")]
        public string ApiKey { get; set; }

        // --- clientCredentials (OAuth2) ---
        [JsonProperty("tokenUrl")]
        public string TokenUrl { get; set; }

        [JsonProperty("clientId")]
        public string ClientId { get; set; }

        [JsonProperty("clientSecret")]
        public string ClientSecret { get; set; }

        [JsonProperty("scope")]
        public string Scope { get; set; }
    }

    public sealed class TableMapping
    {
        /// <summary>Relative path for list operations, e.g. "/customers".</summary>
        [JsonProperty("listPath")]
        public string ListPath { get; set; }

        /// <summary>
        /// Relative path for single-record fetch with {id} token,
        /// e.g. "/customers/{id}". If omitted, Retrieve falls back to
        /// listPath + key filter.
        /// </summary>
        [JsonProperty("getPath")]
        public string GetPath { get; set; }

        /// <summary>
        /// JSON path to the array of records in the list response.
        /// Empty/null means the response body IS the array.
        /// e.g. "data" or "result.items".
        /// </summary>
        [JsonProperty("itemsPath")]
        public string ItemsPath { get; set; }

        /// <summary>
        /// Path to the record inside a get-by-id response, when it differs from
        /// itemsPath. Real APIs commonly wrap a list as {"data":{"items":[...]}}
        /// but a single record as {"data":{...}} — one path cannot serve both.
        /// Defaults to itemsPath when unset.
        /// </summary>
        [JsonProperty("getItemsPath")]
        public string GetItemsPath { get; set; }

        /// <summary>Unwrap path to use for a single-record response.</summary>
        public string EffectiveGetItemsPath
        {
            get { return string.IsNullOrEmpty(GetItemsPath) ? ItemsPath : GetItemsPath; }
        }

        /// <summary>Source field holding the record's primary key.</summary>
        [JsonProperty("keyField")]
        public string KeyField { get; set; }

        /// <summary>guid | int | string: how the source key is synthesised into a Dataverse GUID.</summary>
        [JsonProperty("keyKind")]
        public string KeyKind { get; set; } = "string";

        /// <summary>Dataverse primary key column logical name, e.g. "rvtp_customerid".</summary>
        [JsonProperty("primaryIdAttribute")]
        public string PrimaryIdAttribute { get; set; }

        /// <summary>
        /// Column mappings: Dataverse logical column name -> source mapping.
        /// </summary>
        [JsonProperty("columns")]
        public Dictionary<string, ColumnMapping> Columns { get; set; }
            = new Dictionary<string, ColumnMapping>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Query-parameter names the API supports for equality filtering,
        /// keyed by Dataverse column logical name. A column absent from this
        /// dictionary is NOT filterable and the provider throws if a query
        /// filters on it (fail loudly, never silently client-side filter).
        /// </summary>
        [JsonProperty("filterParams")]
        public Dictionary<string, string> FilterParams { get; set; }
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        [JsonProperty("paging")]
        public PagingConfig Paging { get; set; }

        /// <summary>
        /// Sort support: Dataverse column -> API sort expression
        /// (e.g. "name" -> "name"). Combined with sortParam below.
        /// Empty means ordering is ignored-with-trace or thrown per strictSort.
        /// </summary>
        [JsonProperty("sortFields")]
        public Dictionary<string, string> SortFields { get; set; }
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        [JsonProperty("sortParam")]
        public string SortParam { get; set; }

        [JsonProperty("sortDescPrefix")]
        public string SortDescPrefix { get; set; } = "-";

        /// <summary>If true (default), unsupported sorts throw; if false they are ignored with a trace.</summary>
        [JsonProperty("strictSort")]
        public bool StrictSort { get; set; } = false;

        /// <summary>
        /// Headers for this table only, merged over the config-level ones.
        /// </summary>
        [JsonProperty("headers")]
        public Dictionary<string, string> Headers { get; set; }
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// HTTP method for the LIST call: GET (default) or POST. Use POST for
        /// APIs that take the query in the body, e.g. a /search endpoint.
        /// Get-by-id (getPath) is always GET — override with getMethod.
        /// </summary>
        [JsonProperty("method")]
        public string Method { get; set; } = "GET";

        /// <summary>Method for the get-by-id call; defaults to GET.</summary>
        [JsonProperty("getMethod")]
        public string GetMethod { get; set; } = "GET";

        /// <summary>
        /// Static JSON body sent with POST calls. Translated filter, paging and
        /// sort values are merged into it at bodyParamsPath, so an API that
        /// expects {"filters":{...},"page":1} is expressible without code.
        /// </summary>
        [JsonProperty("body")]
        public Newtonsoft.Json.Linq.JObject Body { get; set; }

        /// <summary>
        /// Dot-path inside body where translated parameters are written.
        /// Empty or null means merge them at the root of the body.
        /// </summary>
        [JsonProperty("bodyParamsPath")]
        public string BodyParamsPath { get; set; }
    }

    public sealed class ColumnMapping
    {
        /// <summary>Dot-notation path into each item's JSON, e.g. "address.city".</summary>
        [JsonProperty("sourcePath")]
        public string SourcePath { get; set; }

        /// <summary>string | int | decimal | bool | datetime | money | double</summary>
        [JsonProperty("type")]
        public string Type { get; set; } = "string";
    }

    public sealed class PagingConfig
    {
        /// <summary>pageNumber | skipTop | none</summary>
        [JsonProperty("mode")]
        public string Mode { get; set; } = "none";

        [JsonProperty("pageParam")]
        public string PageParam { get; set; } = "page";

        [JsonProperty("sizeParam")]
        public string SizeParam { get; set; } = "pageSize";

        [JsonProperty("skipParam")]
        public string SkipParam { get; set; } = "skip";

        [JsonProperty("topParam")]
        public string TopParam { get; set; } = "top";

        [JsonProperty("maxPageSize")]
        public int MaxPageSize { get; set; } = 250;
    }
}
