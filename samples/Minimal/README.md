# Minimal Interactive Reports host

A report-only ASP.NET Core application with one anonymous orders report and an
explicit `Microsoft.Data.Sqlite` dependency. It uses the packaged viewer and JSON
API. Saved-report persistence is not configured.

## Run from this repository

Requires the .NET 10 SDK and Node.js 20 or later. From the repository root:

```sh
npm ci
npm run build:client
dotnet run --project samples/Minimal --urls http://127.0.0.1:5043
```

Open [the report](http://127.0.0.1:5043). The root redirects to
`/api/reports/orders/view`. Stop the server with Ctrl+C.

The sample host creates three orders in a shared in-memory SQLite database at
startup and keeps one connection open so the report can read them. The data is
discarded when the process exits. Interactive Reports does not create the database.

## What to copy

- `Minimal.csproj` references the server, JSON client, and SQLite provider.
- `Program.cs` registers the two services and maps the JSON endpoints.
- `appsettings.json` supplies the connection string, provider metadata, and report SQL.

For an application outside this repository, replace the two `ProjectReference`
entries with NuGet references to `InteractiveReport.AspNetCore` and
`InteractiveReport.Client.Json`. Keep the explicit SQLite package reference. The
JSON client NuGet package includes the browser assets, so a consuming application
does not need the npm build steps.

To use an existing SQLite database, remove the sample connection's `using` block
and the `CreateSampleData` method from `Program.cs`, keeping the service registration,
endpoint mapping, and `app.RunAsync()` call. Change `ConnectionStrings:SampleDb` to your database's connection
string, and update the report SQL. The `SampleDb_ProviderName` setting identifies
the provider explicitly.

The report allows anonymous access for this example. For an authenticated report,
integrate your application's authentication and remove `allowAnonymous`. See the
[getting-started guide](../../docs/GETTING-STARTED.md) for further integration options.

## Check the API

```sh
curl http://127.0.0.1:5043/api/reports/orders/schema
curl http://127.0.0.1:5043/api/reports/orders/default
```

The default response's `result.rows` contains the three sample orders.
