# Integration API

This guide is the application integrator's reference for Interactive Reports. It
starts with the ASP.NET Core surface because the server owns report definitions,
authorization, trusted context parameters, database access, and validation. The
browser element owns presentation and user-authored report state.

For report-state semantics, see the [User Guide](USER-GUIDE.md); for persistence and
configured documents, see [Saved reports](SAVED-REPORTS.md). The full trust and
component model is in [Architecture](ARCHITECTURE.md), and the optional saved-report
transport is covered by the [GraphQL adapter](GRAPHQL.md).

## Packages and namespaces

| Package | Primary namespace | Use it for |
|---|---|---|
| `InteractiveReport.AspNetCore` | `InteractiveReport.AspNetCore` | Server registration, transport-neutral request/authorization boundary, configuration, execution, and persistence integration. |
| `InteractiveReport.Client.Json` | `InteractiveReport.Client.Json` | REST, saved-report, administration, viewer, and packaged-browser routes. |
| `InteractiveReport.Client.FileDownload` | `InteractiveReport.Client.FileDownload` | Authorized file-download routes and CSV rendering. |
| `InteractiveReport.Core` | `InteractiveReport.Core.*` | Report definitions and state, execution, definition stores, and persistence contracts. This is referenced transitively by the server package. |
| `InteractiveReport.Client.GraphQL` | `InteractiveReport.Client.GraphQL` | Optional query-only discovery and execution of saved reports through GraphQL.NET. |

## Register and map the server

The smallest application uses configuration-backed definitions:

```csharp
using InteractiveReport.AspNetCore;
using InteractiveReport.Client.Json;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddInteractiveReports(builder.Configuration);
builder.Services.AddInteractiveReportJson();

var app = builder.Build();
app.MapInteractiveReportJson("/api/reports");
app.Run();
```

The default configuration section is `InteractiveReport`. Supply a different
`sectionName` argument when the application needs one:

```csharp
builder.Services.AddInteractiveReports(builder.Configuration, "Reporting");
```

```json
{
  "ConnectionStrings": {
    "MainDb": "Data Source=reports.db"
  },
  "InteractiveReport": {
    "Reports": {
      "orders": {
        "title": "Orders",
        "dataSource": "MainDb",
        "provider": "sqlite",
        "sql": "SELECT ORDER_ID, CUSTOMER, STATUS, AMOUNT FROM ORDERS",
        "authorization": { "allowAnonymous": true }
      }
    }
  }
}
```

`MapInteractiveReportJson` returns a `RouteGroupBuilder`, so normal endpoint conventions
can be applied to the complete surface:

```csharp
app.UseAuthentication();
app.UseAuthorization();

app.MapInteractiveReportJson("/api/reports")
    .RequireAuthorization("ReportingUsers")
    .RequireRateLimiting("reports");
```

The host's route-group authorization is independent of report-definition
authorization. For example, a group-level requirement still applies to a definition
that sets `allowAnonymous`.

### Register a connection factory

Use `AddConnection` when the application creates connections in code. The callback
must create a new, unopened connection for each call. The package detects the SQL
dialect from the concrete connection type.

```csharp
using InteractiveReport.AspNetCore;
using Microsoft.Data.SqlClient;

builder.Services
    .AddInteractiveReports(builder.Configuration)
    .AddConnection("MainDb", sp =>
    {
        var configuration = sp.GetRequiredService<IConfiguration>();
        var connectionString = configuration.GetConnectionString("MainDb")
            ?? throw new InvalidOperationException("MainDb is not configured.");
        return new SqlConnection(connectionString);
    });
```

Wrapper and profiler connection types may not reveal their provider. Declare the
dialect in that case:

```csharp
using InteractiveReport.Core.Model;

reports.AddConnection(
    "ProfiledDb",
    sp => new ProfiledDbConnection(CreateInnerConnection(sp)),
    ReportDialect.SqlServer);
```

The corresponding report definition uses `"connection": "MainDb"`. A definition
sets either `connection` or `dataSource`, never both.

## Report-definition configuration

Definitions are keyed by an internal family name under `InteractiveReport:Reports`.
Processing, family discovery, document loading, and viewer endpoints use that key.
Database-generated numeric ids identify individual documents within or after that family
context; mutation routes use the id directly.

| Property | Purpose |
|---|---|
| `title` | Optional display title. |
| `dataSource`, `provider` | A `ConnectionStrings` name or literal connection string and its provider token. |
| `connection` | A name registered through `AddConnection`; an alternative to `dataSource`. |
| `sql` | The developer-owned base `SELECT`. It never crosses the client boundary. |
| `contextParams` | Trusted server-resolved values used by the base SQL. |
| `authorization` | Authentication, policy, administrator-only, restriction, and configured-user rules. |
| `features` | Initial client-control suggestions. `download` and saved-report creation also have server checks. |
| `defaultState` | The developer-owned initial `ReportState`. |
| `documentFiles` | Source-controlled saved-report envelopes, relative to the content root unless absolute. |
| `maxRows`, `defaultPageSize`, `maxPageSize`, `maxPivotColumns`, `maxChartPoints` | Execution limits. |
| `commandTimeoutSeconds`, `consistency`, `timeZone` | Database execution policy. |
| `columnLabels`, `columns`, `editLink`, `createLink` | Definition-owned presentation and behavior hints; the two links carry a `mode` of `navigate` or `event`. |

Configuration is validated at startup. Reports require authentication unless their
authorization block explicitly sets `allowAnonymous`. `features` is a client hint,
not an authorization boundary: embedding JavaScript may override client controls.
The server still independently enforces authorization, report-state validation,
trusted context, and the `download` and saved-report-creation feature checks.

Each `documentFiles` entry is a `{ "title", "default", "state" }` JSON envelope;
`title` is the file-backed report's required configured name. The state remains on disk;
the saved-report store supplies its runtime identity and catalogue metadata. At most one
file per family may set `default: true`. Reconciliation, failure handling, deployment,
and import/export are described in [Saved reports](SAVED-REPORTS.md#source-controlled-documents).

## Server API index

| API | Purpose |
|---|---|
| `IServiceCollection.AddInteractiveReports(...)` | Registers definitions, execution, persistence, and the transport-neutral server/authorization boundary. Returns `InteractiveReportBuilder`. |
| `IServiceCollection.AddInteractiveReportJson()` | Registers the JSON/browser client adapter. |
| `IServiceCollection.AddInteractiveReportFileDownload()` | Registers file writers; currently CSV. |
| `InteractiveReportBuilder.AddConnection(...)` | Registers an unopened ADO.NET connection factory, with inferred or explicit dialect. |
| `InteractiveReportBuilder.UseLogger(...)` | Sends package diagnostics to a host-owned `ILogger`. The package is silent when no logger is supplied. |
| `InteractiveReportBuilder.UseContextParameterResolver<T>()` | Replaces claim-based trusted-context resolution with a singleton application resolver. |
| `InteractiveReportBuilder.UseUserProvider<T>()` | Adds a scoped application user directory for administration choices. |
| `InteractiveReportBuilder.UseAuthorization(...)` | Adds a direct application authorization callback. |
| `InteractiveReportBuilder.UseAspNetCoreAuthorization()` | Adds an adapter to ASP.NET Core resource-based authorization. |
| `IEndpointRouteBuilder.MapInteractiveReportJson(...)` | Maps REST, saved-report, administration, packaged asset, and optional viewer routes. |
| `IEndpointRouteBuilder.MapInteractiveReportFileDownload(...)` | Maps `POST {prefix}/{name}/{format}` file requests. |
| `IReportAuthorizationService` | Central transport-neutral definition, resource, administrator, feature, and application-authorization boundary. |
| `IInteractiveReportServer` | Loads saved documents and executes authorized client-submitted report documents without HTTP response types. |
| `IReportDefinitionStore` | Resolves executable report definitions. `IReportDefinitionAuthorizationStore` is its optional lightweight authorization companion. |
| `ReportExecutor` | Validates and executes a resolved definition and `ReportState`; engine calls perform no transport authorization themselves. |
| `ISavedReportStore` | Replaceable persistence contract. It stores data but does not make authorization decisions. |
| `IrJson.Options` | Shared JSON protocol options for host-owned endpoints. |
| `ReportFeatures` | Canonical client-control feature names and server feature checks. |

## Trusted context parameters

The default resolver reads configured claims from the current principal. These
values are never accepted from report-state JSON.

```json
{
  "sql": "SELECT * FROM ORDERS WHERE TENANT_ID = @tenantId",
  "contextParams": {
    "tenantId": { "claim": "tenant_id" }
  }
}
```

Register an `IContextParameterResolver` when context comes from another trusted
source, such as a tenant service:

```csharp
using System.Security.Claims;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;

public sealed class TenantContextResolver(ITenantAccessor tenants)
    : IContextParameterResolver
{
    public ValueTask<object?> Resolve(
        string name,
        ContextParamSpec spec,
        ClaimsPrincipal? user,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return name.Equals("tenantId", StringComparison.OrdinalIgnoreCase)
            ? ValueTask.FromResult<object?>(tenants.RequiredTenantId)
            : throw new InvalidOperationException($"Unknown context parameter '{name}'.");
    }
}

builder.Services
    .AddInteractiveReports(builder.Configuration)
    .UseContextParameterResolver<TenantContextResolver>();
```

The resolver is registered as a singleton. Its dependencies must therefore be safe
to consume from a singleton, or it must use a scope-safe accessor.

## Application authorization

Report-definition authorization remains active for every integration. Application
authorization can add restrictions; it cannot bypass authentication, configured
users, administrator rules, or database grants.

### Direct callback

`UseAuthorization` receives the caller, attempted action, report resource, and the
current request service provider. Return `false` for an expected denial.

```csharp
var reports = builder.Services.AddInteractiveReports(builder.Configuration);

reports.UseAuthorization(static (request, ct) =>
{
    ct.ThrowIfCancellationRequested();

    var allowed = request.Action switch
    {
        InteractiveReportAction.ViewReport or
        InteractiveReportAction.Query or
        InteractiveReportAction.Export
            => request.User.IsInRole("ReportingUsers"),

        _ => request.User.IsInRole("ReportAdministrators")
    };

    return ValueTask.FromResult(allowed);
});
```

Use `request.Resource.ReportName` and `request.Resource.SavedReport` for
resource-specific decisions. Multiple callbacks, plus the ASP.NET Core adapter when
enabled, compose with AND semantics.

### ASP.NET Core resource authorization

`UseAspNetCoreAuthorization` emits an
`InteractiveReportAuthorizationRequirement` and an
`InteractiveReportAuthorizationResource` through `IAuthorizationService`:

```csharp
using InteractiveReport.AspNetCore;
using Microsoft.AspNetCore.Authorization;

builder.Services.AddSingleton<IAuthorizationHandler, ReportAuthorizationHandler>();

builder.Services
    .AddInteractiveReports(builder.Configuration)
    .UseAspNetCoreAuthorization();

public sealed class ReportAuthorizationHandler
    : AuthorizationHandler<
        InteractiveReportAuthorizationRequirement,
        InteractiveReportAuthorizationResource>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        InteractiveReportAuthorizationRequirement requirement,
        InteractiveReportAuthorizationResource resource)
    {
        if (context.User.IsInRole("ReportingUsers") &&
            resource.ReportName.Equals("orders", StringComparison.OrdinalIgnoreCase))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
```

The requirement's `Action` distinguishes query, export, saved-report, publication,
and administration operations. A handler should succeed only the actions it intends
to grant.

## Build a host-owned HTTP endpoint

Use `IInteractiveReportServer` when a custom endpoint should preserve the same definition,
authorization, saved-document, and trusted-context boundary as the packaged endpoints:

```csharp
using InteractiveReport.AspNetCore;
using InteractiveReport.Client.Json;
using InteractiveReport.Core.Model;

app.MapPost("/internal/orders/query", async (
    ReportState state,
    HttpContext http,
    IInteractiveReportServer reports,
    CancellationToken ct) =>
{
    var result = await reports.Query(
        "orders",
        state,
        InteractiveReportHttpRequest.Context(http),
        ct);

    return result.Failure is null
        ? Results.Json(result.Value!, IrJson.Options)
        : InteractiveReportHttpResult.Failure(result.Failure, http);
});
```

Return `InteractiveReportHttpResult.Failure` unchanged for a failed server result. This
preserves the package's status mapping, non-disclosure behavior, and coded error shape.
Add an ASP.NET Core endpoint policy separately when the host needs one in addition to the
report's own authentication and authorization rules.

## Execute and export in process

`ReportExecutor` is the transport-neutral query engine. Direct callers must resolve
an executable definition and supply trusted context parameters themselves:

```csharp
using InteractiveReport.Core.Definitions;
using InteractiveReport.Core.Execution;

var definition = await definitions.Find("orders", ct)
    ?? throw new KeyNotFoundException("Report 'orders' was not found.");

var contextParams = new Dictionary<string, object?>
{
    ["tenantId"] = tenant.Id
};

var result = await executor.Query(definition, state, contextParams, ct);
```

For an in-process list of values, pass the complete current document explicitly:

```csharp
var values = await executor.Lov(definition, new ReportLovRequest
{
    Document = state,
    Table = state.ActiveTable ?? "definition",
    Column = "STATUS",
    Search = "pen"
}, contextParams, ct);
```

`values.Items` contains no more than `ReportExecutor.MaxLovItems` (50) entries.

This lower-level path does not perform application authorization or infer context from a
principal. Prefer `IInteractiveReportServer` in an HTTP request. A background process that
uses `ReportExecutor` directly becomes the trust and authorization boundary itself.

For browser-facing files, register and map the file client:

```csharp
using InteractiveReport.Client.FileDownload;

builder.Services.AddInteractiveReportFileDownload();
// ...
app.MapInteractiveReportFileDownload("/api/download");
```

The file client accepts the JavaScript client's current document, turns paging off in a
detached copy, runs central `Export` authorization and the `download` feature check, then
submits the document as an ordinary query and renders the returned rows. CSV rendering
applies effective labels and masks, emits link text and image URLs rather than browser
HTML, retains action labels, appends requested pivot totals, and omits hidden renderer
inputs from the file.

## Replace a definition or saved-report store

The built-in `IReportDefinitionStore` reads configuration. An application can replace
it after calling `AddInteractiveReports`:

```csharp
using InteractiveReport.Core.Definitions;
using Microsoft.Extensions.DependencyInjection.Extensions;

builder.Services.Replace(
    ServiceDescriptor.Singleton<IReportDefinitionStore, DatabaseReportDefinitionStore>());
```

A custom definition store returns detached definitions, assigns the canonical `Name`,
and resolves `Connection` and `Dialect` before returning. It is also responsible for
validating its definition-level settings. Implement
`IReportDefinitionAuthorizationStore` as well when the store can return a lightweight
name and authorization envelope before loading the executable SQL definition.

Replacing the store also replaces configuration-backed report lookup, configured
document synchronization through that lookup, and its built-in definition behavior.
Treat this as an advanced application boundary, not as a way to append one report.

`ISavedReportStore` is separately replaceable for custom persistence. Its methods are
storage-only; ownership and authorization policy remain in
`IReportAuthorizationService`. Implementations must honor the documented detached-snapshot
and compare-and-swap contracts.

## Supply administration user choices

`IInteractiveReportUserProvider` supplies choices to the administration UI. It does
not authorize those identities.

```csharp
using System.Security.Claims;
using InteractiveReport.AspNetCore;

public sealed class ReportUserProvider(IApplicationUsers users)
    : IInteractiveReportUserProvider
{
    public async ValueTask<IReadOnlyCollection<InteractiveReportUser>?> GetUsers(
        ClaimsPrincipal administrator,
        CancellationToken ct = default)
    {
        return (await users.List(ct))
            .Select(user => new InteractiveReportUser(user.DisplayName, user.SubjectId))
            .ToArray();
    }
}

builder.Services
    .AddInteractiveReports(builder.Configuration)
    .UseUserProvider<ReportUserProvider>();
```

Returning `null` or an empty collection keeps free-form identity entry available.
The provider is scoped.

## REST surface

With the default prefix, the principal routes are:

| Method and route | Contract |
|---|---|
| `GET /api/reports` | Lists appsettings report configurations the caller may view as `{ name, title }`. It does not list or reconcile report documents. |
| `GET /api/reports/{name}` | Reconciles one configured family and lists its visible report documents. Administrators see the complete family; other callers see public and exactly owned documents. IDs are JSON numbers. |
| `GET /api/reports/{name}/schema` | Definition schema, presentation hints, limits, features, and client capabilities for a configured definition key. |
| `POST /api/reports/{name}/query` | Accepts the client's `ReportState`; returns `ReportResult` with rows and the accepted server-enriched `document`. |
| `POST /api/reports/{name}/lov` | Accepts a required current `document`, its active `table`, one `column`, and optional `search`; returns at most 50 distinct values. |
| `POST /api/download/{name}/{format}` | File-client endpoint. Accepts the current `ReportState`; CSV is currently supported. |
| `POST /api/reports/{id}/saved` | Creates a private or global saved report from `SaveReportRequest` in that family. |
| `GET /api/reports/{name}/{id}` | Returns `SavedReportDocument` after authorizing the named configuration and confirming that the numeric id belongs to it. |
| `PUT /api/reports/{id}` | Applies `UpdateSavedReportRequest`; `isDefault: true` atomically selects a new default. |
| `DELETE /api/reports/{id}` | Deletes an editable saved report. |
| `GET /api/reports/whoami` | Optional identity diagnostic; disabled unless `WhoamiEnabled` is true. |
| `GET /api/reports/admin/users` | Lists application-supplied identity choices after the administration gate. |
| `GET /api/reports/admin/authorization` | Returns configured and database-backed administrator, restriction, and user grants. |
| `POST /api/reports/admin/authorization/administrators` | Adds a database-backed administrator grant. |
| `DELETE /api/reports/admin/authorization/administrators` | Removes a database-backed administrator grant. |
| `PUT /api/reports/admin/authorization/reports/{name}` | Sets the database-backed restriction marker for a report. |
| `POST /api/reports/admin/authorization/reports/{name}/users` | Adds a database-backed user grant for a report. |
| `DELETE /api/reports/admin/authorization/reports/{name}/users` | Removes a database-backed user grant for a report. |
| `GET /api/reports/admin/saved/{id}/document` | Downloads a saved report as a configured-document envelope. |
| `POST /api/reports/admin/{id}/documents` | Validates and imports an envelope as a private document in the selected family. |
| `GET /api/reports/ui/{file}` | Packaged browser assets. |
| `GET /api/reports/{name}/view` | Optional packaged viewer page. |
| `GET /api/reports/admin` | Optional packaged administration page. |

`ReportResult.configuredLabels` carries the definition-owned base label map separately
from structural column metadata. Clients layer the returned document's label composables
over that map when rendering their own representation.

All persisted document IDs are database-generated integers. A missing document, a document
addressed through the wrong configured family, and a document the caller may not read all
return 404. The ordinary component starts with the appsettings name: it lists that family,
selects `isDefault`, then loads `/api/reports/{name}/{id}`. It never calls the root catalogue.
The administration component uses the root catalogue to enumerate appsettings families,
then loops over the same family-list route. Schema, query, LOV, and download also use the
configured definition key. These processing endpoints authorize that definition and do
not read the saved-report store. A submitted `ReportState` has no required ID or stored
provenance; it may be a mutated default, another retrieved document, or a document made
entirely by the client.

Each report family has exactly one public default. Selecting an ordinary database report
as default also publishes it globally and retains the previous default as a global report.
The default cannot be unset directly. When a configured file declares `default: true`,
configuration owns the selection and API attempts to replace it return 409.

All data and management routes enter the central boundary through
`IInteractiveReportServer`. Packaged assets and page
shells are intentionally anonymous; the page's API calls are still authorized.
Every request body must be declared as `Content-Type: application/json`; any other
declared type is refused by the framework with an empty 415 before a handler runs, on
the JSON and file-download routes alike.
`MapInteractiveReportJson` supplies standard endpoint summaries, tags, request and
response types, and error metadata for the host's OpenAPI generator.

A minimal query document looks like this:

```http
POST /api/reports/orders/query HTTP/1.1
Content-Type: application/json

{
  "page": { "index": 1, "size": 50 },
  "activeTable": "base",
  "tables": {
    "base": {
      "from": "definition",
      "schema": null,
      "composables": [
        { "kind": "filter", "filters": [ { "expr": "STATUS = 'Open'" } ] },
        { "kind": "sort", "sorts": [ { "col": "AMOUNT", "dir": "desc" } ] },
        { "kind": "select", "columns": [ "ORDER_ID", "CUSTOMER", "AMOUNT" ] }
      ]
    }
  }
}
```

Use the `document` in the successful response as the accepted state. The server may
populate advisory schema caches or remove stale state into `ignored`.

Query JSON writes CLR `Int64`, `UInt64`, and `Decimal` values as invariant strings so a
JavaScript client cannot silently round them through IEEE-754. Their column metadata still
reports `type: "number"`. Ordinary 32-bit integers and floating-point values remain JSON
numbers.

The LOV endpoint executes against the complete document currently held by the client,
including unsaved changes. `table` must identify that document's active table, and only
the named column is read. The optional case-insensitive search is applied before the hard
50-item limit. The endpoint uses the same `InteractiveReportAction.Query` authorization
as the report table; there is no second column authorization check.

The packaged LOV is an editable combobox shared by filter and highlight authoring.
Its search is a case-insensitive partial match by default: `ac` finds values containing
`ac`, without requiring wildcard syntax. Selecting a returned text item creates a
case-insensitive exact condition. Pressing Enter or **Use Typed Value** without selecting
an item accepts the search text as the condition value and follows the same exact rule.
An unescaped `*` in typed text changes that condition to a case-insensitive partial
match; `Ac*Corp` matches text beginning with `Ac` and ending with `Corp`, while `Ac\*Corp`
matches the literal text `Ac*Corp`. SQL wildcard characters `%` and `_` are ordinary
literal characters in this syntax. Numeric, boolean, date, and null LOV values retain
their typed exact-match behavior.

```http
POST /api/reports/orders/lov HTTP/1.1
Content-Type: application/json

{
  "document": {
    "activeTable": "base",
    "tables": {
      "base": {
        "from": "definition",
        "composables": [
          { "kind": "filter", "filters": [ { "expr": "STATUS <> 'CANCELLED'" } ] }
        ]
      }
    }
  },
  "table": "base",
  "column": "CUSTOMER",
  "search": "ac"
}
```

```json
{
  "table": "base",
  "column": "CUSTOMER",
  "type": "text",
  "items": ["Acme Corp", "Acme Services"],
  "truncated": false
}
```

## Diagnostics and error responses

Interactive Reports uses one optional host-owned logger. Supply it during registration
with `UseLogger`, or when mapping the JSON endpoints:

```csharp
var reportLogger = app.Services.GetRequiredService<ILoggerFactory>()
    .CreateLogger("InteractiveReport");
app.MapInteractiveReportJson("/api/reports", reportLogger);
```

Without a supplied logger, the package creates no provider or output. `Information`
records startup validation, mapped requests, report queries and exports, and configured
document synchronization. `Debug` adds authorization decisions, schema-cache activity,
and submitted SQL. Request bodies and SQL parameter values are not logged. The host owns
filtering and destinations.

Every JSON failure uses `InteractiveReportError`. `code` and `description` are required;
`title`, `details`, and `traceId` appear when applicable:

```json
{
  "code": "IR-1201",
  "description": "One or more report settings are invalid.",
  "title": "Report state failed validation",
  "details": "tables.orders.composables[0].filters[0].expr: unknown column 'OLD_NAME'"
}
```

The `IR-nnnn` code is the stable, language-independent identity. Request-specific paths
and rejected values belong in `details`. Unexpected failures remain sanitized and add a
`traceId` that correlates with the server log. A client with a matching localized catalog
may replace a known code's title and description; it should retain the server fallback for
unknown codes.

## GraphQL transport

The optional adapter executes saved reports by id. It does not accept arbitrary
report-state input.

```csharp
using InteractiveReport.Client.GraphQL;

builder.Services.AddInteractiveReports(builder.Configuration);
builder.Services.AddInteractiveReportGraphQL();

app.MapInteractiveReportGraphQL("/graphql")
    .RequireRateLimiting("reports");
```

See [GraphQL adapter](GRAPHQL.md) for the query shape, authorization sequence, limits,
and errors.

## Browser custom element

The packaged module defines `<interactive-report>`. It uses a shadow root and exposes
only the supported element interface; mutable controller state remains private.

```html
<script type="module" src="/api/reports/ui/ir.js"></script>

<interactive-report
  id="orders-report"
  report="orders"
  saved-report="87"
  api-base="/api/reports"
  stylesheet="/css/orders-report.css">
</interactive-report>
```

### Attributes and properties

| Attribute | Property | Meaning |
|---|---|---|
| `report` | none | Required appsettings report configuration name. Changing the attribute lists and activates that family. |
| none | `reportId` (read-only) | Numeric id of the active report document; available after activation. |
| none | `definitionName` (read-only) | Canonical configured definition key learned during activation. |
| `saved-report` | none | Optional numeric document id to load on activation. |
| `api-base` | `apiBase` | API prefix. It is inferred from the module URL when omitted. |
| `base` | `apiBase` | Older alias for `api-base`. |
| `download-base` | `downloadBase` | File-download prefix. It is inferred from the API prefix when omitted. |
| `lang` | none | Client locale. |
| `theme` | `theme` | `light`, `dark`, or empty to follow the surrounding page and system preference. |
| `disabled` | `disabled` | Makes all package-owned controls inert without clearing control overrides. |
| `stylesheet` | `styleSheet` | Application-owned stylesheet URL inserted into this element's shadow root. Set the property to `null` to remove it. |

### Methods

| Method | Result |
|---|---|
| `getReportDocument()` | Detached, JSON-compatible accepted report document. Requires a completed initial query. |
| `submitReportDocument(document)` | Replaces, queries, adopts, and renders a document. Resolves to a detached result, or `undefined` when canceled or superseded. |
| `getListOfValues({ document, table, column, search, signal })` | Posts a complete current document and returns `{ table, column, type, items, truncated }`; `document` and `table` default to the element's current values. |
| `getExport(format = "csv", { signal } = {})` | Resolves to `{ blob, filename, contentType, truncated }` without starting a browser download. |
| `setControlEnabled(name, enabled)` | Sets one client override. Use `null` to resume the server suggestion. Returns the effective state. |
| `setControlOverrides(overrides)` | Atomically applies several overrides and returns the detached override map. |
| `clearControlOverrides()` | Removes every client override. |
| `isControlEnabled(name)` | Returns one effective control state. |
| `getControlOverrides()` | Returns a detached object containing explicit client overrides. |

Inputs and outputs are detached. Mutating a value returned by the element cannot reach
its working state.

```js
const report = document.querySelector("#orders-report");

const document = report.getReportDocument();
document.search = "urgent";
document.page.index = 1;

const result = await report.submitReportDocument(document);
console.log(result?.rows);

const lov = await report.getListOfValues({
  document,
  table: document.activeTable,
  column: "CUSTOMER",
  search: "ac"
});
console.log(lov.items); // never more than 50
```

The `search` argument above is the LOV lookup text and is partial-match by default.
When the packaged filter or highlight picker accepts a text value, it authors
`LOWER(column) = LOWER(value)` for an exact case-insensitive match, or
`WILDCARD_MATCH(column, pattern)` when typed text contains an unescaped `*`.

### Query lifecycle events

| Event | Timing and detail |
|---|---|
| `ir-before-query` | Cancelable, bubbling, composed. `detail` is `{ document, source, requestId, signal }`. Synchronously mutate the detached document before transport, or call `preventDefault()` to cancel. |
| `ir-query-complete` | Bubbling and composed after the current response is adopted and rendered. `detail` is detached `{ document, result, submitted, source, requestId }`. It is observational. |
| `ir-action` | Bubbling and composed when an action-format cell is invoked. `detail` is `{ command, row, column }`. |

`source` is `initial`, `user`, `saved-report`, `host`, or `refresh`.

```js
report.addEventListener("ir-before-query", event => {
  event.detail.document.search = event.detail.document.search?.trim();
});

report.addEventListener("ir-query-complete", event => {
  auditReportQuery(event.detail.source, event.detail.document);
});
```

Ordinary package-control edits are single-flight. An edit on an idle widget queries
immediately. Edits made while a query is in flight accumulate in the working document;
the in-flight result is rendered when it lands, without replacing the working document,
and one follow-up query carries the final state. Initial loads, saved-report loads,
explicit `submitReportDocument` calls, exports, and administration refreshes abort an
in-flight query instead.

### Client controls

The server's `features` list supplies initial suggestions. The client may override
them without changing the server's authorization or endpoint policies.

```js
report.setControlEnabled("filter", true);
report.setControlEnabled("download", false);
report.setControlEnabled("filter", null); // inherit the server suggestion

report.setControlOverrides({ search: true, sort: false, savedReports: true });
console.log(report.isControlEnabled("search"));
console.log(report.getControlOverrides());
report.clearControlOverrides();
```

Supported names are `search`, `columns`, `rename`, `columnSettings`, `filter`, `sort`,
`pagination`, `controlBreak`, `highlight`, `aggregate`, `compute`, `groupBy`, `pivot`,
`chart`, `savedReports`, and `download`. Names are matched case-insensitively.

## Ownership boundaries at a glance

| Concern | Owner |
|---|---|
| Base SQL, connection, limits, authorization, trusted context | Server definition and host application |
| Report-state validation and SQL generation | Server engine |
| Initial control availability | Server hint, interpreted by the client |
| Effective packaged controls | Client, after host overrides |
| Host stylesheet URL and CSS | Embedding application |
| Search, filters, sorting, layouts, and other report state | User or host JavaScript, validated by the server |
