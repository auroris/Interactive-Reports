# AurorisNet.InteractiveReports.Client.FileDownload

CSV and self-contained HTML downloads for Interactive Reports. This optional
ASP.NET Core adapter executes a report document through the shared server and
returns the current view as a file, applying the same report authorization,
filters, sorting, and configured export limits.

## Which package should I install?

**For browser reports with downloads, install both
[AurorisNet.InteractiveReports](https://www.nuget.org/packages/AurorisNet.InteractiveReports/)
and this package:**

```sh
dotnet add package AurorisNet.InteractiveReports
dotnet add package AurorisNet.InteractiveReports.Client.FileDownload
```

If the browser package is already installed, add only the file-download package.
For a service that only exposes file downloads, this package can be used on its
own; the browser/REST adapter is not required.

## How the packages fit together

| Package | Relationship to this adapter |
|---|---|
| [AurorisNet.InteractiveReports.AspNetCore](https://www.nuget.org/packages/AurorisNet.InteractiveReports.AspNetCore/) | Included dependency: report configuration, authorization, and server execution. |
| [AurorisNet.InteractiveReports.Core](https://www.nuget.org/packages/AurorisNet.InteractiveReports.Core/) | Included through the server: report models, validation, and query engine. |
| [AurorisNet.InteractiveReports](https://www.nuget.org/packages/AurorisNet.InteractiveReports/) | Optional companion: browser UI and REST API. Its download controls use this adapter. |
| [AurorisNet.InteractiveReports.Client.GraphQL](https://www.nuget.org/packages/AurorisNet.InteractiveReports.Client.GraphQL/) | Separate optional adapter for querying saved reports through GraphQL.NET. |

The file-download adapter calls the server directly. It includes neither the
browser UI nor GraphQL, and neither of those adapters installs it automatically.
It targets .NET 8; the host supplies its database's ADO.NET driver.

## Register and map downloads

In an ASP.NET Core host with report and connection settings configured:

```csharp
using InteractiveReport.AspNetCore;
using InteractiveReport.Client.FileDownload;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddInteractiveReports(builder.Configuration);
builder.Services.AddInteractiveReportFileDownload();

var app = builder.Build();
app.MapInteractiveReportFileDownload("/api/download");
app.Run();
```

In an existing browser-report application, keep its JSON registration and route
mapping, and add the file-download registration before `builder.Build()` and
mapping afterward. Register the shared server only once. Configure the host's
usual authentication middleware for protected reports.

Post a report state document to `POST /api/download/{name}/csv` or
`POST /api/download/{name}/html`. Exports request rows beyond the current page,
subject to the report's limits. The default `/api/download` prefix matches the
browser component's default download location.

NuGet IDs use `AurorisNet.InteractiveReports`; C# namespaces remain
`InteractiveReport.*`. Use matching versions when combining packages.

## Documentation

- [Integration API and file downloads](https://github.com/auroris/Interactive-Reports/blob/main/docs/API.md)
- [Browser download controls](https://github.com/auroris/Interactive-Reports/blob/main/docs/EMBEDDING.md)
- [Getting started and data sources](https://github.com/auroris/Interactive-Reports/blob/main/docs/GETTING-STARTED.md)
- [Authorization](https://github.com/auroris/Interactive-Reports/blob/main/docs/AUTHORIZATION.md)
- [Source and issues](https://github.com/auroris/Interactive-Reports)
