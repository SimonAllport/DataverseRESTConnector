# Deployment and ALM

How RestVtp gets from a local build into a target Dataverse environment,
including one you do not administer and whose network is locked down.

The short version: you build in an environment you control, ship a managed
solution, and the target's admins import it. Most of RestVtp travels in that
zip. Two things do not, and one of them (outbound network access) is the
constraint most likely to stop the whole approach.

## The shape

```
Developer Plan environment            Target environment
(yours, build here)                   (closed, network-locked)

  register provider (once, PRT)
  create virtual tables
  create data source record  ─── does NOT travel ───┐
  add all to unmanaged solution                     │
        │                                           │
        └── export managed ──► solution.zip ──► import
                                                    │
                                    someone inside creates
                                    the data source record ◄┘
                                    with baseUrl + credentials
```

## What travels in the solution zip

| Component | Travels | Notes |
|---|---|---|
| Plug-in package (assembly + Newtonsoft.Json) | Yes | Must be a package (nupkg), not a bare assembly, or the dependency is missing |
| Plug-in types, Retrieve / RetrieveMultiple bindings | Yes | |
| `rvtp_restdatasource` table, including `rvtp_mappingjson` | Yes | Table definition only, not its rows |
| Virtual tables and their columns | Yes | Created by `swaggerforge create-tables` |
| Data provider registration (`entitydataprovider`) | **Verify** | See Open questions. If it does not travel, every target needs its own PRT registration, which a closed environment may not permit |
| **The data source record** | **No** | It is a data row. Someone must create it inside the target |

The data source record not travelling is correct behaviour, not a gap. It is
the K2 service instance: it holds `baseUrl`, credentials, and the mapping JSON,
and dev should never point at the target's API anyway. It does mean every
environment needs a manual (or scripted) creation step.

### The split-solution trap (observed 2026-08-14)

"Travels" above means *travels if it is actually in the solution*, and by default
it is not. Registering through PRT scatters the pieces:

- **Register New Package** put the plug-in package in whichever solution was
  selected at that moment.
- **Register New Data Provider** put the provider in a different solution.

The result was a `RestVtp` solution holding the provider and tables, and a
separate solution holding the package. Exporting `RestVtp` as managed
**succeeded**, producing a zip containing only `customizations.xml` and
`solution.xml` — no plug-in payload whatsoever. The unresolved dependency
appears in the unmanaged export as a `MissingDependency` on the `PluginPackage`,
but it does **not** block a managed export. Importing that zip into a fresh
environment would create the tables and the provider with no code behind them.

Check before shipping: unpack the managed zip and confirm a plug-in payload is
present, and check `Other/Solution.xml` for `MissingDependencies` in the
unmanaged export. A correct export contains `pluginpackages/<name>/package/*.nupkg`;
adding the package took the zip from 10 KB to 299 KB.

`pac solution add-solution-component` **cannot** add either component type — it
rejects the correct `EntityDataProvider` id (181) as unknown, mis-resolves the
name to 78, and cannot resolve `PluginPackage` at all. Use the SwaggerForge
commands instead:

```bash
swaggerforge list-components --env <url> --solution RESTDataConnector
swaggerforge add-component --env <url> --solution RestVtp \
  --id <pluginpackageid> --type 10041 --required
```

Component types, read from a live environment rather than guessed: **10041**
PluginPackage, **181** EntityDataProvider, **183** data source record, 91
PluginAssembly, 1 Entity.

### The data source record dependency

Once the tables are bound correctly, the unmanaged export reports one remaining
`MissingDependency`: the data source record itself, as
`Required type="183" solution="Active"`. This is expected — it is the data row
that deliberately does not travel — but it has a consequence worth planning for.

Each virtual table stores `DataSourceId`, the GUID of the data source record in
*this* environment. That GUID will not exist in the target. After importing and
creating the target's own data source record, the tables must be re-bound to it,
either by re-running `create-tables` against the target or by updating
`DataSourceId` on each imported table. Do not assume a clean import produces
working tables.

## Build environment

A free **Power Apps Developer Plan** environment is sufficient to build and
prove everything: full Dataverse, custom plug-in registration, data providers,
virtual tables, and solution export. Note two limits. It is licensed for
development and test only, so it cannot be the destination, and it does not
include Managed Environments, which rules out the VNet option described below
for this environment specifically.

## Procedure

### Phase 0: register the provider (once, ever)

Requires the Plugin Registration Tool, which is **Windows only**. `pac tool`
on macOS offers only `list`, `admin`, and `maker`. Borrow a Windows machine or
VM for this step.

1. Register the plug-in package.
2. Register New Data Provider, which creates `rvtp_restdatasource` and binds
   Retrieve to `RestVtp.Plugins.RetrievePlugin` and RetrieveMultiple to
   `RestVtp.Plugins.RetrieveMultiplePlugin`.
3. Add the multiline text column `rvtp_mappingjson` to that table.

After this the solution zip becomes your installer and you should not need PRT
again.

### Phase 1: build the solution

Everything from here runs on macOS.

```bash
pac auth create --environment https://yourdev.crm11.dynamics.com
pac solution init --publisher-name RestVtp --publisher-prefix rvtp
```

Add the components to an unmanaged solution in the maker portal, or force
specific ones in with `pac solution add-solution-component`. The provider
registration is the one to watch: if the portal will not offer it as a
component, add it explicitly by id.

### Phase 2: create the tables

```bash
swaggerforge generate-config swagger.json --prefix rvtp --out mapping.json
# review: keyKind, itemsPath, paging mode, auth, filterParams
swaggerforge create-tables --config mapping.json \
  --env https://yourdev.crm11.dynamics.com --prefix rvtp --solution MySolution
```

`generate-config` output is a draft. It leaves `filterParams` empty unless API
query parameter names match sanitised column names exactly, and it always
writes `paging: "none"`. Both need a human pass before `create-tables`.

### Phase 3: export managed

```bash
pac solution version --patchversion
pac solution export --path ./out --name MySolution --managed
```

### Phase 4: import to the target

Their admin imports the zip. Nothing in the import needs PRT, assuming the
provider registration travelled.

### Phase 5: create the data source record

Inside the target: Advanced Settings, Administration, Virtual Entity Data
Sources, New, select the RestVtp provider, paste the mapping JSON into
`rvtp_mappingjson`. Whoever does this needs write access to that table and has
to be trusted with the API credentials, which is why the secrets question below
stops being optional.

### Phase 6: smoke test

Open a table in Advanced Find. First failure modes, in the order they usually
appear: `itemsPath` wrong (array error), `keyKind` mismatch (GUID errors),
filtering on a column absent from `filterParams` (deliberate throw), and
network (see below).

## Network: the hard constraint

RestVtp's entire function is an outbound HTTPS call from the Dataverse sandbox
to `baseUrl`. In a locked-down environment this fails in one of two distinct
ways, and they have different fixes.

### Failure mode A: the target API will not accept the call (ingress)

The API sits behind a firewall or IP allowlist, and the admins ask you for the
source IP to allow. **You cannot give them a useful one.** Dataverse sandbox
outbound traffic originates from Azure datacenter ranges for the region. Those
ranges are large and they change. Allowlisting them means allowlisting a large
slice of Azure, which most security teams will refuse, and rightly.

The fix is a relay with a stable outbound IP:

- Azure API Management, or an Azure Function on a plan supporting a dedicated
  outbound IP, or any reverse proxy you control.
- Dataverse calls the relay. The relay calls the target API.
- The API owner allowlists one IP, the relay's, instead of half of Azure.
- Set `baseUrl` to the relay. RestVtp needs no code change for this, which is
  the point of keeping everything in the mapping config.

This also gives you a natural place to hold the real API credentials, so the
data source record can carry a relay-scoped secret instead of the live one.

### Failure mode B: Dataverse cannot call out at all (egress)

If the environment's policy blocks sandbox egress entirely, a relay does not
help, because the first hop is what is blocked.

The supported answer is **Power Platform virtual network support** via Azure
subnet delegation, which lets Dataverse plug-ins reach resources over a VNet
rather than the public internet. It requires **Managed Environments**, so it is
a premium capability of the *target*, not something your Developer Plan can
provide. If the target does not have it, and policy genuinely blocks egress,
then a Dataverse plug-in calling an external REST API is not achievable in that
environment by any route, and the honest answer is to stop and reconsider the
integration pattern.

### What does not work

The **on-premises data gateway** is not available to plug-ins. It serves
connectors, dataflows, and similar, not sandboxed plug-in code. Do not plan
around it.

### What to ask their admins

1. Does the environment permit registering custom plug-in assemblies at all,
   and is there a review process?
2. Can the Dataverse sandbox make outbound HTTPS calls to the public internet?
   If not, is the environment a Managed Environment with VNet support
   configured?
3. Does the target API restrict inbound by IP? If yes, will they allowlist a
   single relay IP that we control?
4. Who is permitted to create the data source record, and how should the API
   credential be handed over?
5. Is TLS interception in play? `HttpExecutor` forces TLS 1.2 and does no
   certificate pinning, but a corporate MITM certificate still has to be
   trusted by the sandbox.

## The 4000-character ceiling on a data source record

Discovered 2026-08-14, and it constrains the whole design.

Dataverse packs a data source record's custom fields into the
`connectiondefinition` attribute of `entitydatasource`, which is capped at
**4000 characters**. That cap applies no matter what length you declare on
`rvtp_mappingjson` — ours is declared at 100,000 and is still limited to 4000 in
practice. Exceeding it fails on save with:

> The length of the 'connectiondefinition' attribute of the 'entitydatasource'
> entity exceeded the maximum allowed length of '4000'.

**The mapping does not get all 4000 of those characters.** It is stored escaped,
alongside every other field on the record, and escaping roughly doubles JSON.
Measured against a live environment:

| Mapping JSON | Result |
|---|---|
| 1297 characters | saves |
| 1767 characters | rejected |

Consequences worth planning around:

- The practical ceiling is around **1500 characters of mapping**, not 4000.
- Store it **compact, never indented**. Pretty-printing spends the budget for
  nothing, since the record is not where you edit the mapping.
- That is roughly **two or three small tables per data source record**. Two
  tables with four and seven columns came to 1297; adding a third three-column
  table pushed it to 1767 and failed.
- Past that, split tables across **several data source records**, each with its
  own provider binding. This is not a workaround — a data source record already
  represents one API with one `baseUrl` and one set of credentials.
- Long `sourcePath` values and large column counts consume the budget fastest.
  Trimming columns you will never surface in Dataverse is the cheapest saving.

`DataSourceAdminPlugin` checks the merged length before saving and fails with a
message naming the real limit, rather than letting Dataverse's error surface.

## Secrets

v1 stores credentials in the mapping JSON on the data source record. That is
acceptable for dev and is not acceptable for a closed target, because anyone
with read access to that row can read the API key or client secret.

Before shipping to a real environment, move credentials to **environment
variables** (which are solution-aware and can be given per-environment values
at import time) or Key Vault references. This is currently listed as v2
backlog and should move into scope for any deployment past the walking skeleton.

## Open questions

These are unverified and should be confirmed the first time a real solution is
built. Do not treat them as settled.

1. ~~**Does `entitydataprovider` package as a solution component?**~~
   **Partly answered (2026-08-14): yes, it exports.** Exporting an unmanaged
   solution containing the provider produced `EntityDataProviders/{id}.xml` in
   the unpacked solution, so the registration is a real, packageable component
   and does not have to be recreated by hand in each environment. Still
   unverified: whether it *imports* cleanly into a different environment, and
   whether the plug-in package it points at is resolved correctly there. Confirm
   on the first real import before relying on it for a closed target.
2. ~~**Does `MetadataBuilder` bind the tables correctly?**~~
   **Answered (2026-08-14): it did not, and is now fixed.** The suspicion was
   right: setting `DataProviderId` without `DataSourceId` is not enough.
   `MetadataBuilder` now resolves the data source record and sets both, with
   `--datasourcerecord` / `--datasourceid` to disambiguate when several exist.

   Creating a virtual table also required disabling a family of capabilities
   that assume stored rows. `ValidateVirtualEntityCreate` rejects them one at a
   time, so expect a sequence of refusals rather than a single error:
   change tracking (`ChangeTrackingEnabled`, `CanChangeTrackingBeEnabled`),
   auditing, charts, connections, duplicate detection, mail merge, business
   process and quick create. `ExternalName` and `ExternalCollectionName` are
   also mandatory on the entity, even though RestVtp never reads them.

3. ~~**Does `IEntityDataSourceRetrieverService` resolve as written** inside the
   sandbox.~~ **Answered (2026-08-14): yes.** A live query against a virtual
   table returned mapped rows, which is only possible if the service resolved
   and the mapping JSON was read off the data source record.
4. **Whether managed solution import handles the plug-in package** cleanly on
   upgrade, in particular assembly version bumps against an existing
   registration. The strong name key must not change, or the assembly identity
   changes and the upgrade path breaks.
