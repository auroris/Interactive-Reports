# GraphQL adapter

`InteractiveReport.GraphQL` is an optional, query-only GraphQL.NET transport for
executing saved Interactive Reports. It does not replace the HTTP API, report engine,
saved-report store, or authorization system.

## Installation and mapping

Reference the `InteractiveReport.GraphQL` package in addition to
`InteractiveReport.AspNetCore`, then register and map its schema:

```csharp
using InteractiveReport.AspNetCore;
using InteractiveReport.GraphQL;

var reports = builder.Services.AddInteractiveReports(builder.Configuration);
builder.Services.AddInteractiveReportGraphQL();

var app = builder.Build();
app.MapInteractiveReports("/api/reports");
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
The Workbench additionally resolves its checked-in `orders / Default` report at
startup and preloads a ready-to-run report query with paging variables.

`MapInteractiveReportGraphQL` returns an `IEndpointConventionBuilder`, so standard
ASP.NET Core conventions remain available:

```csharp
app.MapInteractiveReportGraphQL("/graphql")
    .RequireRateLimiting("reports");
```

Authentication middleware must run before the mapped endpoint when the host uses it.
Interactive Reports does not select an authentication scheme.

## Query contract

The schema has one root field:

```graphql
type Query {
  report(id: ID!, page: Int, pageSize: Int): InteractiveReportResult
}
```

`id` is the saved-report id returned by the existing saved-report listing and creation
APIs. It is unique across report definitions and origins. Configured file-backed ids
remain stable while their configured report name and resolved file path remain stable.

`page` and `pageSize` optionally replace only the saved state's paging request:

- `page` is 1-based.
- `pageSize` is non-negative.
- `pageSize: 0` uses the engine's unpaged query mode. Reserve it for reports whose
  filtered result is known to be bounded; positive sizes are safer for general use.
- Omitting both executes the saved state exactly as stored.

For example:

```graphql
query ExecuteSavedReport($id: ID!, $page: Int, $pageSize: Int) {
  report(id: $id, page: $page, pageSize: $pageSize) {
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
    elapsedMs
  }
}
```

Variables:

```json
{
  "id": "cfg_0123456789abcdef",
  "page": 1,
  "pageSize": 100
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
- global or primary database reports;
- configured file-backed reports synchronized into the store.

The adapter does not read configured files as a separate catalog. It synchronizes them
through the existing document synchronizer and then performs the same saved-report
lookup used for database rows.

File-backed reports are strongly recommended when another application will rely on a
report as a durable API surface. Their state is read-only through ordinary saved-report
operations and changes only when an administrator deliberately updates the configured
file. Database reports remain useful for experimentation and private integrations, but
their owners can update or delete them at any time.

This is a recommendation rather than a security boundary. Origin never grants access.

## Authorization

GraphQL execution is an alternate transport over the same resources, not an alternate
authorization model. For every request the resolver:

1. Gets the saved report from `ISavedReportStore`.
2. Applies normal read visibility: public, owner, or administrator.
3. Resolves and authorizes the underlying report definition.
4. Requests `ReadSavedReport` and `Query` from every configured application authorizer.
5. Resolves trusted context parameters from the current `ClaimsPrincipal`.
6. Executes the stored report state through `ReportExecutor`.

The application authorization resource contains both `ReportName` and the saved-report
metadata. Callbacks and ASP.NET Core resource handlers can therefore make the same
principal/action/resource decision they make for HTTP API calls. All configured
authorizers must grant both actions.

Private reports denied to another caller use the same non-disclosure rule as the HTTP
API and return the GraphQL error code `NOT_FOUND`. The transport does not reveal
whether the id exists.

## Errors

Executed GraphQL operations follow normal GraphQL response semantics: resolver errors
appear in the `errors` array, normally with HTTP 200. Stable adapter error codes are:

| Code | Meaning |
|---|---|
| `BAD_USER_INPUT` | A paging override is outside its allowed range. |
| `NOT_FOUND` | The saved report or underlying definition is absent, or access is hidden. |
| `UNAUTHENTICATED` | The operation requires an authenticated principal. |
| `FORBIDDEN` | The caller is known but the operation is denied without non-disclosure. |
| `REPORT_VALIDATION_FAILED` | The stored state no longer validates against the live schema. |
| `INTERNAL_SERVER_ERROR` | Authorization infrastructure or report execution failed. |

Validation details contain only report-state paths and validation messages. Unexpected
failures are logged server-side and return a correlation `traceId`; database exception
text and generated SQL are not returned.

Errors that occur before GraphQL execution use the HTTP API's single coded-error object
instead of a GraphQL `errors` array. At present this is
`IR-1500` for methods other than GET or POST and for WebSocket
upgrades. Its `description` is English fallback text that localized clients may replace
using the code.

## Operational boundaries

The adapter deliberately exposes no report-state input beyond paging. A caller cannot
use GraphQL to add filters, projections, expressions, or arbitrary report state. To
create or change a report, use Interactive Reports and its saved-report API, then
execute the resulting id through GraphQL.

The underlying report definition still controls ordinary positive page sizes. Unpaged
queries can return the complete filtered result, so hosts should prefer positive sizes
and apply their normal authentication, CORS, request-size, timeout, and ASP.NET Core
rate-limiting policies to the GraphQL endpoint.
