# RestVtp: Generic REST Virtual Table Data Provider for Dataverse

Swagger in, virtual tables out. One generic data provider + a config document
per API, instead of a bespoke plug-in per integration. The K2 analogy holds
throughout:

| K2 | RestVtp |
|---|---|
| Service Type (broker assembly) | `RestVtp.Plugins` registered as a Data Provider |
| DescribeSchema | `SwaggerForge` (design-time metadata generation) |
| Service Instance | Data source record holding `mapping.json` |
| Service Object | Virtual table |
| Execute | `RetrievePlugin` / `RetrieveMultiplePlugin` |

## Repo layout

```
src/RestVtp.Plugins/        Runtime provider (net462, sandbox)
  ProviderPluginBase.cs     Service resolution + config load from data source
  RetrievePlugin.cs         Get-by-id (GUID -> source key -> API)
  RetrieveMultiplePlugin.cs QueryExpression -> API list call
  QueryTranslator.cs        Filter/sort/paging translation (fails loudly)
  GuidSynthesizer.cs        Reversible key<->GUID embedding
  EntityHydrator.cs         JSON -> Entity with dot-path mapping
  HttpExecutor.cs           Static HttpClient, apiKey + client_credentials
tools/SwaggerForge/         Design-time CLI (net8.0)
config/mapping.example.json Full config shape reference
```

## v1 scope contract (deliberate)

- **Read-only.** No Create/Update/Delete.
- **Equality filters only**, on columns listed in `filterParams`. Anything
  else (OR filters, quick find, `like`, ranges) throws with a clear message
  rather than silently returning wrong data.
- **Auth:** none, API key header, OAuth2 client credentials. Plus arbitrary
  static `headers` (config-level, overridable per table) for APIs needing more
  than one — subscription keys, tenant ids, `Accept`.
- **POST-to-read.** Set `"method": "POST"` on a table for APIs that take the
  query in the body (search endpoints). The static `body` is sent as JSON, and
  translated filter/paging/sort values are merged into it at `bodyParamsPath`
  instead of the query string. Still read-only: this posts a *query* and reads
  rows back, it does not write. Get-by-id stays GET unless `getMethod` says
  otherwise.
- **Keys:** GUID, int, or strings ≤ 14 UTF-8 bytes (reversibly embedded in
  the row GUID, because plug-ins are stateless so there is no lookup table).
- **Paging:** pageNumber, skip/top, or none (local slice, small collections
  only).

## Deployment runbook

Two documents, aimed at different readers:

- **[INSTALL.md](INSTALL.md)** — step-by-step, non-technical. Installing into a
  fresh environment, then adding an API and creating its tables through the
  portal, with no command line.
- **[DEPLOYMENT.md](DEPLOYMENT.md)** — the technical detail behind it: what
  travels in a solution zip, what does not, solution component types, and the
  network constraints that decide whether this approach is viable at all.

1. **Build**
   ```
   sn -k src/RestVtp.Plugins/RestVtp.snk        # once
   dotnet build src/RestVtp.Plugins -c Release
   dotnet build tools/SwaggerForge -c Release
   ```
   The signing key is **not** in this repository — it is a private key, and it
   is the assembly's identity. Generate your own with the `sn -k` line above.
   Note the consequence: an assembly built with a different key has a different
   `PublicKeyToken`, so it cannot upgrade a registration made with another one.
   Once you have registered a build in an environment, keep that key safe and
   never regenerate it.
   The plug-in build produces a **plug-in package** at
   `src/RestVtp.Plugins/bin/Release/RestVtp.Plugins.0.1.0.nupkg`, containing
   `RestVtp.Plugins.dll` and `Newtonsoft.Json.dll`. Register that package, not
   the bare assembly: Newtonsoft.Json is a real runtime dependency and only the
   package carries it. The Dataverse-provided SDK assemblies are marked
   `PrivateAssets="All"` so they are deliberately excluded from the package.

2. **Register the Data Provider** (Plugin Registration Tool)
   - Register the assembly/package.
   - *Register New Data Provider*: create the **data source table** (e.g.
     `rvtp_restdatasource`) when prompted, and bind:
     - Retrieve → `RestVtp.Plugins.RetrievePlugin`
     - RetrieveMultiple → `RestVtp.Plugins.RetrieveMultiplePlugin`
   - Add a multiline text column `rvtp_mappingjson` to the data source table.

3. **Create a data source record** (your Service Instance): Advanced
   Settings → Administration → Virtual Entity Data Sources → New, pick the
   RestVtp provider, paste the reviewed `mapping.json` into
   `rvtp_mappingjson`. Secrets note: v1 stores credentials in that JSON, which is
   acceptable for dev; move to environment variables + Key Vault before
   anything real.

4. **Generate config + tables**
   ```
   swaggerforge generate-config swagger.json --prefix rvtp --out mapping.json
   # review mapping.json: keyKind, itemsPath, paging, auth, filterParams
   swaggerforge create-tables --config mapping.json --env https://org.crm.dynamics.com --prefix rvtp --solution MySolution
   ```

5. **Smoke test**: open the table in Advanced Find. First failure modes to
   check: itemsPath wrong (array error), keyKind mismatch (GUID errors),
   filters on unmapped columns (deliberate throw).

## Local end-to-end testing (MockApi)

`tools/MockApi/server.js` is a zero-dependency Node server that matches
`config/mapping.example.json` exactly: 57 customers, `city`/`active`
equality filters, `page`/`pageSize` paging, `sort`/`-sort`, get-by-id, and
`X-Api-Key: test-key-123` auth.

```
node tools/MockApi/server.js 3000
curl -H "X-Api-Key: test-key-123" "http://localhost:3000/v1/customers?city=London&page=1&pageSize=3"
```

Two test loops it enables:

1. **SwaggerForge round-trip**: run `generate-config` against
   `tools/MockApi/swagger.json` and diff the draft against
   `mapping.example.json`. The nested columns (`contact.email`,
   `address.city`) are deliberately absent from the swagger schema, because v1
   flattens top-level properties only, so adding those dot-paths by hand is
   the intended manual step.
2. **Deployed provider test**: Dataverse plug-ins can't reach localhost, so
   tunnel it (`ngrok http 3000` / Tailscale funnel, your Mac mini setup is
   ideal here), set the tunnel URL as `baseUrl` on the data source record,
   and hit the virtual table from Advanced Find. Filtering on city, paging
   through 57 rows, and opening a record form covers Retrieve,
   RetrieveMultiple, translation, and GUID synthesis in one pass.

## Known gaps / v2 backlog

- Quick find (OR-of-LIKEs): needs API-side search support and per-column
  `like` mapping.
- Range/comparison operators where the API supports them.
- `orderby` paging cookies from grids (currently pageNumber only).
- Secrets out of mapping JSON → environment variables / managed identity.
- Write support (Create/Update/Delete plug-ins).
- FakeXrmEasy test harness for QueryTranslator (pure unit-testable already).

## Licence

[MIT](LICENSE). Use it for anything, commercially or otherwise; keep the
copyright notice, and understand it comes with no warranty.
