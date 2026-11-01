# AurorisNet.InteractiveReports.Client.GraphQL

A query-only GraphQL.NET adapter for Interactive Reports. It lets API consumers
discover report configurations, list saved report documents, and execute a saved
document with paging, search, and sorting overrides. Requests use the same server
execution and authorization as the REST and browser clients.

## Which package should I install?

**Install this package when your consumers need GraphQL access to saved reports:**

```sh
dotnet add package AurorisNet.InteractiveReports.Client.GraphQL
```

For the interactive browser viewer, administration pages, and REST API, install
[AurorisNet.InteractiveReports](https://www.nuget.org/packages/AurorisNet.InteractiveReports/).
You can combine it with this adapter to let users author saved reports in the
browser and other applications query them through GraphQL:

```sh
dotnet add package AurorisNet.InteractiveReports
```

The GraphQL adapter does not create or edit saved reports and does not include a
browser viewer or GraphQL IDE. Configure saved-report persistence and provide
saved documents before querying them; the
[GraphQL guide](https://github.com/auroris/Interactive-Reports/blob/main/docs/GRAPHQL.md)
explains discovery, configured documents, and the complete query contract.

## How the packages fit together

| Package | Relationship to this adapter |
|---|---|
| [AurorisNet.InteractiveReports.AspNetCore](https://www.nuget.org/packages/AurorisNet.InteractiveReports.AspNetCore/) | Included dependency: report configuration, saved reports, authorization, and server execution. |
| [AurorisNet.InteractiveReports.Core](https://www.nuget.org/packages/AurorisNet.InteractiveReports.Core/) | Included through the server: report models, validation, and query engine. |
| [AurorisNet.InteractiveReports](https://www.nuget.org/packages/AurorisNet.InteractiveReports/) | Optional companion: browser UI, administration, and REST API. |
| [AurorisNet.InteractiveReports.Client.FileDownload](https://www.nuget.org/packages/AurorisNet.InteractiveReports.Client.FileDownload/) | Separate optional adapter for CSV and self-contained HTML downloads. |

This adapter calls the server directly and can run without the browser/REST
package. It targets .NET 8; the host supplies its database's ADO.NET driver.

## Register and map GraphQL

In an ASP.NET Core host with report, connection, and saved-report settings configured:

```csharp
using InteractiveReport.AspNetCore;
using InteractiveReport.Client.GraphQL;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddInteractiveReports(builder.Configuration);
builder.Services.AddInteractiveReportGraphQL();

var app = builder.Build();
app.MapInteractiveReportGraphQL("/graphql");
app.Run();
```

In an existing browser-report application, add the GraphQL registration and
mapping alongside the JSON adapter and register the shared server only once.
Configure the host's usual authentication middleware for protected reports.
The endpoint accepts HTTP GET and POST queries; mutations and subscriptions
are not supported.

NuGet IDs use `AurorisNet.InteractiveReports`; C# namespaces remain
`InteractiveReport.*`. Use matching versions when combining packages.

## Documentation

- [GraphQL queries, results, and errors](https://github.com/auroris/Interactive-Reports/blob/main/docs/GRAPHQL.md)
- [Saved reports and persistence](https://github.com/auroris/Interactive-Reports/blob/main/docs/SAVED-REPORTS.md)
- [Getting started and data sources](https://github.com/auroris/Interactive-Reports/blob/main/docs/GETTING-STARTED.md)
- [Authorization](https://github.com/auroris/Interactive-Reports/blob/main/docs/AUTHORIZATION.md)
- [Source and issues](https://github.com/auroris/Interactive-Reports)
