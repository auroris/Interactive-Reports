using InteractiveReport.AspNetCore;
using Microsoft.Data.Sqlite;
using Workbench;

var builder = WebApplication.CreateBuilder(args);

var dbPath = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "sample.db");
var connectionString = $"Data Source={dbPath}";

var interactiveReports = builder.Services.AddInteractiveReports(builder.Configuration)
    .AddConnection("SampleDb", _ => new SqliteConnection(connectionString));

// Browser automation can isolate saved-report writes from the developer's Workbench
// database by supplying this path and selecting the named connection in configuration.
// Normal sample runs leave it unset and retain the zero-configuration App_Data store.
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

var app = builder.Build();

SampleData.EnsureSeeded(dbPath);

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();

app.MapInteractiveReports("/api/reports");

app.Run();
