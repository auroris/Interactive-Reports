using InteractiveReport.AspNetCore;
using InteractiveReport.GraphQL;
using Microsoft.Data.Sqlite;
using Workbench;

var builder = WebApplication.CreateBuilder(args);

var dbPath = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "sample.db");
var connectionString = $"Data Source={dbPath}";

// The sample database path is computed at runtime, so the ConnectionStrings entry
// the "order-feed" report's dataSource references is injected here rather than
// written into appsettings.json. The _ProviderName companion is the same convention
// Umbraco uses, and it is what lets that report configure no provider and no dialect.
builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
{
    ["ConnectionStrings:SampleDb"] = connectionString,
    ["ConnectionStrings:SampleDb_ProviderName"] = "Microsoft.Data.Sqlite",
});

// The code-registered connection declares no dialect: the engine detects it from
// the factory's connection type (see the redistributable-package milestone).
var interactiveReports = builder.Services.AddInteractiveReports(builder.Configuration)
    .AddConnection("SampleDb", _ => new SqliteConnection(connectionString));

// Browser automation can isolate saved-report writes from the explicitly configured
// Workbench database by supplying this path and selecting the named connection in
// configuration.
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

// Real ASP.NET Core policy, referenced by the "regional-summary" report definition —
// proves the per-report policy gate against the host's authorization system.
builder.Services.AddAuthorization(options =>
    options.AddPolicy("WorkbenchAdmins", policy =>
        policy.RequireAssertion(ctx => ctx.User.Identity?.Name == DevAuthHandler.DefaultUser)));
builder.Services.AddInteractiveReportGraphQL();

var app = builder.Build();

SampleData.EnsureSeeded(dbPath);

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();

app.MapInteractiveReports("/api/reports");
app.MapInteractiveReportGraphQL("/graphql");

app.Run();
