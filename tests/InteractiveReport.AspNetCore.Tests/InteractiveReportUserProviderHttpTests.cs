using System.Net;
using System.Security.Claims;
using System.Text.Json;
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

public sealed class InteractiveReportUserProviderHttpTests
{
    [Fact]
    public async Task Administrator_receives_the_application_user_list_in_provider_order()
    {
        await using var host = await Start(useProvider: true);
        host.State.Users =
        [
            new InteractiveReportUser("  Ada Lovelace  ", " ada-id "),
            new InteractiveReportUser("Grace Hopper", "grace-id"),
        ];

        using var response = await host.Client.SendAsync(Request("configured-admin"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var users = await ReadJson(response);
        Assert.Equal(2, users.GetArrayLength());
        Assert.Equal("Ada Lovelace", users[0].GetProperty("display").GetString());
        Assert.Equal("ada-id", users[0].GetProperty("value").GetString());
        Assert.Equal("Grace Hopper", users[1].GetProperty("display").GetString());
        Assert.Equal("grace-id", users[1].GetProperty("value").GetString());
        Assert.Equal("configured-admin", host.State.Caller);
        Assert.Equal(1, host.State.Calls);
    }

    [Fact]
    public async Task User_directory_is_not_invoked_or_disclosed_to_non_administrators()
    {
        await using var host = await Start(useProvider: true);
        host.State.Users = [new InteractiveReportUser("Secret User", "secret-id")];

        using var anonymous = await host.Client.SendAsync(Request(identity: null));
        using var ordinary = await host.Client.SendAsync(Request("ordinary-user"));

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, ordinary.StatusCode);
        Assert.Equal(0, host.State.Calls);
    }

    [Fact]
    public async Task Missing_null_and_empty_providers_return_an_empty_list_for_free_form_entry()
    {
        await using (var missing = await Start(useProvider: false))
        {
            using var response = await missing.Client.SendAsync(Request("configured-admin"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(0, (await ReadJson(response)).GetArrayLength());
        }

        await using (var present = await Start(useProvider: true))
        {
            present.State.Users = null;
            using var nullResponse = await present.Client.SendAsync(Request("configured-admin"));
            Assert.Equal(HttpStatusCode.OK, nullResponse.StatusCode);
            Assert.Equal(0, (await ReadJson(nullResponse)).GetArrayLength());

            present.State.Users = [];
            using var emptyResponse = await present.Client.SendAsync(Request("configured-admin"));
            Assert.Equal(HttpStatusCode.OK, emptyResponse.StatusCode);
            Assert.Equal(0, (await ReadJson(emptyResponse)).GetArrayLength());
        }
    }

    [Fact]
    public async Task Invalid_or_ambiguous_provider_entries_fail_without_disclosing_details()
    {
        await using var host = await Start(useProvider: true);
        host.State.Users =
        [
            new InteractiveReportUser("First", "same-id"),
            new InteractiveReportUser("Second", " same-id "),
        ];

        using var response = await host.Client.SendAsync(Request("configured-admin"));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var problem = await ReadJson(response);
        Assert.Equal("Report execution failed", problem.GetProperty("title").GetString());
        Assert.False(problem.ToString().Contains("same-id", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<RunningHost> Start(bool useProvider)
    {
        var tempRoot = Directory.CreateTempSubdirectory("interactive-report-users-").FullName;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = tempRoot,
            EnvironmentName = Environments.Development,
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["InteractiveReport:Administrators:0"] = "configured-admin",
            ["InteractiveReport:SavedReports:DataSource"] =
                $"Data Source={Path.Combine(tempRoot, "saved.db")};Pooling=False",
            ["InteractiveReport:SavedReports:Provider"] = "sqlite",
        });

        var state = new ProviderState();
        builder.Services.AddSingleton(state);
        var reports = builder.Services.AddInteractiveReports(builder.Configuration);
        if (useProvider) reports.UseUserProvider<TestUserProvider>();

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (context.Request.Headers.TryGetValue("X-Test-Identity", out var identity)
                && !string.IsNullOrEmpty(identity))
            {
                context.User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, identity!)],
                    authenticationType: "UserProviderTest"));
            }
            await next();
        });
        app.MapInteractiveReports("/api/reports");
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new RunningHost(
            app,
            new HttpClient { BaseAddress = new Uri(address) },
            tempRoot,
            state);
    }

    private static HttpRequestMessage Request(string? identity)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/reports/admin/users");
        if (identity is not null) request.Headers.Add("X-Test-Identity", identity);
        return request;
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private sealed class ProviderState
    {
        public IReadOnlyCollection<InteractiveReportUser>? Users { get; set; }
        public int Calls { get; set; }
        public string? Caller { get; set; }
    }

    private sealed class TestUserProvider(ProviderState state) : IInteractiveReportUserProvider
    {
        public ValueTask<IReadOnlyCollection<InteractiveReportUser>?> GetUsers(
            ClaimsPrincipal administrator,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            state.Calls++;
            state.Caller = administrator.FindFirstValue(ClaimTypes.NameIdentifier);
            return ValueTask.FromResult(state.Users);
        }
    }

    private sealed class RunningHost(
        WebApplication app,
        HttpClient client,
        string tempRoot,
        ProviderState state) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;
        public ProviderState State { get; } = state;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }
}
