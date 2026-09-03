# Getting started

This guide takes an ASP.NET Core application from an empty integration to a working
report. It keeps the first run small, then points to the guides that own optional
features and operational detail.

## Prerequisites

You need an ASP.NET Core application targeting .NET 8 or later and a database the
application can reach. Interactive Reports includes SQLite support. For SQL Server,
PostgreSQL, or Oracle, the host application must reference the matching ADO.NET driver.

## 1. Install the packages

Install the server and JSON client:

```sh
dotnet add package InteractiveReport.AspNetCore
dotnet add package InteractiveReport.Client.Json
```

Add the file-download package when users should be able to export CSV:

```sh
dotnet add package InteractiveReport.Client.FileDownload
```

## 2. Configure a data source and report

Add a normal connection string and one report definition to `appsettings.json`:

```json
{
  "ConnectionStrings": {
    "MainDb": "Data Source=reports.db"
  },
  "InteractiveReport": {
    "Reports": {
      "orders": {
        "dataSource": "MainDb",
        "sql": "SELECT ORDER_ID, CUSTOMER, AMOUNT, ORDER_DATE FROM ORDERS",
        "authorization": { "allowAnonymous": true }
      }
    }
  }
}
```

The `dataSource` value has two forms:

- A value without `=` names an entry under `ConnectionStrings`.
- A value containing `=` is treated as a literal connection string.

SQLite is inferred from the connection string above. For another database, use the
standard `ConnectionStrings:MainDb_ProviderName` companion setting or set the report's
`provider` to `sqlServer`, `postgres`, or `oracle`. The host must reference
`Microsoft.Data.SqlClient`, `Npgsql`, or `Oracle.ManagedDataAccess.Core`, respectively.

The `sql` value is trusted application configuration. It must be a `SELECT`; it is never
sent to the browser. Reports require an authenticated caller by default. This example
sets `allowAnonymous` only to make the first run independent of the host's authentication
setup. Remove that setting for a real authenticated report.

## 3. Register and map the server

Add the services and endpoints in `Program.cs`:

```csharp
using InteractiveReport.AspNetCore;
using InteractiveReport.Client.Json;

var builder = WebApplication.CreateBuilder(args);
var reports = builder.Services.AddInteractiveReports(builder.Configuration);
builder.Services.AddInteractiveReportJson();

var app = builder.Build();
// Configure the application's usual middleware.

app.MapInteractiveReportJson("/api/reports");
```

If you installed the file-download package, register and map it too:

```csharp
using InteractiveReport.Client.FileDownload;

builder.Services.AddInteractiveReportFileDownload();

// After app has been built:
app.MapInteractiveReportFileDownload("/api/download");
```

The route prefix is application-owned. The examples in this documentation assume
`/api/reports` and `/api/download`.

## 4. Open the report

Run the application and browse to:

```text
/api/reports/orders/view
```

The packaged viewer page is useful for verification and internal tools. To place the
report inside an application page, load the packaged module and add the custom element:

```html
<script type="module" src="/api/reports/ui/ir.js"></script>
<interactive-report report="orders"></interactive-report>
```

The element obtains the report family's default document, loads its schema, and queries
the configured report. See [Embedding the report](EMBEDDING.md) for properties, events,
theming, and host integration.

If the viewer does not start, check these boundaries first:

- The configured connection string points to a reachable database.
- The configured `SELECT` runs for the report connection's database principal.
- A non-SQLite provider package is referenced by the host.
- The request is authenticated unless the report explicitly allows anonymous access.

Definition errors fail during application startup with a message naming the invalid
report. Runtime API failures use the coded error shape described with the
[REST surface](API.md#rest-surface).

## Add saved reports and administration

No persistence is created by the basic integration. To enable saved reports, configured
report documents, and administration, choose a database explicitly:

```json
{
  "InteractiveReport": {
    "Administrators": [ "bootstrap-admin-id" ],
    "SavedReports": {
      "dataSource": "MainDb",
      "tablePrefix": "MYAPP_"
    }
  }
}
```

The storage `dataSource` follows the same rules as a report data source. With the default
auto-create behavior, this example creates `MYAPP_IR_SAVED_REPORTS` and
`MYAPP_IR_REPORT_AUTHORIZATION` when the persistence subsystem is first used. A report-only
installation still creates neither tables nor local files.

The packaged administration page is then available at:

```text
/api/reports/admin
```

The bootstrap administrator value must match the caller's canonical identity. Enable
`InteractiveReport:WhoamiEnabled` temporarily when you need the application to show the
identity it resolved. See [Saved reports](SAVED-REPORTS.md) for persistence, defaults,
configured JSON documents, import/export, and the administration workflow.

## Choose an authorization integration

Interactive Reports always applies its built-in authentication, report-policy, saved-report
visibility, and administrator rules. Applications can add operation-specific rules with
either of these builder methods:

```csharp
reports.UseAuthorization((request, cancellationToken) =>
{
    cancellationToken.ThrowIfCancellationRequested();
    var allowed = request.Action switch
    {
        InteractiveReportAction.Export =>
            request.User.HasClaim("reports", "export"),
        _ => true,
    };

    return ValueTask.FromResult(allowed);
});
```

Or delegate to standard ASP.NET Core resource handlers:

```csharp
reports.UseAspNetCoreAuthorization();
builder.Services.AddScoped<IAuthorizationHandler, InteractiveReportsAuthorizationHandler>();
```

Once application authorization is registered, it participates in every protected action;
it is not limited to exceptional cases. Read [Authorization](AUTHORIZATION.md) before
deploying custom callbacks or handlers. That guide defines every action, candidate
resource, composition rule, and denial response.

## Optional integrations

The remaining guides each own one integration surface:

| Goal | Guide |
|---|---|
| Configure every server option or call the public .NET and REST APIs | [Integration API](API.md) |
| Persist, publish, deploy, import, or administer report documents | [Saved reports](SAVED-REPORTS.md) |
| Embed and customize the browser component | [Embedding the report](EMBEDDING.md) |
| Expose saved-report queries through GraphQL.NET | [GraphQL adapter](GRAPHQL.md) |
| Understand package boundaries and request execution | [Architecture](ARCHITECTURE.md) |
| Learn the report UI and expression language | [User Guide](USER-GUIDE.md) |

### Umbraco 13

An Umbraco site already has its connection string under
`ConnectionStrings:umbracoDbDSN` and a companion provider-name value. A report over the
Umbraco database can therefore use `"dataSource": "umbracoDbDSN"`. Register the services
normally and map the endpoints after `app.UseUmbraco(...)`. Members-only reports can use
a policy registered by the host:

```json
"authorization": { "policy": "MayViewReports" }
```

### Programmatic connections

Use a named connection factory when configuration cannot create the connection, such as
for a profiled wrapper or a tenant-aware factory:

```csharp
reports.AddConnection("MainDb", services => new SqlConnection(...));
```

The report then uses `"connection": "MainDb"` instead of `dataSource`. Supply an explicit
`ReportDialect` when the returned connection type is a wrapper the engine cannot infer.
