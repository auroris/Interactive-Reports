using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace InteractiveReport.AspNetCore.Tests;

public sealed class ConfiguredReportDocumentStartupTests
{
    [Fact]
    public async Task Two_configured_defaults_in_one_family_fail_host_startup()
    {
        // Left to run time, the second default would only collide on the database's default
        // index during reconciliation and the whole family would read as not found.
        var tempRoot = Directory.CreateTempSubdirectory("interactive-report-startup-").FullName;
        try
        {
            var documents = Path.Combine(tempRoot, "ReportDocuments");
            Directory.CreateDirectory(documents);
            const string body = """
                {
                  "title": "TITLE",
                  "default": true,
                  "state": {
                    "activeTable": "base",
                    "tables": { "base": { "from": "definition", "composables": [] } }
                  }
                }
                """;
            await File.WriteAllTextAsync(Path.Combine(documents, "a.json"), body.Replace("TITLE", "A"));
            await File.WriteAllTextAsync(Path.Combine(documents, "b.json"), body.Replace("TITLE", "B"));

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ContentRootPath = tempRoot,
                EnvironmentName = Environments.Development,
            });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["InteractiveReport:Reports:orders:Connection"] = "Data",
                ["InteractiveReport:Reports:orders:Dialect"] = "Sqlite",
                ["InteractiveReport:Reports:orders:Sql"] = "SELECT 1 AS ID",
                ["InteractiveReport:Reports:orders:Authorization:AllowAnonymous"] = "true",
                ["InteractiveReport:Reports:orders:DocumentFiles:0"] = "ReportDocuments/a.json",
                ["InteractiveReport:Reports:orders:DocumentFiles:1"] = "ReportDocuments/b.json",
                ["InteractiveReport:SavedReports:Connection"] = "Data",
            });
            builder.Services
                .AddInteractiveReports(builder.Configuration)
                .AddConnection("Data", _ => new SqliteConnection("Data Source=:memory:"));

            await using var app = builder.Build();
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync());

            Assert.Contains("only one configured report document may be marked as default", error.Message);
            Assert.Contains("a.json", error.Message);
            Assert.Contains("b.json", error.Message);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }
}
