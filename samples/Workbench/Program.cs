// Workbench host entrypoint: assembles a SQLite-backed sample application that exercises the
// packaged REST, GraphQL, UI, authorization, and persistence surfaces. The host is
// intentionally composition-focused so it demonstrates integration without duplicating engine
// behavior.

using GraphQL.Server.Ui.GraphiQL;
using InteractiveReport.AspNetCore;
using InteractiveReport.Client.Json;
using InteractiveReport.Client.FileDownload;
using InteractiveReport.Core.Model;
using InteractiveReport.Client.GraphQL;
using Microsoft.Data.Sqlite;
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
builder.Services.AddInteractiveReportJson();
builder.Services.AddInteractiveReportFileDownload();
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
app.MapInteractiveReportJson("/api/reports", interactiveReportLogger);
app.MapInteractiveReportFileDownload("/api/download");
// Workbench-only: the order editor behind crud.html writes through these, not through the packages.
app.MapWorkbenchOrdersCrud(connectionString);
app.MapInteractiveReportGraphQL("/graphql");
if (app.Environment.IsDevelopment())
    app.MapGraphQLGraphiQL("/graphiql", new GraphiQLOptions
    {
        GraphQLEndPoint = "/graphql",
        // Invariant: the graphql-ws fetcher opens a socket only for subscription operations.
        // The legacy fetcher connects eagerly, including during schema introspection.
        GraphQLWsSubscriptions = true,
    });

app.Run();
