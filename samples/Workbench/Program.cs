using InteractiveReport.AspNetCore;
using Microsoft.Data.Sqlite;
using Workbench;

var builder = WebApplication.CreateBuilder(args);

var dbPath = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "sample.db");
var connectionString = $"Data Source={dbPath}";

builder.Services.AddInteractiveReports(builder.Configuration)
    .AddConnection("SampleDb", _ => new SqliteConnection(connectionString));

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
