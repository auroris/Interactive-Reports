# InteractiveReports

APEX-style interactive reports for ASP.NET Core. A **report definition** is
developer-owned configuration — a name, a data source, and a SELECT; from it users
get an auto-generated **default report** (every column the SELECT brings up, with
search, filters, sorting, control breaks, computed columns, highlighting, group by,
pivot, charts, and CSV export) and can layer their own **saved reports** on top.
The browser UI ships inside the package as a custom element plus ready-made pages —
consumers need no Node.js and no frontend build.

## Getting started

1. Add the package:

   ```sh
   dotnet add package InteractiveReport.AspNetCore
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

3. Wire it up in `Program.cs` — two lines:

   ```csharp
   builder.Services.AddInteractiveReports(builder.Configuration);
   // …
   app.MapInteractiveReports("/reports");
   ```

4. Done — browse **`/reports/orders/view`**. The packaged page hosts the report;
   embedding `<interactive-report>` in your own pages (below) remains the primary
   path for real applications. Saved reports and administration require the explicit
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

`MapInteractiveReports` publishes standard ASP.NET Core endpoint summaries, tags,
request-body types, response types, and problem response metadata. A host can add its
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
  "sql": "SELECT ORDER_ID, CUSTOMER, CUSTOMER_URL, THUMBNAIL_URL, AMOUNT FROM ORDERS",
  "styleSheet": "/css/orders-report.css",
  "documentFiles": [
    "ReportDocuments/orders.primary.json",
    "ReportDocuments/orders.finance.json"
  ]
}
```

Each file contains its selector title, an optional initial primary flag, and the
normal versioned state document:

```json
{
  "title": "Default",
  "primary": true,
  "state": {
    "v": 2,
    "columns": [ "ORDER_ID", "CUSTOMER", "THUMBNAIL_URL", "AMOUNT" ],
    "sorts": [ { "col": "AMOUNT", "dir": "desc" } ],
    "formats": {
      "CUSTOMER": {
        "displayAs": "link",
        "urlColumn": "CUSTOMER_URL",
        "textColumn": "CUSTOMER"
      },
      "THUMBNAIL_URL": {
        "displayAs": "image",
        "urlColumn": "THUMBNAIL_URL"
      },
      "AMOUNT": { "mask": "currency:CAD", "classes": [ "amount-column", "emphasized" ] }
    }
  }
}
```

With saved-report storage configured, all files appear as global saved reports and are synced into the saved-report store
whenever they change, as rows marked with a configured origin. A file's `primary`
value seeds the row when it is first synchronized; after that an administrator can
flag or unflag it without editing the file. File content remains read-only, although
Save As can create an editable database copy under another title. A configured title
takes precedence over an existing database report with the same title, and new title
collisions are rejected. Ensure the host project copies these files to its build and
publish output; the Workbench project shows one way to do that.

Auto-created stores add `IS_PRIMARY` to an existing current-shape table in place and
create the adjacent `IR_REPORT_AUTHORIZATION` table. `savedReports.tablePrefix` is
prepended to both base table names. Hosts with
`savedReports.autoCreate: false` must manage both tables and add the non-null 0/1
saved-report column themselves.

Primary is an administrator-controlled publication flag. Every primary report is
visible to anyone who can access the underlying dataset. The generated report named
`Default` always exists; a stored primary report whose title is `Default`
(case-insensitive) replaces that generated state. Unflagging or deleting it restores
the generated Default. Other primary reports remain selectable alternatives.

The packaged administration panel lists every saved report through an embedded
`<interactive-report>` bound to the built-in, administrator-only `__saved-reports`
definition — the listing is a report like any other, so searching, sorting, column
tools, pagination, and CSV export all apply to it. Per-row actions (Publish/Unpublish,
Make primary/Unflag, Reassign, State, Download, Delete) are action-renderer cells;
configured rows permit the primary action while their file-backed content remains
read-only. Report names beginning with `__` are reserved. A definition
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
base table name only when an operator-managed schema requires another identifier:

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

The panel can download any listed report as the canonical `{ title, primary, state }`
JSON envelope. This makes a database-backed saved report ready to add to
`documentFiles` without manually reconstructing the file. **Upload JSON…** takes a
configured report name and one of these envelopes, validates its state against that
report's current schema through the same ingestion pipeline as query and export, and
imports it as the administrator's saved report for live testing. The envelope's
`primary` flag is preserved as the stored primary publication flag.

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

All mapped report and security-administration endpoints enter `IReportAccessService`
once before their protected work. Host-owned endpoints can resolve that service to use
the same boundary. The opt-in `whoami` bootstrap diagnostic is the deliberate
exception: it reports the current principal while an operator is still determining
which exact identity value belongs in configuration, and it grants no authority.

Saved-report decisions are resource-based: public, owner, or administrator may read;
owner or administrator may update title/state or delete; and the explicit global,
primary, ownership, list-all, authorization-management, upload, and download actions
require administrator authority. The API applies these rules regardless of which
client issued the request. Ordinary saved-report summaries expose only the derived
`mine` flag, not the canonical owner identity; ownership remains a database column and
is available to the protected administration report and authorization callbacks.

See [Authorization](docs/AUTHORIZATION.md) for complete setup examples for all three
styles, the action/resource reference, multi-action composition, administrator
resolution, denial status codes, UI hints, migration guidance, and recommended tests.

## GraphQL adapter

`InteractiveReport.GraphQL` is an optional query-only adapter built on GraphQL.NET.
It executes any saved report from the same `ISavedReportStore` used by the HTTP API;
database-authored and configured file-backed reports follow the same lookup and access
rules. Add and map it separately so applications that do not use GraphQL acquire no
GraphQL dependency:

```csharp
using InteractiveReport.GraphQL;

builder.Services.AddInteractiveReports(builder.Configuration);
builder.Services.AddInteractiveReportGraphQL();

app.MapInteractiveReports("/api/reports");
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
context-parameter, and application authorization rules. See
[GraphQL adapter](docs/GRAPHQL.md) for the complete contract and operational notes.
The Workbench also installs GraphiQL at `/graphiql` in Development so its schema can be
explored and saved-report queries can be executed in the browser.

To log the exact SQL command text submitted to the configured database, enable Debug
for the report executor category. Parameter values are never logged:

```json
"Logging": {
  "LogLevel": {
    "InteractiveReport.Core.Execution.ReportExecutor": "Debug"
  }
}
```

`styleSheet` is an application-controlled relative or HTTP(S) URL. The component
places its `<link>` inside the report's shadow root after the packaged styles, so its
rules can target report internals without leaking into the host page. For example:

```css
.amount-column { font-variant-numeric: tabular-nums; }
.emphasized { font-weight: 700; }
```

Column Settings accepts space-separated class names and saves them in the column's
`formats.classes` list. Classes apply to the column header, data cells, and aggregate
cells. Tokens begin with a letter or `_`, then use letters, digits, `_`, or `-`; the
component's `ir-` prefix is reserved. Report documents can therefore select
developer-defined rules but cannot supply CSS or choose a stylesheet URL. The host's
Content Security Policy still governs stylesheet loading.

Column Settings also offers `Text (Default)`, `Link`, and `Image` display modes.
A link selects a URL column and a text column; an image selects a URL column. Source
columns do not have to be visible: the server schema-checks them and includes them only
in row data required by the grid and CSV renderers. Hidden sources remain absent from
displayed and exported column metadata. In CSV, a Display As cell contains the encoded
HTML fragment shown in the browser: `<a class="ir-cell-link">` or
`<img class="ir-cell-image">`; ordinary cells remain raw values. Relative and HTTP(S)
URLs are accepted for both renderers;
links additionally accept `mailto:` and `tel:`. Active or embedded-content schemes
such as `javascript:` and `data:` render as ordinary text instead.

A fourth renderer, `displayAs: "action"`, is definition-authored only (Column
Settings never offers it, but preserves it across restyles): the cell's value is a
button label — a NULL label renders no button — and clicking dispatches a composed
`ir-action` CustomEvent from the report element with `{ command, row, column }`,
where the row includes the format's schema-bound `keyColumn` value. The built-in
admin listing is its first consumer; in CSV an action cell exports its raw label.

A definition can also declare an APEX-style edit pencil and per-column overrides —
configuration, not report state:

```json
"orders": {
  "connection": "MainDb",
  "sql": "SELECT ORDER_ID, CUSTOMER, AMOUNT, NOTES FROM ORDERS",
  "editLink": {
    "urlTemplate": "/orders/{ORDER_ID}/edit",
    "label": "Edit order"
  },
  "columns": {
    "AMOUNT": { "helpText": "Order total before tax." },
    "NOTES": { "hideLabel": true, "sortable": false, "filterable": false }
  }
}
```

`editLink` renders a leading pencil column in grid view. Its `{COLUMN}` placeholders
reference base schema columns; the referenced values travel as hidden row data (like
renderer source columns), are URL-encoded into the template client-side, and the
result is an ordinary anchor — middle-click and open-in-new-tab work, `target:
"_blank"` adds `rel="noopener"`, and a row whose placeholder value is NULL shows no
pencil. The edit column has an empty heading (its `label` is the accessible name and
tooltip), never appears in column pickers, search, sorts, or filters, and is absent
from CSV exports and grouped/pivoted/charted views.

`columns` overrides one column at a time: `label` replaces `columnLabels` for that
column (configuring both is rejected), `hideLabel` blanks the table heading while
menus and dialogs keep the real name, `sortable: false` / `filterable: false` remove
those controls (control breaks count as sorting; computed columns are always exempt),
and `helpText` appears as a note in the column's header menu. The server also strips
sorts, breaks, and filter rules that violate the restrictions from incoming documents
into `ignored[]`, so saved reports created before a restriction degrade gracefully
instead of erroring.

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

The Pivot dialog's **Show total rows** option adds aggregate rows below the matrix.
Those values are re-aggregated from the filtered source instead of adding displayed
cells, so averages, medians, distinct counts, and null handling remain correct. It
does not synthesize a right-side total column, which may require report-specific rules
such as excluding cancelled orders.

A saved report retains every configured view. Grid, Group By, Pivot, and Chart can be
switched without rebuilding their settings; the view selected when the report is saved
is the one it opens with. Only the selected view is validated and executed. Settings
belonging to inactive views remain available without producing ignored-setting notices
or validation failures.

Save updates the selected saved report. Save As creates a new report when its name is
unused; when the name matches an editable report, it asks for confirmation and replaces
that report instead. Saved-report titles are case-insensitively unique per report.

Highlights have a report-facing name and a positive sequence. Matching rules are
applied from lower to higher sequence, so the highest sequence wins when rules set the
same style; cell highlights are applied after row highlights. Legacy documents without
these fields use the rule id as the name and list position in increments of ten as the
sequence.

## Embedding the report

The bundle contains the component's styles and renders into a shadow root, so
host styles such as Tailwind resets do not reach the widget and widget styles do
not reach the host page.

```html
<script type="module" src="/assets/ir.js"></script>
<interactive-report
  report="open-orders"
  saved-report="My Open Orders"
  api-base="/api/reports">
</interactive-report>
```

`report` is required and is the only report-definition name the component requests.
There is no configured-report catalog or report selector. Server authorization still
applies when the component requests that report's schema, saved reports, queries, and
exports.

The widget adapts to the definition's `features` whitelist (docs/ARCHITECTURE.md §4):
menu entries, view buttons, the search bar, and the saved-report select render only
for whitelisted features, and the server additionally refuses CSV export and
saved-report creation for reports that do not whitelist `download` / `savedReports`.

A definition's `editLink` pencil navigates like an ordinary anchor — relative
templates resolve against the host page, and routing to the edit form is entirely
the host application's concern.

Page size is configured through **Actions → Pagination**. The standard choices are
10, 50, 100, 500, 1000, and All; numeric choices above a definition's `maxPageSize`
are omitted. All is stored as `page.size: 0`. A positive definition `maxRows` is a
hard response cap for All grid rows, All Group By groups, and CSV export regardless of
the client request. Set `maxRows` to `0` or a negative number for unlimited results.
CSV export reports a positive-cap truncation through `X-IR-Truncated`.

Each row in **Actions → Sort** also offers Nulls: Default, First, or Last. First
and Last are stored on that sort instruction as `nulls: "first"` or `"last"` and
produce the same placement on every supported database. Default omits the field and
preserves the database dialect's ordinary ordering behavior. Header-menu quick sorts
continue to use Default.

Control-break columns render in the break heading instead of repeating in each detail
row. A subtotal appears only with the logical end of its break group, even when that
group crosses a page boundary, and grand totals appear only on the report's final page.

`saved-report` is optional. When present, the component finds a visible saved report by
its title (case-insensitive) and loads it before the first query. The title must identify
exactly one visible saved report. A missing or ambiguous title loads Default and shows
a warning. Omit the attribute to start from Default.

`api-base` may be a relative path or an absolute URL. If it is omitted, the
component infers the API prefix from the script URL. The older `base` attribute
remains available as an alias. Theme tokens such as `--ir-accent` can be set on
the custom element without exposing its internal CSS.

```css
interactive-report {
  --ir-accent: #7c3aed;
  --ir-accent-soft: #f3e8ff;
  --ir-font: Inter, system-ui, sans-serif;
  --ir-radius: 0.5rem;
}

interactive-report::part(toolbar) {
  padding: 1rem;
}
```

The supported theme properties are `--ir-accent`, `--ir-accent-soft`,
`--ir-border`, `--ir-border-light`, `--ir-bg`, `--ir-bg-soft`,
`--ir-bg-header`, `--ir-text`, `--ir-text-muted`, `--ir-danger`,
`--ir-radius`, `--ir-font`, and `--ir-font-size`, plus the chart tokens
`--ir-chart-1` … `--ir-chart-8` (categorical palette; slot 1 is also the
single-series color), `--ir-chart-grid`, and `--ir-chart-text`. The supported
structural parts are `surface`, `toolbar`, `notices`, `chips`,
`table-container`, `chart-container`, `table`, `pager`, `menu`,
and `dialog`. Editor dialogs are movable, modeless windows, so the report remains
available while they are open. Short destructive confirmations remain modal.

## Developing

The client-side source is in `src/client`; generated browser assets are written to
`src/InteractiveReport.AspNetCore/Ui/dist` for embedding by the server project. Install
the toolchain and create the browser bundles with:

```sh
npm ci
npm run build
```

`npm run dev` rebuilds on changes. `npm test` builds the client and runs the fast
DOM unit tests. Browser automation is configured with Playwright; install Chromium
once with `npx playwright install chromium`, then run `npm run test:ui`.
`npm run verify` runs both test layers.

The generated `src/InteractiveReport.AspNetCore/Ui/dist/ir.js`, `ir-admin.js`, and
`ir-chart.js` files are embedded in the ASP.NET Core assembly and are deliberately
not committed. A source checkout must run the client build before a Release build,
a pack, or a run of the packaged UI — `dotnet pack` and `dotnet build -c Release`
fail with instructions when the bundles are missing, so a UI-less package cannot
ship silently. `scripts/pack.ps1` (also `npm run pack`) chains the client build, the
fast test layers, and `dotnet pack` for the three distributable projects into
`artifacts/packages`; publishing beyond that is currently a manual
`dotnet nuget push` (release automation is deliberately still open).
Package consumers never need Node.js. `ir-chart.js` (the Chart.js-based chart
renderer) is fetched on demand the first time a report enters chart view; pages
that never chart never load it.
