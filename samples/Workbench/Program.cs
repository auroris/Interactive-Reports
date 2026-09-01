// Workbench host entrypoint: assembles a SQLite-backed sample application that exercises the
// packaged REST, GraphQL, UI, authorization, and persistence surfaces. The host is
// intentionally composition-focused so it demonstrates integration without duplicating engine
// behavior.

using GraphQL.Server.Ui.GraphiQL;
using InteractiveReport.AspNetCore;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.SavedReports;
using InteractiveReport.GraphQL;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using Workbench;

var builder = WebApplication.CreateBuilder(args);

var dbPath = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "sample.db");
var connectionString = $"Data Source={dbPath}";

// Provider constraint: the sample database path is computed at runtime, so the
// ConnectionStrings entry the "order-feed" report's dataSource references is injected here
// rather than written into appsettings.json. The _ProviderName companion is the same convention
// Umbraco uses, and it is what lets that report configure no provider and no dialect.
builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
{
    ["ConnectionStrings:SampleDb"] = connectionString,
    ["ConnectionStrings:SampleDb_ProviderName"] = "Microsoft.Data.Sqlite",
});

// Provider constraint: the code-registered connection declares no dialect: the engine detects
// it from the factory's connection type (see the redistributable-package milestone).
var interactiveReports = builder.Services.AddInteractiveReports(builder.Configuration)
    .AddConnection("SampleDb", _ => new SqliteConnection(connectionString));

// Browser automation can isolate saved-report writes from the explicitly configured Workbench
// database by supplying this path and selecting the named connection in configuration.
var testSavedReportsPath = builder.Configuration["InteractiveReportTest:SavedReportsPath"];
if (!string.IsNullOrWhiteSpace(testSavedReportsPath))
{
    testSavedReportsPath = Path.GetFullPath(testSavedReportsPath);
    Directory.CreateDirectory(Path.GetDirectoryName(testSavedReportsPath)!);
    interactiveReports.AddConnection(
        "PlaywrightSavedReports",
        _ => new SqliteConnection($"Data Source={testSavedReportsPath};Pooling=False"));
}

builder.Services
    .AddAuthentication(DevAuthHandler.SchemeName)
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, DevAuthHandler>(DevAuthHandler.SchemeName, null);

// Real ASP.NET Core policy, referenced by the "regional-summary" report definition — proves the
// per-report policy gate against the host's authorization system.
builder.Services.AddAuthorization(options =>
    options.AddPolicy("WorkbenchAdmins", policy =>
        policy.RequireAssertion(ctx => ctx.User.Identity?.Name == DevAuthHandler.DefaultUser)));
builder.Services.AddInteractiveReportGraphQL();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    // The reusable packages generate XML documentation. Loading both assemblies here lets
    // Swagger show their endpoint contracts and nested report-state descriptions.
    foreach (var assembly in new[] { typeof(EndpointExtensions).Assembly, typeof(ReportState).Assembly })
    {
        var xmlPath = Path.Combine(AppContext.BaseDirectory, $"{assembly.GetName().Name}.xml");
        if (File.Exists(xmlPath)) options.IncludeXmlComments(xmlPath);
    }
});

var app = builder.Build();

SampleData.EnsureSeeded(dbPath);

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.DocumentTitle = "Interactive Reports Workbench API";
        options.DisplayRequestDuration();
    });
}

var interactiveReportLogger = app.Services.GetRequiredService<ILoggerFactory>()
    .CreateLogger("InteractiveReport");
app.MapInteractiveReports("/api/reports", interactiveReportLogger);
app.MapInteractiveReportGraphQL("/graphql");
if (app.Environment.IsDevelopment())
{
    await app.Services.GetRequiredService<ConfiguredReportDocumentSynchronizer>().EnsureSynced();
    var defaultReport = await app.Services.GetRequiredService<ISavedReportStore>()
        .FindDefault("orders")
        ?? throw new InvalidOperationException(
            "The Workbench GraphiQL example requires the configured 'orders / Default' report.");
    const string defaultQuery = """
        query FetchDefaultReport($id: ID!, $page: Int, $pageSize: Int) {
          report(id: $id, page: $page, pageSize: $pageSize) {
            columns { name label type computed }
            rows
            page { index size }
            totalRows
            elapsedMs
          }
        }
        """;
    var defaultVariables = JsonSerializer.Serialize(
        new { id = defaultReport.Id, page = 1, pageSize = 25 },
        new JsonSerializerOptions { WriteIndented = true });

    app.MapGraphQLGraphiQL("/graphiql", new GraphiQLOptions
    {
        GraphQLEndPoint = "/graphql",
        // Invariant: the graphql-ws fetcher opens a socket only for subscription operations.
        // The legacy fetcher connects eagerly, including during schema introspection.
        GraphQLWsSubscriptions = true,
        PostConfigure = (_, html) => WithDefaults(html, defaultQuery, defaultVariables),
    });
}

app.Run();

// Accepts the GraphiQL HTML template plus default query and variables JSON, and returns a
// rewritten template. It does not mutate its inputs; it throws when the upstream template no
// longer exposes the expected replacement points.
static string WithDefaults(string html, string query, string variables)
{
    const string queryProperty = "query: parameters.query,";
    const string variablesProperty = "variables: parameters.variables,";
    if (!html.Contains(queryProperty, StringComparison.Ordinal)
        || !html.Contains(variablesProperty, StringComparison.Ordinal))
    {
        throw new InvalidOperationException(
            "The GraphiQL page template no longer contains its query and variables properties.");
    }

    return html
        .Replace(
            queryProperty,
            $"query: parameters.query ?? {JsonSerializer.Serialize(query)},",
            StringComparison.Ordinal)
        .Replace(
            variablesProperty,
            $"variables: parameters.variables ?? {JsonSerializer.Serialize(variables)},",
            StringComparison.Ordinal);
}
