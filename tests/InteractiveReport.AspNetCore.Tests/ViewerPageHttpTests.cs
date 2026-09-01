using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace InteractiveReport.AspNetCore.Tests;

/// <summary>
/// The packaged pages: anonymous shells that render identically for any report name
/// (no existence disclosure — the element's schema call is the gate), reference the
/// packaged script by absolute prefix so api-base inference works, encode every
/// injected value, and disappear behind ViewerPagesEnabled.
/// </summary>
public sealed class ViewerPageHttpTests : IAsyncLifetime
{
    private string _tempRoot = "";
    private WebApplication? _app;
    private HttpClient _client = null!;
    private long _securedId;

    public async Task InitializeAsync()
    {
        _tempRoot = Directory.CreateTempSubdirectory("interactive-report-pages-").FullName;
        var dataPath = Path.Combine(_tempRoot, "pages-data.db");
        var connectionString = $"Data Source={dataPath};Pooling=False";

        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE IR_PAGE_TEST (ID INTEGER PRIMARY KEY); INSERT INTO IR_PAGE_TEST (ID) VALUES (1);";
            await command.ExecuteNonQueryAsync();
        }

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = _tempRoot,
            EnvironmentName = Environments.Development,
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            // Deliberately NOT allowAnonymous: proves the shell is public while the
            // data endpoints stay gated.
            ["InteractiveReport:Reports:secured:DataSource"] = connectionString,
            ["InteractiveReport:Reports:secured:Provider"] = "sqlite",
            ["InteractiveReport:Reports:secured:Sql"] = "SELECT ID FROM IR_PAGE_TEST",
            ["InteractiveReport:SavedReports:DataSource"] = connectionString,
            ["InteractiveReport:SavedReports:Provider"] = "sqlite",
        });
        builder.Services.AddInteractiveReports(builder.Configuration);

        _app = builder.Build();
        _app.MapInteractiveReports("/api/reports");
        await _app.StartAsync();

        var address = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        _client = new HttpClient { BaseAddress = new Uri(address) };
        _securedId = await ReportDocumentTestIds.Default(_app.Services, "secured");
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    [Fact]
    public async Task The_viewer_shell_is_anonymous_while_the_data_endpoints_stay_gated()
    {
        using var page = await _client.GetAsync($"/api/reports/{_securedId}/view");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", page.Headers.CacheControl?.ToString());
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("src=\"/api/reports/ui/ir.js\"", html);
        Assert.Contains($"<interactive-report report=\"{_securedId}\">", html);
        Assert.DoesNotContain("api-base", html);

        using var schema = await _client.GetAsync("/api/reports/secured/schema");
        Assert.Equal(HttpStatusCode.Unauthorized, schema.StatusCode);
    }

    [Fact]
    public async Task The_shell_renders_identically_for_unknown_ids_and_encodes_injected_values()
    {
        using var unknown = await _client.GetAsync($"/api/reports/{long.MaxValue}/view");
        Assert.Equal(HttpStatusCode.OK, unknown.StatusCode);
        Assert.Contains($"report=\"{long.MaxValue}\"", await unknown.Content.ReadAsStringAsync());

        using var hostile = await _client.GetAsync($"/api/reports/{long.MaxValue}/view?saved-report=%22%3E%3Cimg%3E");
        Assert.Equal(HttpStatusCode.OK, hostile.StatusCode);
        var html = await hostile.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<script>", html.Replace("<script type=\"module\"", ""));
        Assert.DoesNotContain("\"><img>", html);
        Assert.Contains("saved-report=", html);
    }

    [Fact]
    public async Task The_admin_shell_serves_the_admin_bundle()
    {
        using var page = await _client.GetAsync("/api/reports/admin");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("src=\"/api/reports/ui/ir-admin.js\"", html);
        Assert.Contains("<interactive-report-admin>", html);
    }

    [Fact]
    public async Task The_packaged_pages_negotiate_Canadian_French()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/reports/{_securedId}/view");
        request.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue("en", 0.5));
        request.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue("fr-CA", 0.9));
        using var page = await _client.SendAsync(request);
        var html = await page.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("<html lang=\"fr-CA\">", html);
        Assert.Contains("Cette page nécessite JavaScript", html);

        using var adminRequest = new HttpRequestMessage(HttpMethod.Get, "/api/reports/admin");
        adminRequest.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue("fr"));
        using var admin = await _client.SendAsync(adminRequest);
        var adminHtml = await admin.Content.ReadAsStringAsync();
        Assert.Contains("<html lang=\"fr-CA\">", adminHtml);
        Assert.Contains("<title>Administration des rapports enregistrés</title>", adminHtml);
    }

    [Fact]
    public async Task Disabled_pages_return_not_found()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = _tempRoot,
            EnvironmentName = Environments.Development,
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["InteractiveReport:ViewerPagesEnabled"] = "false",
        });
        builder.Services.AddInteractiveReports(builder.Configuration);

        await using var app = builder.Build();
        app.MapInteractiveReports("/api/reports");
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };

        using var view = await client.GetAsync("/api/reports/1/view");
        Assert.Equal(HttpStatusCode.NotFound, view.StatusCode);
        using var admin = await client.GetAsync("/api/reports/admin");
        Assert.Equal(HttpStatusCode.NotFound, admin.StatusCode);
        await app.StopAsync();
    }
}
