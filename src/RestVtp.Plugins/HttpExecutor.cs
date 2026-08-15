using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.Xrm.Sdk;
using Newtonsoft.Json.Linq;

namespace RestVtp.Plugins
{
    /// <summary>
    /// Sandbox-safe HTTP layer. Plug-ins are stateless and short-lived, so:
    ///   - HttpClient is static (socket exhaustion protection across
    ///     executions on the same worker).
    ///   - Client-credentials tokens are cached statically with expiry;
    ///     worst case after a worker recycle is one extra token call.
    /// </summary>
    public static class HttpExecutor
    {
        private static readonly HttpClient Client = CreateClient();

        private static readonly object TokenLock = new object();
        private static string _cachedToken;
        private static DateTime _tokenExpiryUtc = DateTime.MinValue;

        private static HttpClient CreateClient()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            return new HttpClient();
        }

        /// <summary>
        /// Existing GET-only entry point, kept so callers that never need a body
        /// stay simple.
        /// </summary>
        public static JToken GetJson(
            MappingConfig cfg, string relativePath,
            IDictionary<string, string> queryParams, ITracingService trace)
        {
            return Fetch(cfg, null, relativePath, queryParams, "GET", trace);
        }

        /// <summary>
        /// Issues the call for one table, honouring its method, headers and body.
        ///
        /// With POST the translated filter/paging/sort values move out of the
        /// query string and into the body at bodyParamsPath, because an API that
        /// wants a query in the body will not read them from the URL.
        /// </summary>
        public static JToken Fetch(
            MappingConfig cfg, TableMapping map, string relativePath,
            IDictionary<string, string> queryParams, string method, ITracingService trace)
        {
            var isPost = string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase);

            // On POST the parameters travel in the body, so keep them out of the URL.
            var url = BuildUrl(cfg.BaseUrl, relativePath, isPost ? null : queryParams);
            var verb = isPost ? HttpMethod.Post : HttpMethod.Get;
            trace.Trace($"RestVtp: {verb} {url}");

            using (var request = new HttpRequestMessage(verb, url))
            {
                ApplyHeaders(cfg, map, request, trace);
                ApplyAuth(cfg.Auth, request, trace);

                if (isPost)
                {
                    var body = BuildBody(map, queryParams);
                    trace.Trace($"RestVtp: body {Truncate(body, 1000)}");
                    request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                }

                return Send(cfg, request, relativePath, trace);
            }
        }

        private static JToken Send(
            MappingConfig cfg, HttpRequestMessage request, string relativePath, ITracingService trace)
        {
                using (var cts = new System.Threading.CancellationTokenSource(
                    TimeSpan.FromSeconds(cfg.TimeoutSeconds)))
                {
                    var response = Client.SendAsync(request, cts.Token)
                        .ConfigureAwait(false).GetAwaiter().GetResult();
                    var body = response.Content.ReadAsStringAsync()
                        .ConfigureAwait(false).GetAwaiter().GetResult();

                    if (!response.IsSuccessStatusCode)
                    {
                        trace.Trace($"RestVtp: {(int)response.StatusCode} body: {Truncate(body, 2000)}");
                        throw new InvalidPluginExecutionException(
                            $"RestVtp: API returned {(int)response.StatusCode} {response.ReasonPhrase} for {relativePath}.");
                    }

                    return string.IsNullOrWhiteSpace(body) ? new JArray() : JToken.Parse(body);
                }
        }

        /// <summary>
        /// Config-level headers first, then table-level over the top, so a table
        /// can override a shared default. Auth is applied afterwards and wins.
        /// </summary>
        private static void ApplyHeaders(
            MappingConfig cfg, TableMapping map, HttpRequestMessage request, ITracingService trace)
        {
            var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (cfg.Headers != null)
                foreach (var kv in cfg.Headers) merged[kv.Key] = kv.Value;

            if (map != null && map.Headers != null)
                foreach (var kv in map.Headers) merged[kv.Key] = kv.Value;

            foreach (var kv in merged)
            {
                if (string.IsNullOrWhiteSpace(kv.Key)) continue;

                // Content headers cannot be set on the request; they belong to
                // the body and are applied when the content is created.
                if (kv.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                {
                    trace.Trace($"RestVtp: ignoring content header '{kv.Key}' on the request.");
                    continue;
                }

                if (!request.Headers.TryAddWithoutValidation(kv.Key, kv.Value))
                    trace.Trace($"RestVtp: header '{kv.Key}' was rejected by HttpClient.");
            }
        }

        /// <summary>
        /// Builds the POST body: the static template from config, with the
        /// translated parameters merged in at bodyParamsPath (root when unset).
        /// Values are written as strings, matching what the query string would
        /// have carried.
        /// </summary>
        private static string BuildBody(TableMapping map, IDictionary<string, string> queryParams)
        {
            var body = map != null && map.Body != null
                ? (JObject)map.Body.DeepClone()
                : new JObject();

            if (queryParams == null || queryParams.Count == 0)
                return body.ToString(Newtonsoft.Json.Formatting.None);

            var target = body;
            var path = map == null ? null : map.BodyParamsPath;

            if (!string.IsNullOrWhiteSpace(path))
            {
                foreach (var segment in path.Split('.'))
                {
                    var child = target[segment] as JObject;
                    if (child == null)
                    {
                        child = new JObject();
                        target[segment] = child;
                    }
                    target = child;
                }
            }

            foreach (var kv in queryParams)
                target[kv.Key] = kv.Value;

            return body.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static void ApplyAuth(AuthConfig auth, HttpRequestMessage request, ITracingService trace)
        {
            switch ((auth?.Type ?? "none").ToLowerInvariant())
            {
                case "none":
                    return;

                case "apikey":
                    request.Headers.TryAddWithoutValidation(auth.HeaderName, auth.ApiKey);
                    return;

                case "clientcredentials":
                    request.Headers.TryAddWithoutValidation(
                        "Authorization", "Bearer " + GetToken(auth, trace));
                    return;

                default:
                    throw new InvalidPluginExecutionException(
                        $"RestVtp: unknown auth type '{auth.Type}'.");
            }
        }

        private static string GetToken(AuthConfig auth, ITracingService trace)
        {
            lock (TokenLock)
            {
                if (_cachedToken != null && DateTime.UtcNow < _tokenExpiryUtc)
                    return _cachedToken;
            }

            trace.Trace("RestVtp: acquiring OAuth token (client_credentials).");
            var form = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("grant_type", "client_credentials"),
                new KeyValuePair<string, string>("client_id", auth.ClientId),
                new KeyValuePair<string, string>("client_secret", auth.ClientSecret),
            };
            if (!string.IsNullOrEmpty(auth.Scope))
                form.Add(new KeyValuePair<string, string>("scope", auth.Scope));

            var response = Client.PostAsync(auth.TokenUrl, new FormUrlEncodedContent(form))
                .ConfigureAwait(false).GetAwaiter().GetResult();
            var body = response.Content.ReadAsStringAsync()
                .ConfigureAwait(false).GetAwaiter().GetResult();

            if (!response.IsSuccessStatusCode)
                throw new InvalidPluginExecutionException(
                    $"RestVtp: token endpoint returned {(int)response.StatusCode}.");

            var json = JObject.Parse(body);
            var token = (string)json["access_token"];
            var expiresIn = (int?)json["expires_in"] ?? 300;

            lock (TokenLock)
            {
                _cachedToken = token;
                // refresh 60s early
                _tokenExpiryUtc = DateTime.UtcNow.AddSeconds(Math.Max(expiresIn - 60, 30));
            }
            return token;
        }

        private static string BuildUrl(
            string baseUrl, string relativePath, IDictionary<string, string> queryParams)
        {
            var sb = new StringBuilder();
            sb.Append(baseUrl.TrimEnd('/'));
            sb.Append('/').Append(relativePath.TrimStart('/'));

            if (queryParams != null && queryParams.Count > 0)
            {
                sb.Append('?');
                sb.Append(string.Join("&", queryParams.Select(kv =>
                    Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value ?? ""))));
            }
            return sb.ToString();
        }

        private static string Truncate(string s, int max)
            => s == null ? "" : (s.Length <= max ? s : s.Substring(0, max) + "…");
    }
}
