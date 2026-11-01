# AurorisNet.InteractiveReports.Core

The database query engine behind Interactive Reports, an Oracle APEX-style
reporting library for .NET. It turns a trusted report definition and a user's
report state into validated, parameterized SQL for SQLite, SQL Server,
PostgreSQL, or Oracle.

This package contains report and state models, the expression language, schema
discovery, validation, query planning and execution, and saved-report storage
contracts and implementations. It targets .NET 8 and has no ASP.NET Core dependency.

## Which package should I install?

**For interactive reports in an ASP.NET Core website, install
[AurorisNet.InteractiveReports](https://www.nuget.org/packages/AurorisNet.InteractiveReports/).**
It includes the browser UI and REST API, and brings in this engine and the
ASP.NET Core server automatically:

```sh
dotnet add package AurorisNet.InteractiveReports
```

Reference Core directly when building a custom host, working with report models,
or calling the engine without the ASP.NET Core integration:

```sh
dotnet add package AurorisNet.InteractiveReports.Core
```

Core provides no browser UI or HTTP endpoints. Direct engine callers supply the
database connections, resolved definitions, trusted context, and authorization
decisions. In an ASP.NET Core request, use the server package's
`IInteractiveReportServer` to retain the shared authorization and saved-report
behavior.

## How the packages fit together

| Package | When to choose it |
|---|---|
| [AurorisNet.InteractiveReports](https://www.nuget.org/packages/AurorisNet.InteractiveReports/) | The usual starting point: browser viewer, administration, and REST API. Includes the server and Core. |
| [AurorisNet.InteractiveReports.AspNetCore](https://www.nuget.org/packages/AurorisNet.InteractiveReports.AspNetCore/) | Server-side .NET integration or custom endpoints with configuration and authorization. Includes Core. |
| [AurorisNet.InteractiveReports.Client.FileDownload](https://www.nuget.org/packages/AurorisNet.InteractiveReports.Client.FileDownload/) | CSV and self-contained HTML downloads. Includes the server and Core. |
| [AurorisNet.InteractiveReports.Client.GraphQL](https://www.nuget.org/packages/AurorisNet.InteractiveReports.Client.GraphQL/) | Query saved reports through GraphQL.NET. Includes the server and Core. |

Every other Interactive Reports package depends on Core, directly or through the
server. Installing Core alone does not install those higher-level packages. The
host application supplies its database's ADO.NET driver.

NuGet IDs use `AurorisNet.InteractiveReports`; C# namespaces remain
`InteractiveReport.*`. All five packages are released together at the same version.

## Documentation

- [Architecture and package boundaries](https://github.com/auroris/Interactive-Reports/blob/main/docs/ARCHITECTURE.md)
- [Integration API, including direct engine calls](https://github.com/auroris/Interactive-Reports/blob/main/docs/API.md)
- [Getting started with browser reports](https://github.com/auroris/Interactive-Reports/blob/main/docs/GETTING-STARTED.md)
- [Interactive demo](https://interactive-report.pages.dev/)
- [Source and issues](https://github.com/auroris/Interactive-Reports)
