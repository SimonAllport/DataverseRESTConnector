# RestVtp — Installation and Configuration Guide

How to install the REST virtual-table provider into a fresh Dataverse
environment, and how to point it at an API once it is there.

No command line is needed for any step marked **portal**. The two steps that do
need a technical colleague are called out where they occur.

| | |
|---|---|
| Plug-in version | 0.8.0 |
| Publisher prefix | `rvtp` |
| Access | Read-only |
| Updated | 15 August 2026 |

> **Read this before you begin.**
> RestVtp is proven working in the development environment — a live virtual table
> returning several hundred real records from an external API. **It has never
> been installed into a second environment.** Part One is therefore the intended
> procedure rather than a rehearsed one. Allow time for it, do it with a
> technical colleague available, and expect the first attempt to surface at least
> one thing this guide does not predict.

---

## What you are actually installing

RestVtp makes an external REST API appear inside Dataverse as ordinary tables.
People open a table, see rows, filter and sort them; behind the scenes each of
those actions becomes a live call out to the API. Nothing is copied or stored in
Dataverse — close the view and there is no data left behind.

Three things make that work, and it is worth knowing them apart, because each
step below acts on one of them:

- **The provider** — the code. Installed once per environment; you never touch it
  again.
- **A data source record** — one per API. Holds the API's web address, its
  credentials, and the instructions describing which fields map to which columns.
- **A virtual table** — one per thing you want to see: orders, customers, cases.
  Created from the data source record and permanently tied to it.

---

# Part One — Installing into a separate environment

You will need a colleague with **System Administrator** rights in the target
environment, and access to the development environment where RestVtp was built.
Budget half a day for the first run.

## 1. Answer four questions first

All four are about the target environment, and any one of them can stop the
project. Get answers in writing before spending time on the rest.

1. **Can the environment call out to the internet?** RestVtp's entire job is to
   make an outbound call from Dataverse to your API. Some locked-down
   environments block this completely.
2. **Does the API restrict who can call it?** If it only accepts traffic from
   approved addresses, you cannot give them a useful one — Dataverse calls come
   from a very large, constantly shifting pool of Microsoft addresses. The answer
   is a relay: a small service you control with one fixed address, which passes
   calls through. RestVtp needs no change for this; you simply point it at the
   relay instead.
3. **Are custom plug-ins permitted at all,** and is there a review process to go
   through first?
4. **Who is allowed to hold the API credentials?** In this version the API key is
   stored in plain text on a record inside Dataverse. Anyone who can read that
   record can read the key. That is fine for development and needs a decision
   before it is fine for anything else.

> **The one that ends the project.**
> If the answer to question 1 is a firm no, and the environment does not have the
> premium network features that provide an alternative route, then no relay and
> no configuration change will help — the first hop is the one being blocked.
> Stop and reconsider the approach rather than working around it.

## 2. Build the installer file

*In the development environment · technical*

The installer is a single *managed solution* file exported from the development
environment. Before exporting, open that solution and confirm all four of these
are inside it. They are added separately and it is easy to end up with only some
of them:

- The **plug-in package** — the code itself.
- The **data provider** registration — what tells Dataverse to run that code.
- The **REST Data Source** table, including its Mapping JSON column.
- The **API Definition** table and its two automations — this is the screen you
  will use in Part Two. Without it, the target environment has no way to author
  anything without command-line tools.

> **Leave the virtual tables behind.**
> Do not include the existing virtual tables in the installer. Every virtual
> table is permanently tied to the specific data source record it was created
> from, and that record will not exist in the new environment — imported tables
> arrive pointing at nothing. It is faster and safer to create them fresh in the
> target using Part Two.

Then export, choosing **Managed**.

## 3. Check the installer is not empty

*Thirty seconds · do not skip*

Look at the size of the exported file. This has already caught out one export.

| File size | What it means |
|---|---|
| **Around 10 KB** | The code is missing. You have exported the tables and settings only. It will import successfully and then do nothing at all. |
| **A few hundred KB** | The code is included. This is what you want. |

Dataverse does not warn you about this. A managed export succeeds happily with no
code inside it, and the failure only appears later as tables that return errors.
If the file is tiny, go back to step 2 — the plug-in package is not in the
solution.

## 4. Import it into the new environment

*In the target environment · System Administrator · portal*

> **make.powerapps.com** → pick the target environment → **Solutions** →
> **Import solution** → browse to the file → **Next** → **Import**

The import runs in the background and takes a few minutes. Wait for it to report
success before going further. If it fails, download the log it offers — the
reason is usually named plainly in it.

## 5. Confirm the provider arrived

*In the target environment · portal*

This is the checkpoint that tells you whether the install genuinely worked.

> **Advanced settings** → **Administration** → **Virtual Entity Data Sources** →
> **New**

A list of available providers appears. **RestVtp Provider** should be one of
them. If it is, the code and the registration both travelled correctly — cancel
out of the dialog, you will come back to it in Part Two.

> **If RestVtp Provider is not in the list.**
> The provider registration did not survive the import. This is the single most
> likely thing to go wrong on a first install, and it is the one point where the
> no-tools approach breaks down: someone technical needs to register the provider
> by hand using the Plugin Registration Tool, which runs on Windows only, against
> the target environment. It is a twenty-minute job, but it needs permission to
> run a tool against the environment — worth confirming that is allowed as part
> of step 1, question 3.

## 6. Remove any tables that came along

*Only if you ignored the advice in step 2*

If the installer did include virtual tables, they are now present and broken.
Each one remembers the identity of a data source record that exists only in the
development environment, and there is no way to edit that link afterwards.

Delete them. Then create them again in Part Two, which binds them correctly to
this environment's own record. If deleting is not acceptable, this becomes a
technical task — someone has to re-point each table's data source using the
command-line tooling.

## 7. Prove the connection works

Now run through **Part Two** once, with the simplest API endpoint you have.
Getting a single table to return real rows is the only meaningful proof that the
install succeeded — everything before this point can look fine while being subtly
broken.

Expect the first attempt to fail on something small, and treat that as normal
rather than as evidence the install went wrong. The troubleshooting table at the
end covers what usually appears first.

## 8. Give people access

Virtual tables follow the same permission rules as any other table. Two things
are needed before anyone other than an administrator can see them:

- **Security roles** — grant Read on each new table to whichever roles need it.
  Read is the only privilege that means anything here; RestVtp cannot write.
- **A place to see it** — add the table to a model-driven app, or people will
  have no way to reach it.

Do not grant broad access to the **REST Data Source** table. That is where the
API credentials live.

---

# Part Two — Adding an API and creating its tables

This part needs no tools and no code. You work entirely through two records: one
describing the API, and one worksheet where you author and review before anything
is created. Repeat it for each API you connect.

## 1. Create the data source record

*Once per API · portal*

> **Advanced settings** → **Administration** → **Virtual Entity Data Sources** →
> **New** → choose **RestVtp Provider**

Give it a name that says which API it is — *Orders API (Staging)*, not *Data
Source 1*. You will type this name again in the next step, and it is how the two
records find each other.

Leave Mapping JSON empty for now. The next steps fill it in for you.

> **One record, one API.**
> A data source record holds a single web address and a single set of
> credentials, shared by every table created from it. A second API always needs a
> second record — the system will reject an attempt to mix two addresses into one.

## 2. Create an API definition record

Open the **API Definitions** table and create a new record. This is a
scratchpad — it is where you paste, generate, review and correct, and nothing you
do here affects anything until you explicitly ask it to.

| Field | What goes in it |
|---|---|
| **Name** | Anything meaningful to you. |
| **Base URL** | The API's address, up to but not including the specific endpoint — for example the part ending in `/api/external/v1`. |
| **Swagger JSON** | The API's own description of itself, pasted in whole. Most APIs publish this at a documentation URL; ask the API's owner for the "OpenAPI" or "Swagger" document. |
| **Data Source Name** | The exact name of the record you created in step 1. Required whenever more than one exists. |
| **Solution Name** | Which solution the new tables should be added to, so they can be moved onward later. |

Save the record.

## 3. Tick Generate Mapping

*First of two buttons*

Tick the **Generate Mapping** box and save. The tick clears itself — that is
expected, not a failed save.

The work happens in the background, so nothing appears to change. Wait a moment,
then **refresh the form**. Two fields will have filled in:

- **Mapping JSON** — a draft describing the tables it proposes to create, one per
  endpoint it recognised.
- **Last Result** — how many tables it proposed, and a reminder of what to check.

If Last Result begins with `FAILED`, the reason follows on the next line. Nothing
was created; fix and tick again.

## 4. Review the draft — the real work

The draft is a *guess*, and deliberately so. An API's own documentation cannot
tell you whether it truly honours a request or quietly ignores it, so the
generator refuses to assume. Four things need a human before you go further.

> **Filtering — the one that can mislead people.**
> The draft adds **no filters**. Only add one after you have confirmed, against
> the live API, that it genuinely changes the results. An API that ignores an
> unrecognised filter returns *everything* — and Dataverse would then present a
> full, unfiltered list to the user as though it were a filtered one. A missing
> filter is an inconvenience. A filter that does not work is wrong data on
> someone's screen.

> **Paging always comes back as "none".**
> Meaning: fetch everything and show the first page locally. Fine for a few dozen
> records, wrong for thousands. If the API returns results in pages, set it up to
> match how that API expects to be asked — which page, and how many per page.

> **Only the obvious fields are picked up.**
> Fields sitting at the top level of each record are mapped. Anything nested
> inside another structure — a customer's address inside a customer object — has
> to be added by hand. Ask for help with these; they are simple but fiddly to
> write.

> **The first text column becomes the record's name.**
> Whatever appears first is what people will see as the record title throughout
> Dataverse. If that is an internal reference number rather than something
> readable, reorder the columns so a better one comes first.

While you are here, **delete any columns you will never show**. This is not
tidiness — it buys you room, as the next step explains.

## 5. Stay inside the size budget

Dataverse caps how much configuration one data source record can hold. The
practical ceiling is roughly **1,500 characters**, which in real terms is about
**two or three small tables per data source record**. Measured against the live
environment: two tables of four and seven columns fitted; adding a third
three-column table did not.

You will be told if you exceed it — the failure message names the real limit and
the actual size rather than leaving you guessing. Three ways to get back under:

- Remove columns you will not use. This is by far the biggest saving.
- Shorten how deeply nested fields are described.
- Split the tables across a second data source record for the same API. This is a
  normal thing to do, not a workaround.

## 6. Tick Create Tables

*Second button · this one is permanent*

When the draft is right, tick **Create Tables** and save. Again the tick clears
itself, the work runs in the background, and you refresh to see the outcome in
**Last Result**.

Two things happen: your mapping is copied onto the data source record, and the
tables are built. The copy is a *merge* — tables defined elsewhere on the same
record are left untouched, and Last Result tells you how many were added,
replaced and left alone.

> **Existing tables are skipped, never updated.**
> If a table of that name already exists, it is reported as skipped and left
> exactly as it was. To change a table's columns you have to delete the table and
> create it again — which is why step 4 is worth the time. Deleting and
> recreating also changes the internal identity of every record in it, so any
> saved views, bookmarks or links pointing at individual records will stop
> resolving.

## 7. Test the table

Open the new table's data view, or use Advanced Find. Rows appearing means the
whole chain is working: Dataverse called the API, the API answered, and the
answer was mapped onto columns.

Then test three things deliberately, because each fails differently:

- **Open a single record.** Some APIs answer a request for one record in a
  different shape from a request for a list, and that needs its own setting.
- **Apply each filter you configured** and confirm the result actually narrows.
  See step 4.
- **Page past the first screen** of results if there are many.

When something is not supported, RestVtp raises a clear error rather than quietly
returning an approximate answer. An error saying a column cannot be filtered is
the system working as designed — it is refusing to mislead you.

## 8. Publish it to users

- Grant **Read** on the new table to the relevant security roles.
- Add the table to a model-driven app so people can find it.
- Build the views people actually need — a virtual table's default view shows raw
  columns in creation order.

Keep the API definition record. It is your record of what was configured, and the
place to start from if the table ever needs rebuilding.

---

## When something goes wrong

Every message RestVtp produces begins with `RestVtp:` and names the specific
setting at fault. These are the ones you are most likely to meet, roughly in the
order they tend to appear.

| What you see | What it means | What to do |
|---|---|---|
| No data source record found | The API definition record cannot find the record named in Data Source Name. | Check the spelling matches exactly, or create the record first. |
| Several data source records exist | More than one exists and none was named, so it will not guess. | Fill in Data Source Name. |
| This record points at a different address | You are adding a second API's tables onto a record already serving another API. | Create a separate data source record for it. |
| The merged mapping is over the ceiling | Too much configuration for one record. | Trim columns, or split across two records. See Part Two, step 5. |
| Expected a list but got something else | The setting describing where the records sit in the API's answer is wrong. | Look at what the API actually returns and correct that path. |
| Errors mentioning record identity | The setting for what kind of key the API uses does not match reality. | Correct it, then delete and recreate the table. |
| A column cannot be filtered | Working as designed — someone filtered on a column with no configured filter. | Either configure and test that filter, or accept the limit. |
| Timeouts, or nothing at all | Almost always the network. See Part One, step 1. | Confirm the environment can reach the API, and that the API accepts the call. |

---

## Five things to remember

1. **It is read-only.** People can look at the data, filter it and sort it. Nobody
   can create, change or delete anything through these tables, by design.
2. **One API per data source record.** A record holds one address and one set of
   credentials for all of its tables.
3. **A table cannot be moved to a different data source record.** That link is
   fixed when the table is created. Changing it means deleting the table and
   creating it again.
4. **It fails loudly rather than answering approximately.** Anything RestVtp
   cannot do properly produces an error. It will never filter a list halfway and
   present the result as complete.
5. **Credentials are currently stored in plain text.** Anyone who can read the
   data source record can read the API key. Restrict access to that table, and
   treat moving credentials somewhere safer as required work before any
   production use.

---

*Everything in Part Two is proven in the development environment. Part One is the
intended procedure and has not yet been carried out end to end. For the technical
detail behind these steps — component types, solution packaging, network
options — see [DEPLOYMENT.md](DEPLOYMENT.md).*
