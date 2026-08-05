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

var app = builder.Build();

SampleData.EnsureSeeded(dbPath);

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();

app.MapInteractiveReports("/api/reports");

app.Run();
