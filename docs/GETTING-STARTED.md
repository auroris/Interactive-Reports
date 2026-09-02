# Getting started

This guide takes a host application from an empty `Program.cs` to a running report,
then covers the optional pieces: saved-report storage, source-controlled report
documents, the administration page, authorization, the GraphQL transport, logging, and
localization. It is the long-form companion to the README's quick start.

For the complete server and browser-element reference, see the
[Integration API guide](API.md). For the design and trust model, see
[Architecture](ARCHITECTURE.md). End users of the report UI have their own
[User Guide](USER-GUIDE.md).

## Install and configure

1. Add the server and JSON client packages. Add the file client when the browser should download CSV:

   ```sh
   dotnet add package InteractiveReport.AspNetCore
   dotnet add package InteractiveReport.Client.Json
   dotnet add package InteractiveReport.Client.FileDownload
   ```

2. Configure a report — a data source and a SELECT. No dialect, ever: it is derived
   from the data source's driver.

   ```json
   "InteractiveReport": {
     "Reports": {
       "orders": {
         "dataSource": "MainDb",
         "sql": "SELECT ORDER_ID, CUSTOMER, AMOUNT, ORDER_DATE FROM ORDERS",
         "authorization": { "allowAnonymous": true }
       }
     }
   }
   ```

   `dataSource` is one property with two forms: a value **without `=`** names an
   entry under the standard `ConnectionStrings` section (above, `MainDb`); a value
   **with `=`** is a literal connection string. The ADO.NET provider resolves from
   the `ConnectionStrings:{name}_ProviderName` companion entry when present (the
   Umbraco and classic-ASP.NET convention), or from an explicit one-word
   `"provider"` — `sqlite`, `sqlServer`, `postgres`, or `oracle`. SQLite works out
   of the box; the other drivers come from your app's own package graph, and a
   missing one fails at startup naming the exact package to add (for example
   `Microsoft.Data.SqlClient`). Reports are authenticated-only by default —
   `allowAnonymous` is the deliberate opt-out.

   Multi-query reports default to `"consistency": "none"`: count, aggregate,
   break-total, and page statements run independently and the engine imposes no
   transaction policy. Set `"consistency": "snapshot"` when one database snapshot
   is required. The provider owns the mechanism: Oracle uses a nonblocking read-only
   transaction and returns grid datasets through one anonymous PL/SQL `REF CURSOR`
   batch; Postgres uses `REPEATABLE READ`; SQLite uses a read transaction (WAL mode is
   recommended where concurrent writers matter); SQL Server uses `SNAPSHOT` and fails
   with configuration guidance when the database has not enabled
   `ALLOW_SNAPSHOT_ISOLATION`. A requested guarantee is never silently downgraded.
   This is server-only definition configuration: it is never report state and never
   appears in schema or result payloads.

3. Wire the server and clients up in `Program.cs`:

   ```csharp
   builder.Services.AddInteractiveReports(builder.Configuration);
   builder.Services.AddInteractiveReportJson();
   builder.Services.AddInteractiveReportFileDownload();
   // …
   app.MapInteractiveReportJson("/api/reports");
   app.MapInteractiveReportFileDownload("/api/download");
   ```

4. Done — call **`GET /api/reports/orders`** and select its `isDefault` document, or
   browse `/api/reports/orders/view`. The packaged page hosts the report;
   embedding `<interactive-report>` in your own pages remains the primary
   path for real applications (see [Embedding the report](EMBEDDING.md)). Saved reports and administration require the explicit
   storage configuration below. The administration page is at `/reports/admin`;
   bootstrap it by listing at least one administrator identity in
   `InteractiveReport:Administrators`. Administrators can then add database-backed
   administrators in the page's **Authorization…** editor. Set
   `InteractiveReport:WhoamiEnabled` so the page can show precise identity guidance.
   Report-definition configuration mistakes fail at startup with an error naming the
   fix; saved-storage errors are deferred until that optional subsystem is used. Avoid
   the report names `ui`, `saved`,
   `admin`, and `whoami`, which collide with the endpoint namespace. The packaged
    pages can be turned off with `InteractiveReport:ViewerPagesEnabled: false`.

`MapInteractiveReportJson` publishes standard ASP.NET Core endpoint summaries, tags,
request-body types, response types, and coded-error response metadata. A host can add its
preferred OpenAPI generator without the Interactive Reports package depending on one.
The Workbench demonstrates this with Swagger UI at `/swagger` in Development.

The package performs no persistence setup unless you request it. A report-only
installation does not create `App_Data`, a SQLite file, or database tables. To enable
saved reports and administration, configure their shared storage explicitly:

```json
"InteractiveReport": {
  "SavedReports": {
    "dataSource": "MainDb",
    "tablePrefix": "MYAPP_"
  }
}
```

`dataSource` accepts the same ConnectionStrings name or literal connection string as a
report and uses the same optional `provider` setting. Alternatively, set `connection`
to a database registered with `AddConnection`. `tablePrefix` is optional; the example
creates `MYAPP_IR_SAVED_REPORTS` and `MYAPP_IR_REPORT_AUTHORIZATION`. Without
`dataSource` or `connection`, ordinary reports still run, while saved-report and
administration operations return an error and perform no filesystem or database
writes. A configured but unreachable target also returns a sanitized server error.

### Umbraco 13

An Umbraco site already carries the SQL Server and SQLite drivers and already has
its connection string under `ConnectionStrings:umbracoDbDSN` with the
`umbracoDbDSN_ProviderName` companion — so a report over the Umbraco database is
exactly the minimal configuration above with `"dataSource": "umbracoDbDSN"`, and the
two `Program.cs` lines slot in beside the Umbraco pipeline (map after `app.UseUmbraco(...)`).
Members-only reports can use `"authorization": { "policy": "..." }` against any
policy the site registers.

For programmatic connections (custom factories, wrapper/profiler connection types),
register them in code instead of configuration:

```csharp
builder.Services.AddInteractiveReports(builder.Configuration)
    .AddConnection("MainDb", sp => new SqlConnection(...))                       // dialect detected from the type
    .AddConnection("Profiled", sp => new ProfiledDbConnection(...), ReportDialect.SqlServer); // wrapper: declare it
```

## Configured report documents

A report definition can reference source-controlled documents. Paths are relative to
the host's content root unless absolute:

```json
"orders": {
  "connection": "MainDb",
  "sql": "SELECT ORDER_ID, CUSTOMER, STATUS, CUSTOMER_URL, THUMBNAIL_URL, AMOUNT FROM ORDERS",
  "documentFiles": [
    "ReportDocuments/orders.default.json",
    "ReportDocuments/orders.finance.json"
  ]
}
```

Each file contains its selector title, an optional default flag, and the
normal state document:

```json
{
  "title": "Default",
  "default": true,
  "state": {
    "activeTable": "pivot",
    "tables": {
      "base": {
        "from": "definition",
        "schema": null,
        "composables": [
          { "kind": "filter", "filters": [ { "expr": "AMOUNT > 0" } ] },
          { "kind": "sort", "sorts": [ { "col": "AMOUNT", "dir": "desc" } ] },
          { "kind": "select", "columns": [ "ORDER_ID", "CUSTOMER", "THUMBNAIL_URL", "AMOUNT" ] },
          { "kind": "formats", "formats": {
            "CUSTOMER": {
              "displayAs": "link",
              "urlColumn": "CUSTOMER_URL",
              "textColumn": "CUSTOMER"
            },
            "THUMBNAIL_URL": {
              "displayAs": "image",
              "urlColumn": "THUMBNAIL_URL"
            },
            "AMOUNT": { "mask": "$#,##0.00", "classes": [ "amount-column", "emphasized" ] }
          } }
        ]
      },
      "groupBy": {
        "from": "base",
        "schema": null,
        "composables": [
          { "kind": "group", "by": [ "CUSTOMER" ],
            "values": [ { "id": "ir1", "col": "AMOUNT", "fn": "sum" } ] }
        ]
      },
      "pivot": {
        "from": "base",
        "schema": null,
        "composables": [
          { "kind": "pivot", "rows": [ "CUSTOMER" ], "cols": [ "STATUS" ],
            "values": [ { "id": "ir2", "col": "AMOUNT", "fn": "sum" } ] }
        ]
      },
      "chart": {
        "from": "base",
        "schema": null,
        "composables": [
          { "kind": "chart", "type": "bar", "label": "CUSTOMER",
            "value": "AMOUNT", "fn": "sum" }
        ]
      }
    }
  }
}
```

`tables` is an unordered map of opaque identifiers. A table reads either the
configured SQL (`"from": "definition"`) or another named table, then applies its
`composables` according to their declared semantics. Array position is serialization,
not phase ordering. When `from` names a table, the server completes that parent,
wraps its final SQL as the child's source relation, and carries its output schema and
column metadata forward. The same rule repeats recursively, to a bounded depth, so
Group, Pivot, and Chart results can themselves feed later tables. Names such as `base`,
`groupBy`, or `pivot` have no engine meaning; they are only names the packaged UI tends
to choose for the simple sibling tables it authors.

Compute, filter, Group, Pivot, and Chart composables change the relation available to a
child. Labels and formats change its column metadata. Select, sort, highlight, break,
and aggregate describe the response when their owning table is active; they do not
pretend to be ordered rows or footer datasets inside a derived SQL table. Consequently,
the `base` table's filter above participates in `pivot`, while its visible-column and
sort choices remain the base table's own terminal presentation. `definition` is the
sole reserved input sentinel and cannot be a table key; every other nonblank,
case-unique id is opaque. The packaged client preserves valid deeper compositions from
other clients even when they do not map to one built-in toolbar mode.

Authored computed columns and Group/Pivot metrics share one document-wide synthetic
identity namespace: `ir1`, `ir2`, and so on. IDs are persisted when a column is
authored and never depend on composable array order. Dynamic Pivot cells also use
opaque server-issued `irN` identities; clients consume the returned schema instead of
deriving cell names from metric labels or Pivot values.

Each table's optional `schema` is a non-authoritative cache of its completed public
relation before terminal `select` visibility is applied. New documents may omit it or
set it to null. The client nulls a changed table's cache and every transitive descendant
cache; search changes also invalidate dynamic descendants. On submission the server
recursively fills every null cache from live compilation. Query results include the
enriched state as `document` alongside the requested rows and metadata, and the client
adopts that returned document. The server also replaces non-null caches for the active
table and any ancestor it compiled on the way, so returned working data agrees with the
live plan. Dormant caches remain advisory and are never used for expression binding,
query planning, or authorization.

The server accepts at most 64 tables, a maximum `from` depth of 64, and 512 composables
per document. Within the selected composition, the stacked limits are 20
computed-column rules, 50 filter rules, and 50 highlight rules. Pivot and Chart caps
provide additional bounds for data-dependent relations.

With saved-report storage configured, every configured file receives a database-generated
numeric identity. Its row is the optimistic catalogue authority for the report key,
source filename, display title, and default flag; only the state body is read from disk
when the document is retrieved. Configured titles are deployment declarations and may
duplicate any public, private, or other configured title. File content remains read-only,
although Save As can create an editable database copy. At most one file per report may
set `default: true`. Whenever one family's reports are listed, the server first loads that
database family's complete, unfiltered contents in one query, compares the snapshot with
appsettings in memory, and repairs configured-file discrepancies. Only then does it apply
administrator/public/exact-owner visibility for the response. Discovering
the configured default removes the synthetic default and gives the
file-backed row the default role. Without a configured file default, the server lazily
creates a synthetic default row. Invalid database-backed default state is rebuilt in place
from current configuration, retaining the same id. If a referenced file is missing during
retrieval, its stale database row is deleted, a synthetic default is inserted when the
missing row was the default, and the vanished numeric id returns 404. Ensure the host
project copies the referenced files to its build and publish output; the Workbench project
shows one way to do that. A present configured document whose state fails processing is
different: its identity is deleted and retrieval returns 404, but its declaration remains
authoritative, so no synthetic fallback is created. The next report listing inserts a new
identity and retries it. Each failed attempt is logged with its family, id, source file, and
exception. A configured-identity or synthetic-default insert that leaves the family without
a default is also logged and returns 404; the next family listing retries bootstrap.

Auto-created stores create the report-document and adjacent `IR_REPORT_AUTHORIZATION`
tables. This release uses the replacement schema and does not upgrade an older table in
place. `savedReports.tablePrefix` is prepended to both physical storage table names.
Hosts with `savedReports.autoCreate: false` must provision both current schemas.

Every report family has exactly one default document, and that document is public.
Administrators can select an ordinary database report as the new default; the operation
publishes it globally and retains the previous default as an ordinary global report.
A configured file marked `default: true` owns default selection until configuration changes.

The packaged administration panel discovers appsettings families from `GET /api/reports`,
then loops over `GET /api/reports/{name}` to reconcile and enumerate their report documents.
It presents saved-report administration through an embedded
`<interactive-report>` bound to the built-in, administrator-only `__saved-reports`
definition — the listing is a report like any other, so searching, sorting, column
tools, pagination, and CSV export all apply to it. Per-row actions (Publish/Unpublish,
Make default, Reassign, State, Download, Delete) are action-renderer cells. Configured
rows remain read-only, including their configuration-owned default selection. Report
names beginning with `__` are reserved. A definition
may also declare `"authorization": { "administratorsOnly": true }` to restrict any
report to configured or database administrators the same way. Because the admin element
nests the report inside its own shadow root, theme tokens set on
`<interactive-report-admin>` do not reach the embedded listing; it renders with the
packaged default theme.

Applications may supply the administration screen with an Oracle-style user list of
values. Each entry has display text and a canonical string value; the value is what
Interactive Reports stores as the saved-report owner and should use the same identity
form shown by `whoami`. Implement the optional directory and register it through the
builder:

```csharp
public sealed class ReportUsers : IInteractiveReportUserProvider
{
    public ValueTask<IReadOnlyCollection<InteractiveReportUser>?> GetUsers(
        ClaimsPrincipal administrator,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyCollection<InteractiveReportUser>?>(
        [
            new("Ada Lovelace", "ada-id"),
            new("Grace Hopper", "grace-id"),
        ]);
}

builder.Services
    .AddInteractiveReports(builder.Configuration)
    .UseUserProvider<ReportUsers>();
```

The provider can be scoped and receives the authenticated administrator. Its order is
preserved. Returning `null` or an empty collection, or not registering a provider,
keeps the Reassign Owner dialog as free-form text. The protected
`GET {prefix}/admin/users` endpoint invokes the provider only after the caller passes
the administration gate; it emits the `ListAuthorizationUsers`
application-authorization action. The directory supplies choices, not authority: it
does not change `whoami`, administrator matching, or operation authorization. The Authorization editor reuses
these stable values when it creates real grants.

### Built-in administrator and report authorization

Database authorization is stored in `IR_REPORT_AUTHORIZATION` beside the saved-report
table. It uses the required saved-report target and optional shared prefix. Change its
physical table name only when an operator-managed schema requires another identifier:

```json
{
  "InteractiveReport": {
    "Administrators": [ "bootstrap-admin-id" ],
    "SavedReports": {
      "dataSource": "MainDb",
      "tablePrefix": "MYAPP_"
    },
    "Authorization": {
      "TableName": "IR_REPORT_AUTHORIZATION"
    },
    "Reports": {
      "orders": {
        "dataSource": "MainDb",
        "sql": "SELECT * FROM ORDERS",
        "authorization": {
          "restricted": true,
          "users": [ "orders-user-id", "finance-user-id" ]
        }
      }
    }
  }
}
```

`restricted: true` limits the report to explicitly granted canonical identities.
Configuration users and users added through **Authorization…** are an ordinal,
case-sensitive union because identity-provider subject values are opaque. The editor
may also restrict a report whose configuration leaves `restricted`
false; configured users can therefore be staged before the database restriction is
enabled. A configuration restriction cannot be removed in the editor. Anonymous and
`administratorsOnly` reports cannot also use named-user restrictions.

Top-level `Administrators` and administrators added in the editor are likewise
additive. Once either source contains an administrator, that union is authoritative;
application authorization can restrict it but cannot promote an identity outside it.
Administration permission does not implicitly grant access to a restricted report.
Grant the administrator that report separately when its data should also be visible.
Configuration entries remain read-only in the editor; database entries can be added or
removed there.

The panel can download any listed report as the canonical `{ title, default, state }`
JSON envelope. This makes a database-backed saved report ready to add to
`documentFiles` without manually reconstructing the file. **Upload JSON…** takes a
numeric default document selection and one of these envelopes, validates its state
against that report's current schema through the same ingestion pipeline as query and
export, and imports it as the administrator's private saved report for live testing.

## Application authorization

Interactive Reports describes every protected operation with an action, the ASP.NET
Core `ClaimsPrincipal`, and current/proposed resource metadata. Create and update
authorization receives a mutable, typed `InteractiveReportDefinition`, including the
typed `ReportState` object graph. It makes no assumption
about which client called the endpoint or how the caller reached it. Integrators can
use any of three equivalent styles:

1. A direct `UseAuthorization(...)` callback.
2. `UseAspNetCoreAuthorization()` with a typed ASP.NET Core resource handler.
3. A `UseAuthorization(...)` callback that delegates to named ASP.NET Core policies.

The direct callback is a resource decision:

```csharp
var reports = builder.Services.AddInteractiveReports(builder.Configuration);

reports.UseAuthorization((request, cancellationToken) =>
{
    if (request.Action == InteractiveReportAction.CreateSavedReport
        && request.Resource.Definition is { } definition)
    {
        // Keep the save, but discard the client's request to publish it.
        definition.Public = false;
    }

    return ValueTask.FromResult(ApplicationReportAcl.Allows(
        request.User,
        request.Action,
        request.Resource));
});
```

The mutated definition is revalidated against the dataset schema and then persisted.
For updates, metadata is complete but `State` is populated only when replacement state
was submitted; otherwise the existing stored JSON remains untouched. Reads continue to
return stored state as JSON without typed rehydration.

Built-in report visibility, named-user restrictions, and saved-report ownership remain
in force. The union of configured and database administrators is authoritative when
nonempty: listed identities remain eligible for administrator operations and
application authorization may restrict them further, while an application callback
cannot promote an identity outside that union. When both administrator sources are
empty, operations requiring administrator authority need an affirmative application
authorization decision. With neither mechanism configured, they fail closed. `false`
or `InteractiveReportAuthorizationDeniedException` is an
expected denial; cancellation remains cancellation, and other exceptions are logged
and returned as a sanitized 500 response.

Every client delegates authorization to the server's transport-neutral
`IReportAuthorizationService`. JSON, GraphQL, and file-download packages own their HTTP
mapping and response semantics; the central service owns definition gates, saved-report
resources, application authorizers, administrator decisions, feature gates, and trusted
context parameters without depending on `HttpContext` or `IResult`. The opt-in `whoami`
bootstrap diagnostic is the deliberate exception: it reports the current principal while
an operator is determining which identity belongs in configuration, and grants no authority.

Saved-report decisions are resource-based: public, owner, or administrator may read;
owner or administrator may update title/state or delete; and the explicit global,
default-selection, ownership, list-all, authorization-management, upload, and download actions
require administrator authority. The API applies these rules regardless of which
client issued the request. Ordinary saved-report summaries expose only the derived
`mine` flag, not the canonical owner identity; ownership remains a database column and
is available to the protected administration report and authorization callbacks.

See [Authorization](AUTHORIZATION.md) for complete setup examples for all three
styles, the action/resource reference, multi-action composition, administrator
resolution, denial status codes, UI hints, migration guidance, and recommended tests.

## GraphQL adapter

`InteractiveReport.Client.GraphQL` is an optional query-only adapter built on GraphQL.NET.
It executes any saved report from the same `ISavedReportStore` used by the HTTP API;
database-authored and configured file-backed reports follow the same lookup and access
rules. Add and map it separately so applications that do not use GraphQL acquire no
GraphQL dependency:

```csharp
using InteractiveReport.Client.GraphQL;
using InteractiveReport.Client.Json;

builder.Services.AddInteractiveReports(builder.Configuration);
builder.Services.AddInteractiveReportJson();
builder.Services.AddInteractiveReportGraphQL();

app.MapInteractiveReportJson("/api/reports");
app.MapInteractiveReportGraphQL("/graphql");
```

Execute a saved report by the id returned from the ordinary saved-report API:

```graphql
query ExecuteSavedReport($id: ID!) {
  report(id: $id, page: 1, pageSize: 100) {
    columns { name label type computed }
    rows
    page { index size }
    totalRows
    elapsedMs
  }
}
```

The adapter accepts every saved-report origin. File-backed reports are strongly
recommended for durable GraphQL consumers because ordinary users cannot modify their
state; administrators update the source file deliberately. The resolver enforces both
`ReadSavedReport` and `Query`, including ownership, publication, report-definition,
context-parameter, and application authorization rules. One operation may execute only
one `report` root response key, including through aliases and fragments. See
[GraphQL adapter](GRAPHQL.md) for the complete contract and operational notes.
The Workbench also installs GraphiQL at `/graphiql` in Development so its schema can be
explored and saved-report queries can be executed in the browser.

## Logging

Interactive Reports uses one optional host-owned logger for its entire mapped request
pipeline. Supply it when mapping the endpoints:

```csharp
var reportLogger = app.Services.GetRequiredService<ILoggerFactory>()
    .CreateLogger("InteractiveReport");
app.MapInteractiveReportJson("/api/reports", reportLogger);
```

An already-created logger may instead be supplied during service registration with
`.AddInteractiveReports(...).UseLogger(logger)`. If neither location supplies one,
the package is silent: it creates no provider, console output, file, or other logging
side effect. `Information` records startup validation, every mapped request and its
status/duration, report queries and exports, and configured-document synchronization.
`Debug` adds authorization decisions, schema-cache activity, and exact submitted SQL.
Errors retain the request trace id. Request bodies and SQL parameter values are never
logged.

The supplied logger owns filtering and destinations. For the category created above,
Debug can be enabled with:

```json
"Logging": {
  "LogLevel": {
    "InteractiveReport": "Debug"
  }
}
```

## Error responses

Every JSON API failure uses the same `InteractiveReportError` wire shape. `code` and
`description` are required; `title`, `details`, and `traceId` are omitted when they do
not apply:

```json
{
  "code": "IR-1201",
  "description": "One or more report settings are invalid.",
  "title": "Report state failed validation",
  "details": "tables.orders.composables[0].filters[0].expr: unknown column 'OLD_NAME'"
}
```

The `IR-nnnn` catalog behaves like a product-specific ORA series: each code is a stable,
language-independent core message identity. Context such as paths and rejected values
lives in `details`, which is not translated. Known codes replace the server's English
title and description; unknown codes retain that fallback so client and server versions
can be deployed independently. Unexpected server failures remain sanitized and add
`traceId` for correlation with the server log.

## Localization

The packaged report and administration components localize their complete static UI in
English (`en`) and Canadian French (`fr-CA`). Set `lang` on the component, or on one of
its ancestors:

```html
<interactive-report lang="fr-CA" report="orders"></interactive-report>
```

The nearest `lang` value wins, including across the component's shadow root. The page
language is used next, then browser preferences when the page has no language, with
English as the final fallback. All French variants currently resolve to the Canadian
French catalog. Toolbar and menu text, dialogs, validation, notices, coded errors,
accessible labels, plural messages, and client-formatted numbers and dates follow the
selected locale. Report titles, column labels, query data, and server error `details`
remain application data and are displayed as supplied.

The packaged `{prefix}/{name}/view` and `{prefix}/admin` pages set their document
language from ASP.NET Core Request Localization when configured, then from the request's
`Accept-Language` header. Their page title and JavaScript fallback copy use the same
locale.

Catalogs live in `src/client/locales`; stable semantic message keys and ICU message
syntax keep component code independent of sentence structure. `intl-messageformat` is
compiled into the packaged bundles, so consuming applications do not install an npm
package or make a separate catalog request.

## Formats, numbers, and report behavior

Number masks cover grouped integers and one through four fixed decimal places,
invariant two-place decimals, CAD/USD/EUR/GBP/JPY currency, and zero through two-place
percentages. Date masks cover ISO dates and date-times, localized medium/long dates,
localized times, and localized medium/long date-times. The same scalar formatter is
used by text cells, link text, aggregates, group and pivot values, and the chart's
accessible data table. A synthetic group/pivot/chart metric inherits the format of its
source column; count-only metrics have no source format.

Query JSON carries CLR `Int64`, `UInt64`, and `Decimal` values as invariant strings.
The column metadata still identifies them as numbers, and the client parses, rounds,
groups, and masks every number-like column value through bundled `big.js` arbitrary-
precision arithmetic. Values such as
`9007199254740993` therefore do not lose a digit in JavaScript. Chart pixel coordinates
are the sole exception: Chart.js requires a JavaScript number, while its accompanying
data table retains the exact formatted value.

Numeric aggregate pickers include median alongside sum, average, minimum, maximum,
count, and distinct count. Median uses the same grouped query path for report totals,
control-break totals, Group By, pivot, and chart metrics on every supported database.

The Pivot dialog's **Show total rows** option adds terminal aggregate rows below the
matrix. Those values are re-aggregated from the Pivot's completed input relation
instead of adding displayed
cells, so averages, medians, distinct counts, and null handling remain correct. It
does not synthesize a right-side total column, which may require report-specific rules
such as excluding cancelled orders.

A saved report retains its complete table map. The packaged UI normally authors a
`definition`-backed base table plus Group By, Pivot, and Chart tables whose `from`
points at that base, then switches among them by changing `activeTable`. Only the
selected table and its recursive inputs are executed for rows. Other valid tables
remain in the document, including deeper or multi-compositor relations created by
another client that do not map to one built-in toolbar mode.

Group By produces a complete relation rather than a display-only summary. Its filters
run after grouping, and its computed columns, sorts, highlights, control breaks, and
footer aggregates bind to the Group output
(`dimensions + __count + metrics + computed`). The
same aggregate list supplies the whole-table footer and each control-break subtotal, as
it does on a relation without Group. A Group break counts Group rows; aggregating `__count` reports
the corresponding number of filtered source rows. In an externally authored chain
where a Group dimension or metric already owns `__count`, the generated count column
gains leading underscores until its name is unique; the returned schema is authoritative.

Server resource ceilings allow deep external compositions without leaving expansion
unbounded: 64 tables, depth 64, 512 composables, 256 shape metrics, 900 completed
columns, and 1,800 generated predicates per Pivot. Relational stage depth is 256 in
general and 22 on SQL Server, reserving space for terminal query wrappers. Every
complete command, on every supported dialect, is capped at 2,000 cumulative bound
parameters including context values. Definition-level row, Pivot-column, Pivot-group,
and Chart-point limits still apply.

Save updates the selected saved report. Save As creates a new report when its name is
unused; when the name matches an editable report, it asks for confirmation and replaces
that report instead. Titles are case-insensitively unique in the caller's view: a private
title conflicts with public documents and that owner's private documents, but not with
another owner's private documents. Public and Private selector groups make a later
public/private duplicate unambiguous, and an existing private document remains editable.
Configured file synchronization bypasses these save-time title rules, leaving deployment
collisions for the programmer to resolve; numeric ids and selector groups keep them
addressable.

Administrators may select an ordinary saved report as the new default. That replacement
is atomic, makes the selected report global, and leaves the former default global. A
configured file marked `default: true` can be changed only through application configuration.

Highlights have a report-facing name and a positive sequence. Matching rules are
applied from lower to higher sequence, so the highest sequence wins when rules set the
same style; cell highlights are applied after row highlights. When these optional fields
are omitted, the rule id becomes the name and stable increments of ten supply sequence.
