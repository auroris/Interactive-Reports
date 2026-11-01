# AurorisNet.InteractiveReports.AspNetCore

The shared ASP.NET Core server integration for Interactive Reports. It connects
the report engine to dependency injection, application configuration, database
connections, authentication and authorization hooks, trusted request context,
and saved-report persistence.

Its `IInteractiveReportServer` API provides the common execution boundary used by
the browser/REST, file-download, and GraphQL adapters. This package targets
.NET 8 and includes `AurorisNet.InteractiveReports.Core` as a dependency.

## Which package should I install?

**For the report viewer, administration pages, and REST API, install
[AurorisNet.InteractiveReports](https://www.nuget.org/packages/AurorisNet.InteractiveReports/).**
It brings in this server package and Core automatically:

```sh
dotnet add package AurorisNet.InteractiveReports
```

Install this package directly when your application calls `IInteractiveReportServer`
from .NET or supplies its own endpoints:

```sh
dotnet add package AurorisNet.InteractiveReports.AspNetCore
```

This package registers server services; it does not include the browser UI or map
the standard HTTP routes. Install and map the adapter for the interface you need.

## How the packages fit together

| Package | Relationship to this server |
|---|---|
| [AurorisNet.InteractiveReports.Core](https://www.nuget.org/packages/AurorisNet.InteractiveReports.Core/) | Included dependency: report models, validation, planning, and query execution. |
| [AurorisNet.InteractiveReports](https://www.nuget.org/packages/AurorisNet.InteractiveReports/) | Browser and REST adapter; installs this server automatically. |
| [AurorisNet.InteractiveReports.Client.FileDownload](https://www.nuget.org/packages/AurorisNet.InteractiveReports.Client.FileDownload/) | CSV and self-contained HTML download adapter; installs this server automatically. |
| [AurorisNet.InteractiveReports.Client.GraphQL](https://www.nuget.org/packages/AurorisNet.InteractiveReports.Client.GraphQL/) | Query-only GraphQL.NET adapter; installs this server automatically. |

The three adapters can be installed separately or combined. They share the same
report definitions, saved reports, and authorization decisions.

## Register the server

In an ASP.NET Core host, after configuring the connection and report definitions:

```csharp
using InteractiveReport.AspNetCore;

builder.Services.AddInteractiveReports(builder.Configuration);
```

Inject `IInteractiveReportServer` into your own services or endpoints. The
[Integration API](https://github.com/auroris/Interactive-Reports/blob/main/docs/API.md)
shows how to supply the caller's request context and execute reports. The host
supplies its ADO.NET driver and authentication setup. Saved-report persistence
requires an explicit storage configuration.

NuGet IDs use `AurorisNet.InteractiveReports`; C# namespaces remain
`InteractiveReport.*`. Use matching versions when combining packages.

## Documentation

- [Getting started and data sources](https://github.com/auroris/Interactive-Reports/blob/main/docs/GETTING-STARTED.md)
- [Authorization and host integration](https://github.com/auroris/Interactive-Reports/blob/main/docs/AUTHORIZATION.md)
- [Saved reports and persistence](https://github.com/auroris/Interactive-Reports/blob/main/docs/SAVED-REPORTS.md)
- [Architecture](https://github.com/auroris/Interactive-Reports/blob/main/docs/ARCHITECTURE.md)
- [Source and issues](https://github.com/auroris/Interactive-Reports)
