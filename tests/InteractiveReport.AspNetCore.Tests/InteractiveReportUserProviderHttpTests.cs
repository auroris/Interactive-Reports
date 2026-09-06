using System.Net;
using System.Security.Claims;
using System.Text.Json;
using InteractiveReport.Core.Authorization;
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

public sealed class InteractiveReportUserProviderHttpTests
{
    [Fact]
    public async Task Administrator_receives_directory_entries_in_provider_order_before_known_identities()
    {
        await using var host = await Start(Directory.Provider);
        host.State.Users =
        [
            new InteractiveReportUser("  Ada Lovelace  ", " ada-id "),
            new InteractiveReportUser("Grace Hopper", "grace-id"),
        ];

        using var response = await host.Client.SendAsync(Request("configured-admin"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await ReadJson(response);
        Assert.False(result.GetProperty("truncated").GetBoolean());
        Assert.Equal(
            [
                ("Ada Lovelace", "ada-id"),
                ("Grace Hopper", "grace-id"),
                // The configured administrator is an identity the engine already knows.
                ("configured-admin", "configured-admin"),
            ],
            Users(result));
        Assert.Equal("configured-admin", host.State.Caller);
        Assert.Equal(1, host.State.Calls);
        Assert.Null(host.State.LastSearch);
        Assert.Equal(50, host.State.LastLimit);
    }

    [Fact]
    public async Task User_directory_is_not_invoked_or_disclosed_to_non_administrators()
    {
        await using var host = await Start(Directory.Provider);
        host.State.Users = [new InteractiveReportUser("Secret User", "secret-id")];

        using var anonymous = await host.Client.SendAsync(Request(identity: null));
        using var ordinary = await host.Client.SendAsync(Request("ordinary-user"));

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, ordinary.StatusCode);
        Assert.Equal(0, host.State.Calls);
    }

    [Fact]
    public async Task Without_a_directory_the_engine_still_offers_the_identities_it_knows()
    {
        await using var host = await Start(Directory.None, configuredReportUsers: ["report-reader"]);
        var authorization = host.Services.GetRequiredService<IReportAuthorizationStore>();
        await authorization.GrantAdministrator("db-admin");
        await authorization.GrantReportUser("orders", "db-reader");
        var saved = host.Services.GetRequiredService<ISavedReportStore>();
        await saved.Create(new SavedReport
        {
            Id = 0, ReportName = "orders", Title = "Owned", Owner = "report-owner", StateJson = "{}",
        });
        await saved.Create(new SavedReport
        {
            Id = 0, ReportName = "orders", Title = "Also owned", Owner = "report-owner", StateJson = "{}",
        });

        using var response = await host.Client.SendAsync(Request("configured-admin"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await ReadJson(response);
        Assert.False(result.GetProperty("truncated").GetBoolean());
        Assert.Equal(
            ["configured-admin", "db-admin", "db-reader", "report-owner", "report-reader"],
            Users(result).Select(user => user.Value).ToArray());
        Assert.All(Users(result), user => Assert.Equal(user.Value, user.Display));
    }

    [Fact]
    public async Task Null_and_empty_directories_fall_back_to_known_identities()
    {
        await using var host = await Start(Directory.Provider);

        host.State.Users = null;
        using var nullResponse = await host.Client.SendAsync(Request("configured-admin"));
        Assert.Equal(HttpStatusCode.OK, nullResponse.StatusCode);
        Assert.Equal(["configured-admin"], Users(await ReadJson(nullResponse)).Select(user => user.Value));

        host.State.Users = [];
        using var emptyResponse = await host.Client.SendAsync(Request("configured-admin", search: "zzz"));
        Assert.Equal(HttpStatusCode.OK, emptyResponse.StatusCode);
        Assert.Empty(Users(await ReadJson(emptyResponse)));
    }

    [Fact]
    public async Task Search_is_a_case_insensitive_partial_match_on_display_or_value_and_reaches_the_provider()
    {
        await using var host = await Start(Directory.Provider, cacheSeconds: 0);
        host.State.Users =
        [
            new InteractiveReportUser("Ada Lovelace", "ada-id"),
            new InteractiveReportUser("Grace Hopper", "grace-id"),
            new InteractiveReportUser("Adam Smith", "smith-id"),
        ];

        using var byDisplay = await host.Client.SendAsync(Request("configured-admin", search: "  ADA "));
        Assert.Equal(HttpStatusCode.OK, byDisplay.StatusCode);
        Assert.Equal(["ada-id", "smith-id"], Users(await ReadJson(byDisplay)).Select(user => user.Value));
        Assert.Equal("ADA", host.State.LastSearch);

        using var byValue = await host.Client.SendAsync(Request("configured-admin", search: "GRACE-"));
        Assert.Equal(["grace-id"], Users(await ReadJson(byValue)).Select(user => user.Value));

        using var knownOnly = await host.Client.SendAsync(Request("configured-admin", search: "configured"));
        Assert.Equal(["configured-admin"], Users(await ReadJson(knownOnly)).Select(user => user.Value));

        using var tooLong = await host.Client.SendAsync(Request("configured-admin", search: new string('x', 201)));
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal("IR-1405", (await ReadJson(tooLong)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Results_are_bounded_by_the_configured_limit_and_report_truncation()
    {
        await using var host = await Start(Directory.Provider, maxResults: 3, cacheSeconds: 0);
        host.State.Users = Enumerable.Range(1, 3)
            .Select(index => new InteractiveReportUser($"User {index}", $"user-{index}"))
            .ToArray();

        // The directory fills the limit exactly: presented as truncated so the UI asks for a search.
        using var filled = await host.Client.SendAsync(Request("configured-admin"));
        var filledResult = await ReadJson(filled);
        Assert.Equal(3, host.State.LastLimit);
        Assert.Equal(["user-1", "user-2", "user-3"], Users(filledResult).Select(user => user.Value));
        Assert.True(filledResult.GetProperty("truncated").GetBoolean());

        // Two directory entries plus the known administrator fit exactly: complete.
        host.State.Users = host.State.Users.Take(2).ToArray();
        using var exact = await host.Client.SendAsync(Request("configured-admin"));
        var exactResult = await ReadJson(exact);
        Assert.Equal(["user-1", "user-2", "configured-admin"], Users(exactResult).Select(user => user.Value));
        Assert.False(exactResult.GetProperty("truncated").GetBoolean());

        // Known identities beyond the limit are cut, and the cut is reported.
        await host.Services.GetRequiredService<IReportAuthorizationStore>().GrantAdministrator("zed-admin");
        using var overflow = await host.Client.SendAsync(Request("configured-admin"));
        var overflowResult = await ReadJson(overflow);
        Assert.Equal(3, Users(overflowResult).Count);
        Assert.True(overflowResult.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task No_search_directory_answers_are_memoized_per_administrator_but_searches_are_not()
    {
        await using var host = await Start(Directory.Provider, cacheSeconds: 300);
        host.State.Users = [new InteractiveReportUser("Ada Lovelace", "ada-id")];

        using var first = await host.Client.SendAsync(Request("configured-admin"));
        using var second = await host.Client.SendAsync(Request("configured-admin"));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(1, host.State.Calls);
        Assert.Equal(["ada-id", "configured-admin"], Users(await ReadJson(second)).Select(user => user.Value));

        // Identities the engine learns from storage are merged fresh even while the directory
        // answer is reused.
        await host.Services.GetRequiredService<IReportAuthorizationStore>().GrantAdministrator("new-admin");
        using var third = await host.Client.SendAsync(Request("configured-admin"));
        Assert.Equal(1, host.State.Calls);
        Assert.Equal(["ada-id", "configured-admin", "new-admin"], Users(await ReadJson(third)).Select(user => user.Value));

        using var searched = await host.Client.SendAsync(Request("configured-admin", search: "ada"));
        Assert.Equal(2, host.State.Calls);
        using var searchedAgain = await host.Client.SendAsync(Request("configured-admin", search: "ada"));
        Assert.Equal(3, host.State.Calls);

        // Another administrator gets their own directory answer.
        using var other = await host.Client.SendAsync(Request("new-admin"));
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        Assert.Equal(4, host.State.Calls);
        Assert.Equal("new-admin", host.State.Caller);
    }

    [Fact]
    public async Task Memoization_can_be_disabled()
    {
        await using var host = await Start(Directory.Provider, cacheSeconds: 0);
        host.State.Users = [new InteractiveReportUser("Ada Lovelace", "ada-id")];

        using var first = await host.Client.SendAsync(Request("configured-admin"));
        using var second = await host.Client.SendAsync(Request("configured-admin"));

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(2, host.State.Calls);
    }

    [Fact]
    public async Task Directory_callback_receives_the_search_and_answers_with_identities()
    {
        await using var host = await Start(Directory.Callback, cacheSeconds: 0);
        host.State.Identities =
        [
            new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "ada-id"), new Claim(ClaimTypes.Name, "Ada Lovelace")]),
            // No name claim at all: the value doubles as the display.
            new ClaimsIdentity([new Claim("sub", "grace-id")]),
            // A display claim that is not the identity's Name; the value is what matches "a".
            new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "torvalds-id"), new Claim("preferred_username", "linus")]),
        ];

        using var response = await host.Client.SendAsync(Request("configured-admin", search: "a"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("a", host.State.LastSearch);
        Assert.Equal(50, host.State.LastLimit);
        Assert.Equal("configured-admin", host.State.Caller);
        Assert.NotNull(host.State.RequestServices);
        Assert.Equal(
            [
                ("Ada Lovelace", "ada-id"),
                ("grace-id", "grace-id"),
                ("linus", "torvalds-id"),
                ("configured-admin", "configured-admin"),
            ],
            Users(await ReadJson(response)));
    }

    [Fact]
    public async Task Directory_callback_identities_resolve_through_the_configured_identity_claim()
    {
        await using var host = await Start(Directory.Callback, identityClaim: "email", cacheSeconds: 0);
        host.State.Identities =
        [
            new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "ada-id"), new Claim("email", "ada@example.test"), new Claim(ClaimTypes.Name, "Ada")]),
        ];

        using var response = await host.Client.SendAsync(Request("configured-admin"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(("Ada", "ada@example.test"), Users(await ReadJson(response)));

        // An identity without the configured claim is an integration mistake, not a silent omission.
        host.State.Identities = [new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "no-email")])];
        using var broken = await host.Client.SendAsync(Request("configured-admin"));
        Assert.Equal(HttpStatusCode.InternalServerError, broken.StatusCode);
        Assert.Equal("IR-1202", (await ReadJson(broken)).GetProperty("code").GetString());
        Assert.DoesNotContain("no-email", await broken.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Invalid_or_ambiguous_provider_entries_fail_without_disclosing_details()
    {
        await using var host = await Start(Directory.Provider);
        host.State.Users =
        [
            new InteractiveReportUser("First", "same-id"),
            new InteractiveReportUser("Second", " same-id "),
        ];

        using var response = await host.Client.SendAsync(Request("configured-admin"));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var problem = await ReadJson(response);
        Assert.Equal("IR-1202", problem.GetProperty("code").GetString());
        Assert.Equal("Report execution failed", problem.GetProperty("title").GetString());
        Assert.False(problem.ToString().Contains("same-id", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Startup_rejects_an_out_of_range_lookup_limit()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Start(Directory.None, maxResults: 0));
    }

    private enum Directory
    {
        None,
        Provider,
        Callback,
    }

    private static async Task<RunningHost> Start(
        Directory directory,
        int? maxResults = null,
        int? cacheSeconds = null,
        string? identityClaim = null,
        IReadOnlyList<string>? configuredReportUsers = null)
    {
        var tempRoot = System.IO.Directory.CreateTempSubdirectory("interactive-report-users-").FullName;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = tempRoot,
            EnvironmentName = Environments.Development,
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var settings = new Dictionary<string, string?>
        {
            ["InteractiveReport:Administrators:0"] = "configured-admin",
            ["InteractiveReport:SavedReports:DataSource"] =
                $"Data Source={Path.Combine(tempRoot, "saved.db")};Pooling=False",
            ["InteractiveReport:SavedReports:Provider"] = "sqlite",
            ["InteractiveReport:Reports:orders:DataSource"] =
                $"Data Source={Path.Combine(tempRoot, "saved.db")};Pooling=False",
            ["InteractiveReport:Reports:orders:Provider"] = "sqlite",
            ["InteractiveReport:Reports:orders:Sql"] = "SELECT 1 AS ID",
        };
        if (maxResults is not null)
            settings["InteractiveReport:UserDirectory:MaxResults"] = maxResults.Value.ToString();
        if (cacheSeconds is not null)
            settings["InteractiveReport:UserDirectory:CacheSeconds"] = cacheSeconds.Value.ToString();
        if (identityClaim is not null)
            settings["InteractiveReport:IdentityClaim"] = identityClaim;
        for (var index = 0; index < (configuredReportUsers?.Count ?? 0); index++)
        {
            settings["InteractiveReport:Reports:orders:Authorization:Restricted"] = "true";
            settings[$"InteractiveReport:Reports:orders:Authorization:Users:{index}"] = configuredReportUsers![index];
        }
        builder.Configuration.AddInMemoryCollection(settings);

        var state = new ProviderState();
        builder.Services.AddSingleton(state);
        var reports = builder.Services.AddInteractiveReports(builder.Configuration);
        switch (directory)
        {
            case Directory.Provider:
                reports.UseUserProvider<TestUserProvider>();
                break;
            case Directory.Callback:
                reports.UseUserDirectory((search, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    state.Record(search);
                    return ValueTask.FromResult(state.Identities);
                });
                break;
        }

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (context.Request.Headers.TryGetValue("X-Test-Identity", out var identity)
                && !string.IsNullOrEmpty(identity))
            {
                // The email claim carries the same value so a host configured with
                // IdentityClaim=email still recognizes the configured administrator.
                context.User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, identity!), new Claim("email", identity!)],
                    authenticationType: "UserProviderTest"));
            }
            await next();
        });
        app.MapInteractiveReportJson("/api/reports");
        try
        {
            await app.StartAsync();
        }
        catch
        {
            await app.DisposeAsync();
            if (System.IO.Directory.Exists(tempRoot)) System.IO.Directory.Delete(tempRoot, recursive: true);
            throw;
        }

        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new RunningHost(
            app,
            new HttpClient { BaseAddress = new Uri(address) },
            tempRoot,
            state);
    }

    private static HttpRequestMessage Request(string? identity, string? search = null)
    {
        var path = "/api/reports/admin/users";
        if (search is not null) path += "?search=" + Uri.EscapeDataString(search);
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (identity is not null) request.Headers.Add("X-Test-Identity", identity);
        return request;
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static List<(string Display, string Value)> Users(JsonElement result)
        => result.GetProperty("items").EnumerateArray()
            .Select(user => (
                user.GetProperty("display").GetString()!,
                user.GetProperty("value").GetString()!))
            .ToList();

    private sealed class ProviderState
    {
        public IReadOnlyCollection<InteractiveReportUser>? Users { get; set; }
        public IEnumerable<ClaimsIdentity>? Identities { get; set; }
        public int Calls { get; private set; }
        public string? Caller { get; private set; }
        public string? LastSearch { get; private set; }
        public int? LastLimit { get; private set; }
        public IServiceProvider? RequestServices { get; private set; }

        public void Record(InteractiveReportUserSearch search)
        {
            Calls++;
            Caller = search.Administrator.FindFirstValue(ClaimTypes.NameIdentifier);
            LastSearch = search.Search;
            LastLimit = search.Limit;
            RequestServices = search.RequestServices;
        }
    }

    private sealed class TestUserProvider(ProviderState state) : IInteractiveReportUserProvider
    {
        public ValueTask<IReadOnlyCollection<InteractiveReportUser>?> SearchUsers(
            InteractiveReportUserSearch search,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            state.Record(search);
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
        public IServiceProvider Services => app.Services;
        public ProviderState State { get; } = state;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (System.IO.Directory.Exists(tempRoot)) System.IO.Directory.Delete(tempRoot, recursive: true);
        }
    }
}
