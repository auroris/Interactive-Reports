# GraphQL adapter

`InteractiveReport.Client.GraphQL` is an optional, query-only GraphQL.NET transport for
discovering and executing saved Interactive Reports. It does not replace the HTTP API,
report engine, saved-report store, or authorization system.

It covers the same read path the packaged JavaScript client walks: list the report
configurations the caller may view, list the saved documents of one configuration, then
execute one document with the caller's paging, search, and sorting applied. Creating and
editing saved reports remains a REST and packaged-UI operation.

## Installation and mapping

Reference the `InteractiveReport.Client.GraphQL` package in addition to
`InteractiveReport.AspNetCore`, then register and map its schema:

```csharp
using InteractiveReport.AspNetCore;
using InteractiveReport.Client.GraphQL;
using InteractiveReport.Client.Json;

var reports = builder.Services.AddInteractiveReports(builder.Configuration);
builder.Services.AddInteractiveReportJson();
builder.Services.AddInteractiveReportGraphQL();

var app = builder.Build();
app.MapInteractiveReportJson("/api/reports");
app.MapInteractiveReportGraphQL("/graphql");
```

The path defaults to `/graphql` and can be changed. The mapping supports HTTP GET and
POST queries. It disables mutations, batched requests, form posts, subscriptions, and
WebSockets. The package does not bundle a GraphQL IDE.

The Workbench adds the separate `GraphQL.Server.Ui.GraphiQL` package and maps its
browser IDE at `/graphiql` in Development. Keeping the tool in the host avoids adding
UI middleware to applications that only need the transport. A host can use the same
query-only setup:

```csharp
using GraphQL.Server.Ui.GraphiQL;

if (app.Environment.IsDevelopment())
{
    app.MapGraphQLGraphiQL("/graphiql", new GraphiQLOptions
    {
        GraphQLEndPoint = "/graphql",
        GraphQLWsSubscriptions = true,
    });
}
```

`GraphQLWsSubscriptions = true` selects GraphiQL's current subscription fetcher. It
does not enable subscriptions in Interactive Reports; it prevents the legacy fetcher
from opening a WebSocket during ordinary query and schema-introspection operations.
Unsupported methods and WebSocket upgrades receive HTTP 405 from the adapter.
Configured document identities are reconciled by report listing rather than application
startup, so a freshly started Workbench has no ids to paste. Run `{ reports { name } }`
and then `{ savedReports(report: "...") { id title } }` in GraphiQL to reconcile them
and pick one.

`MapInteractiveReportGraphQL` returns an `IEndpointConventionBuilder`, so standard
ASP.NET Core conventions remain available:

```csharp
app.MapInteractiveReportGraphQL("/graphql")
    .RequireRateLimiting("reports");
```

Authentication middleware must run before the mapped endpoint when the host uses it.
Interactive Reports does not select an authentication scheme.

## Query contract

The schema has three root fields, mirroring the REST read path:

```graphql
type Query {
  reports: [InteractiveReportConfiguration!]!
  savedReports(report: String!): [InteractiveReportSavedReport!]!
  report(
    id: ID!
    report: String
    page: Int
    pageSize: Int
    search: String
    sort: [InteractiveReportSortInput!]
  ): InteractiveReportResult
}
```

Each operation may contain at most one executable root response key. Every field above
reaches the database, so aliases and root fragments do not permit one HTTP request to fan
out into multiple catalogue, listing, or report executions; such an operation fails
GraphQL validation before any resolver runs. Introspection meta-fields (`__schema`,
`__type`, `__typename`) answer from the schema and are exempt. An operation may expand at
most 256 fragment-spread visits across its reachable fragment graph; documents above that
ceiling fail before authorization or execution, including when parsed documents are
cached. The adapter also caps its schema's execution concurrency at one without changing
other GraphQL schemas registered by the host.

### Discovery

`reports` is the GraphQL twin of `GET /api/reports`: the appsettings report
configurations the current caller may view, ordered by title. It lists configurations,
not documents. A caller denied every configuration receives that first denial as an
error rather than an empty list, so a hidden catalogue is never mistaken for an empty one.

`savedReports(report:)` is the twin of `GET /api/reports/{name}`: the documents of one
configuration that the caller may load — public, default, configured, and caller-owned —
with administrators receiving the complete family. Listing reconciles configured file
identities and creates the family's default document if it is missing, exactly as the
REST route does. An unknown or hidden configuration returns `NOT_FOUND`.

```graphql
{
  savedReports(report: "orders") {
    id
    title
    isDefault
    isGlobal
    mine
    isReadOnly
    modifiedUtc
  }
}
```

Each `id` is accepted directly by the `report` field. Like every GraphQL `ID` it is a
string on the wire; the REST listing carries the same value as a JSON number, because
report-document identities are database keys rather than query result values.

### Execution

`id` is the database-generated report-document id returned by `savedReports`,
`GET /api/reports/{name}`, and the ordinary creation APIs. It is unique across report
families and origins. Supply the optional `report` argument when the caller learned the
ID inside a named family. The adapter then verifies that the document still belongs to
that family before loading it, matching `GET /api/reports/{name}/{id}`. A mismatch is
reported as `NOT_FOUND`.

Configured file-backed IDs use the same catalogue and load path. Their disk-backed state,
reconciliation, and recovery behavior are described in
[Saved reports](SAVED-REPORTS.md#reconciliation). A later data- or execution-dependent
validation failure returns `REPORT_VALIDATION_FAILED` and does not delete the configured
identity.

The remaining arguments replace individual parts of the loaded document before it is
executed. They mutate a detached copy; nothing is written back to the store, so the same
id executes identically for the next caller. Omitting an argument — or passing `null` —
keeps what the document already says, and omitting all of them executes the saved state
exactly as stored.

`page` and `pageSize` replace the saved paging request:

- `page` is 1-based.
- `pageSize` is non-negative.
- `pageSize: 0` uses the engine's unpaged query mode. Reserve it for reports whose
  filtered result is known to be bounded; positive sizes are safer for general use.

`search` replaces the document's toolbar search text, the same case-insensitive
contains match across eligible text columns that the packaged client's search box
performs. An empty string clears a stored search.

`sort` replaces the ordering of the document's active table, the same terminal `sort`
composable the packaged client's sort editor writes. An empty list clears the stored
ordering. Each entry is:

```graphql
input InteractiveReportSortInput {
  col: String!
  dir: InteractiveReportSortDirection  # ASC (default) | DESC
  nulls: InteractiveReportNullPlacement # FIRST | LAST; omitted keeps the dialect default
}
```

`col` is a logical column name resolved against the report's live schema like any other
report-state input. Saved reports degrade rather than fail, so an unknown or unsortable
column is dropped and reported in the result's `ignored` list instead of raising an
error — check `ignored` when an ordering appears not to have been applied. Ordering is a
document declaration, so it needs a document table to live in: every default, configured,
and packaged-client document declares one, but a hand-authored state with no `tables`
returns `BAD_USER_INPUT` rather than being restructured.

Report *features* (`InteractiveReport:Reports:{name}:Features`) are a client UI
whitelist, not a query gate. As with `POST /api/reports/{name}/query`, these arguments
are accepted regardless of which features a report enables for its toolbar.

For example:

```graphql
query ExecuteSavedReport(
  $id: ID!
  $report: String
  $page: Int
  $pageSize: Int
  $search: String
  $sort: [InteractiveReportSortInput!]
) {
  report(id: $id, report: $report, page: $page, pageSize: $pageSize, search: $search, sort: $sort) {
    columns {
      name
      label
      type
      computed
    }
    rows
    page {
      index
      size
    }
    totalRows
    ignored {
      kind
      detail
    }
    elapsedMs
  }
}
```

Variables:

```json
{
  "id": 42,
  "report": "orders",
  "page": 1,
  "pageSize": 100,
  "search": "acme",
  "sort": [{ "col": "ORDER_DATE", "dir": "DESC", "nulls": "LAST" }]
}
```

`rows` is a GraphQL complex scalar containing JSON objects keyed by the names returned
in `columns`. Report projections, computed columns, grouping, charts, and pivots can
change the row shape at runtime, so the adapter does not pretend those rows have one
static GraphQL object type.

Row values follow the same exact-number contract as the HTTP API: 64-bit integers and
decimals are serialized as invariant strings so JavaScript clients never round them
through IEEE-754 doubles, while the corresponding `columns` entry still reports
`type: "number"`. Ordinary 32-bit integers and floating-point values remain JSON
numbers. The typed scalar fields (`totalRows`, `elapsedMs`) are GraphQL `Long` values
and stay numbers.

## Eligible reports and stability

Every saved report in `ISavedReportStore` is eligible:

- private database reports;
- default or global database reports;
- configured file-backed reports synchronized into the store.

The adapter does not read configured files as a separate catalogue. It addresses the
existing database identity directly and performs the same saved-report lookup used for
database-backed rows; ordinary catalogue discovery reconciles new configured identities.

File-backed reports are strongly recommended when another application will rely on a
report as a durable API surface. Their state is read-only through ordinary saved-report
operations and changes only when an administrator deliberately updates the configured
file. Database reports remain useful for experimentation and private integrations, but
their owners can update or delete them at any time.

This is a recommendation rather than a security boundary. Origin never grants access.

## Authorization

GraphQL is an alternate transport over the same resources, not an alternate
authorization model. Every field calls the same transport-neutral server boundary the
REST routes call, so the decisions below are made once and shared.

`reports` requests `ViewReport` for each configured report and omits the ones denied.
`savedReports` requests `ListSavedReports` (or `ListAllSavedReports` for the built-in
saved-reports listing), then re-checks `ListAllSavedReports` to decide whether the caller
sees the complete family or only public and owned documents.

For `report`, the resolver:

1. Gets the saved report from `ISavedReportStore`.
2. Applies normal read visibility: public, owner, or administrator.
3. Resolves and authorizes the underlying report definition.
4. Requests `ReadSavedReport` and `Query` from every configured application authorizer.
5. Resolves trusted context parameters from the current `ClaimsPrincipal`.
6. Executes the stored report state, with the requested overrides applied, through
   `ReportExecutor`.

The application authorization resource contains both `ReportName` and the saved-report
metadata. Callbacks and ASP.NET Core resource handlers can therefore make the same
principal/action/resource decision they make for HTTP API calls. All configured
authorizers must grant both actions.

Private reports denied to another caller use the same non-disclosure rule as the HTTP
API and return the GraphQL error code `NOT_FOUND`. The transport does not reveal
whether the id exists, and such documents never appear in `savedReports`.

## Errors

Executed GraphQL operations follow normal GraphQL response semantics: resolver errors
appear in the `errors` array, normally with HTTP 200. Stable adapter error codes are:

| Code | Meaning |
|---|---|
| `BAD_USER_INPUT` | An argument is outside its allowed range or cannot apply to this document. |
| `NOT_FOUND` | The saved report or underlying definition is absent, or access is hidden. |
| `UNAUTHENTICATED` | The operation requires an authenticated principal. |
| `FORBIDDEN` | The caller is known but the operation is denied without non-disclosure. |
| `REPORT_VALIDATION_FAILED` | The state, with any overrides applied, does not validate against the live schema. |
| `INTERNAL_SERVER_ERROR` | Authorization infrastructure, saved-report storage, or report execution failed. |

Errors raised by the server boundary also carry the REST API's stable `IR-` code in the
`reportErrorCode` extension, and their message is the same English fallback text
`GET /api/reports/...` returns for that code, so a localized client can key on one
vocabulary across both transports.

Validation details contain only report-state paths and validation messages. Unexpected
failures are logged server-side and return a correlation `traceId`; database exception
text and generated SQL are not returned.

Errors that occur before GraphQL execution use the HTTP API's single coded-error object
instead of a GraphQL `errors` array. At present this is
`IR-1500` for methods other than GET or POST and for WebSocket
upgrades. Its `description` is English fallback text that localized clients may replace
using the code.

## Operational boundaries

The adapter deliberately exposes no report-state input beyond paging, search, and
sorting. A caller cannot use GraphQL to add filters, computed columns, projections,
expressions, or arbitrary report state, and the schema declares no mutation type: it
creates, updates, and deletes nothing. To author or change a report, use Interactive
Reports and its saved-report API, then discover and execute the resulting id through
GraphQL.

The underlying report definition still controls ordinary positive page sizes. Unpaged
queries can return the complete filtered result, so hosts should prefer positive sizes
and apply their normal authentication, CORS, request-size, timeout, and ASP.NET Core
rate-limiting policies to the GraphQL endpoint.
