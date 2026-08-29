using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.SavedReports;
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

public sealed class ConfiguredReportDocumentHttpTests : IAsyncLifetime
{
    private const string ReportName = "orders";
    private const string Identity = "file-doc-admin";
    private string _tempRoot = "";
    private string _primaryPath = "";
    private WebApplication? _app;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _tempRoot = Directory.CreateTempSubdirectory("interactive-report-documents-").FullName;
        var documentDirectory = Path.Combine(_tempRoot, "ReportDocuments");
        Directory.CreateDirectory(documentDirectory);
        _primaryPath = Path.Combine(documentDirectory, "orders.primary.json");
        await File.WriteAllTextAsync(_primaryPath, """
            {
              "title": "Committed Primary",
              "primary": true,
              "state": {
                "activeTable": "base",
                "tables": {
                  "base": {
                    "from": "definition",
                    "composables": [
                      { "kind": "select", "columns": [ "LABEL" ] },
                      { "kind": "sort", "sorts": [ { "col": "ID", "dir": "desc" } ] }
                    ]
                  }
                }
              }
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(documentDirectory, "orders.regional.json"), """
            {
              "title": "Regional View",
              "state": {
                "activeTable": "regional",
                "tables": {
                  "regional": {
                    "from": "definition",
                    "composables": [
                      { "kind": "select", "columns": [ "ID", "LABEL" ] },
                      { "kind": "filter", "filters": [ { "expr": "ID = 1" } ] }
                    ]
                  }
                }
              }
            }
            """);

        var dataPath = Path.Combine(_tempRoot, "data.db");
        var connectionString = $"Data Source={dataPath};Pooling=False";
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE ORDERS (ID INTEGER PRIMARY KEY, LABEL TEXT NOT NULL);
                INSERT INTO ORDERS (ID, LABEL) VALUES (1, 'first'), (2, 'second');
                """;
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
            ["InteractiveReport:WhoamiEnabled"] = "true",
            ["InteractiveReport:Administrators:0"] = Identity,
            [$"InteractiveReport:Reports:{ReportName}:Connection"] = "Data",
            [$"InteractiveReport:Reports:{ReportName}:Dialect"] = "Sqlite",
            [$"InteractiveReport:Reports:{ReportName}:Sql"] = "SELECT ID, LABEL FROM ORDERS",
            [$"InteractiveReport:Reports:{ReportName}:Authorization:AllowAnonymous"] = "true",
            // This is the generated Default until a primary row titled Default exists.
            [$"InteractiveReport:Reports:{ReportName}:DefaultState:Search"] = "inline default",
            [$"InteractiveReport:Reports:{ReportName}:DocumentFiles:0"] = "ReportDocuments/orders.primary.json",
            [$"InteractiveReport:Reports:{ReportName}:DocumentFiles:1"] = "ReportDocuments/orders.regional.json",
            ["InteractiveReport:SavedReports:Connection"] = "Data",
        });
        builder.Services
            .AddInteractiveReports(builder.Configuration)
            .AddConnection("Data", _ => new SqliteConnection(connectionString));

        _app = builder.Build();
        _app.Use(async (context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, Identity)],
                authenticationType: "ConfiguredDocumentTest"));
            await next();
        });
        _app.MapInteractiveReports("/api/reports");
        await _app.StartAsync();

        var address = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        _client = new HttpClient { BaseAddress = new Uri(address) };
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
        if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true);
    }

    [Fact]
    public async Task A_primary_with_another_title_does_not_replace_Default()
    {
        var schema = await GetJson($"/api/reports/{ReportName}/schema");
        var defaultState = schema.GetProperty("defaultState");
        Assert.Equal("inline default", defaultState.GetProperty("search").GetString());
    }

    [Fact]
    public async Task Configured_alternatives_are_global_read_only_and_database_reports_remain_editable()
    {
        using var saveResponse = await _client.PostAsync(
            $"/api/reports/{ReportName}/saved",
            JsonContent.Create(new { title = "Editable", state = new { v = 3 } }));
        Assert.Equal(HttpStatusCode.Created, saveResponse.StatusCode);
        var saved = await ReadJson(saveResponse);
        Assert.False(saved.GetProperty("isReadOnly").GetBoolean());

        var visible = await GetJson($"/api/reports/{ReportName}/saved");
        Assert.All(visible.EnumerateArray(), summary =>
            Assert.False(summary.TryGetProperty("owner", out _)));
        var configured = visible.EnumerateArray()
            .Single(summary => summary.GetProperty("title").GetString() == "Regional View");
        Assert.True(configured.GetProperty("isGlobal").GetBoolean());
        Assert.True(configured.GetProperty("isReadOnly").GetBoolean());
        Assert.False(configured.GetProperty("mine").GetBoolean());
        var configuredPrimary = visible.EnumerateArray()
            .Single(summary => summary.GetProperty("title").GetString() == "Committed Primary");
        Assert.True(configuredPrimary.GetProperty("isPrimary").GetBoolean());
        Assert.Equal(3, visible.GetArrayLength());

        var id = configured.GetProperty("id").GetString()!;
        var loaded = await GetJson($"/api/reports/saved/{id}");
        Assert.True(loaded.GetProperty("summary").GetProperty("isReadOnly").GetBoolean());
        Assert.Equal("ID = 1", loaded.GetProperty("state").GetProperty("tables")
            .GetProperty("regional").GetProperty("composables")[1]
            .GetProperty("filters")[0].GetProperty("expr").GetString());

        using var update = await _client.PutAsJsonAsync(
            $"/api/reports/saved/{id}", new { title = "Changed", state = new { v = 3 } });
        Assert.Equal(HttpStatusCode.Forbidden, update.StatusCode);
        using var delete = await _client.DeleteAsync($"/api/reports/saved/{id}");
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
        Assert.True(File.Exists(_primaryPath));
    }

    [Fact]
    public async Task Configured_title_shadows_existing_database_row_and_rejects_new_collision()
    {
        var store = _app!.Services.GetRequiredService<ISavedReportStore>();
        await store.Create(new SavedReport
        {
            Id = SavedReport.NewId(),
            ReportName = ReportName,
            Title = "regional view",
            Owner = Identity,
            StateJson = "{\"v\":3,\"search\":\"database\"}",
        });

        var visible = await GetJson($"/api/reports/{ReportName}/saved");
        var matching = visible.EnumerateArray()
            .Where(summary => string.Equals(
                summary.GetProperty("title").GetString(), "Regional View", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.Single(matching);
        Assert.True(matching[0].GetProperty("isReadOnly").GetBoolean());

        using var collision = await _client.PostAsync(
            $"/api/reports/{ReportName}/saved",
            JsonContent.Create(new { title = "REGIONAL VIEW", state = new { v = 3 } }));
        Assert.Equal(HttpStatusCode.Conflict, collision.StatusCode);

        var admin = await GetAdminRows();
        Assert.Equal(2, admin.Count(row => string.Equals(
            row.GetProperty("TITLE").GetString(), "Regional View", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task Editing_a_document_file_resyncs_its_row_and_hostile_titles_stay_data()
    {
        var before = await GetAdminRows();
        var regional = before.Single(row => row.GetProperty("TITLE").GetString() == "Regional View");
        var id = regional.GetProperty("ID").GetString()!;

        // A title a careless operator might commit from a downloaded envelope: quotes,
        // brackets, SQL keywords. It must flow file → sync → listing as inert data.
        var hostile = "Renamed'; DROP TABLE [X] -- View";
        var regionalPath = Path.Combine(_tempRoot, "ReportDocuments", "orders.regional.json");
        await File.WriteAllTextAsync(regionalPath, $$"""
            {
              "title": {{JsonSerializer.Serialize(hostile)}},
              "state": {
                "activeTable": "base",
                "tables": { "base": { "from": "definition", "composables": [] } }
              }
            }
            """);
        File.SetLastWriteTimeUtc(regionalPath, DateTime.UtcNow.AddMinutes(1));

        var after = await GetAdminRows();
        var renamed = Assert.Single(after, row => row.GetProperty("ID").GetString() == id);
        Assert.Equal(hostile, renamed.GetProperty("TITLE").GetString());
        Assert.Equal("Read only", renamed.GetProperty("SCOPE").GetString());
        Assert.DoesNotContain(after, row => row.GetProperty("TITLE").GetString() == "Regional View");

        // The synced row still refuses mutation and still serves the new state.
        using var update = await _client.PutAsJsonAsync(
            $"/api/reports/saved/{id}", new { title = "Takeover", state = new { v = 3 } });
        Assert.Equal(HttpStatusCode.Forbidden, update.StatusCode);
    }

    [Fact]
    public async Task Database_saved_report_titles_are_unique_and_updates_keep_their_own_title()
    {
        using var firstResponse = await _client.PostAsync(
            $"/api/reports/{ReportName}/saved",
            JsonContent.Create(new { title = "Customer Totals", state = new { v = 3 } }));
        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        var first = await ReadJson(firstResponse);

        using var duplicate = await _client.PostAsync(
            $"/api/reports/{ReportName}/saved",
            JsonContent.Create(new { title = "customer totals", state = new { v = 3 } }));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        using var duplicateImport = await _client.PostAsync(
            $"/api/reports/admin/{ReportName}/documents",
            JsonContent.Create(new { title = "CUSTOMER TOTALS", state = new { v = 3 } }));
        Assert.Equal(HttpStatusCode.Conflict, duplicateImport.StatusCode);

        using var sameTitleUpdate = await _client.PutAsJsonAsync(
            $"/api/reports/saved/{first.GetProperty("id").GetString()}",
            new { title = "CUSTOMER TOTALS", state = new { v = 3, search = "updated" } });
        Assert.Equal(HttpStatusCode.OK, sameTitleUpdate.StatusCode);

        using var secondResponse = await _client.PostAsync(
            $"/api/reports/{ReportName}/saved",
            JsonContent.Create(new { title = "Other", state = new { v = 3 } }));
        var second = await ReadJson(secondResponse);
        using var collidingRename = await _client.PutAsJsonAsync(
            $"/api/reports/saved/{second.GetProperty("id").GetString()}",
            new { title = "Customer Totals" });
        Assert.Equal(HttpStatusCode.Conflict, collidingRename.StatusCode);
    }

    [Fact]
    public async Task Administrator_can_download_a_canonical_configured_report_document()
    {
        var admin = await GetAdminRows();
        var configured = admin.Single(row => row.GetProperty("TITLE").GetString() == "Regional View");

        using var response = await _client.GetAsync(
            $"/api/reports/admin/saved/{configured.GetProperty("ID").GetString()}/document");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.EndsWith(".json", response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        var document = await ReadJson(response);
        Assert.Equal("Regional View", document.GetProperty("title").GetString());
        Assert.False(document.GetProperty("primary").GetBoolean());
        Assert.False(document.TryGetProperty("owner", out _));
        Assert.Equal("ID = 1", document.GetProperty("state").GetProperty("tables")
            .GetProperty("regional").GetProperty("composables")[1]
            .GetProperty("filters")[0].GetProperty("expr").GetString());
    }

    [Fact]
    public async Task Administrator_upload_validates_then_imports_a_report_document()
    {
        using var invalidResponse = await _client.PostAsync(
            $"/api/reports/admin/{ReportName}/documents",
            JsonContent.Create(new
            {
                title = "Broken Candidate",
                state = new
                {
                    activeTable = "broken",
                    tables = new
                    {
                        broken = new
                        {
                            @from = "definition",
                            composables = new object[]
                            {
                                new { kind = "filter", filters = new[] { new { expr = "ID +" } } },
                            },
                        },
                    },
                },
            }));
        Assert.Equal(HttpStatusCode.BadRequest, invalidResponse.StatusCode);
        var invalid = await ReadJson(invalidResponse);
        Assert.Equal("IR-1201", invalid.GetProperty("code").GetString());
        Assert.Equal("Report state failed validation", invalid.GetProperty("title").GetString());
        Assert.Equal(
            "One or more report settings are invalid.",
            invalid.GetProperty("description").GetString());
        Assert.Contains("tables.broken", invalid.GetProperty("details").GetString());
        Assert.False(invalid.TryGetProperty("errors", out _));

        var title = $"Uploaded {Guid.NewGuid():N}";
        using var uploadResponse = await _client.PostAsync(
            $"/api/reports/admin/{ReportName}/documents",
            JsonContent.Create(new
            {
                title,
                primary = true,
                state = new
                {
                    activeTable = "uploaded",
                    tables = new
                    {
                        uploaded = new
                        {
                            @from = "definition",
                            composables = new object[]
                            {
                                new { kind = "select", columns = new[] { "ID", "LABEL" } },
                                new { kind = "filter", filters = new[] { new { expr = "ID = 2" } } },
                            },
                        },
                    },
                },
            }));
        Assert.Equal(HttpStatusCode.Created, uploadResponse.StatusCode);
        var imported = await ReadJson(uploadResponse);
        Assert.Equal(title, imported.GetProperty("title").GetString());
        Assert.False(imported.TryGetProperty("owner", out _));
        Assert.False(imported.GetProperty("isGlobal").GetBoolean());
        Assert.True(imported.GetProperty("isPrimary").GetBoolean());

        var id = imported.GetProperty("id").GetString();
        var stored = await _app!.Services.GetRequiredService<ISavedReportStore>().Get(id!);
        Assert.NotNull(stored);
        Assert.Equal(Identity, stored.Owner);
        Assert.DoesNotContain("owner", stored.StateJson, StringComparison.OrdinalIgnoreCase);
        var loaded = await GetJson($"/api/reports/saved/{id}");
        Assert.False(loaded.GetProperty("summary").TryGetProperty("owner", out _));
        Assert.Equal("ID = 2", loaded.GetProperty("state").GetProperty("tables")
            .GetProperty("uploaded").GetProperty("composables")[1]
            .GetProperty("filters")[0].GetProperty("expr").GetString());

        using var downloadResponse = await _client.GetAsync(
            $"/api/reports/admin/saved/{id}/document");
        var downloaded = await ReadJson(downloadResponse);
        Assert.Equal(title, downloaded.GetProperty("title").GetString());
        Assert.True(downloaded.GetProperty("primary").GetBoolean());
        Assert.False(downloaded.TryGetProperty("owner", out _));

        var admin = await GetAdminRows();
        Assert.DoesNotContain(admin, row =>
            row.GetProperty("TITLE").GetString() == "Broken Candidate");
    }

    [Fact]
    public async Task Primary_Default_overrides_the_generated_Default_until_unflagged()
    {
        using var saveResponse = await _client.PostAsync(
            $"/api/reports/{ReportName}/saved",
            JsonContent.Create(new
            {
                title = "Default",
                isPrimary = true,
                state = new { v = 3, search = "database default" },
            }));
        Assert.Equal(HttpStatusCode.Created, saveResponse.StatusCode);
        var saved = await ReadJson(saveResponse);
        Assert.True(saved.GetProperty("isPrimary").GetBoolean());

        var overridden = await GetJson($"/api/reports/{ReportName}/schema");
        Assert.Equal("database default", overridden.GetProperty("defaultState").GetProperty("search").GetString());

        using var unflag = await _client.PutAsJsonAsync(
            $"/api/reports/saved/{saved.GetProperty("id").GetString()}",
            new { isPrimary = false });
        Assert.Equal(HttpStatusCode.OK, unflag.StatusCode);

        var restored = await GetJson($"/api/reports/{ReportName}/schema");
        Assert.Equal("inline default", restored.GetProperty("defaultState").GetProperty("search").GetString());
    }

    private async Task<JsonElement[]> GetAdminRows()
    {
        var schema = await GetJson("/api/reports/__saved-reports/schema");
        using var response = await _client.PostAsync(
            "/api/reports/__saved-reports/query",
            JsonContent.Create(schema.GetProperty("defaultState")));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await ReadJson(response);
        return result.GetProperty("rows").EnumerateArray().Select(row => row.Clone()).ToArray();
    }

    private async Task<JsonElement> GetJson(string path)
    {
        using var response = await _client.GetAsync(path);
        return await ReadJson(response);
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(stream);
        return document.RootElement.Clone();
    }
}
