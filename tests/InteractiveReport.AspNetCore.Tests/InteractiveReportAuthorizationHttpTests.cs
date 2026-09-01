using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using InteractiveReport.AspNetCore.Definitions;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.SavedReports;
using Microsoft.AspNetCore.Authorization;
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

public sealed class InteractiveReportAuthorizationHttpTests
{
    [Fact]
    public async Task Every_protected_endpoint_family_has_an_explicit_action()
    {
        var seen = new ConcurrentQueue<InteractiveReportAction>();
        await using var host = await Start((reports, _) =>
            reports.UseAuthorization((request, _) =>
            {
                seen.Enqueue(request.Action);
                return ValueTask.FromResult(true);
            }));

        using var schemaResponse = await host.Client.SendAsync(Request(
            HttpMethod.Get, "/api/reports/orders/schema", "action-admin"));
        Assert.Equal(HttpStatusCode.OK, schemaResponse.StatusCode);
        var state = (await ReadJson(schemaResponse)).GetProperty("defaultState");

        using var query = await host.Client.SendAsync(Request(
            HttpMethod.Post, "/api/reports/orders/query", "action-admin", state));
        Assert.Equal(HttpStatusCode.OK, query.StatusCode);
        using var export = await host.Client.SendAsync(Request(
            HttpMethod.Post, "/api/download/orders/csv", "action-admin", state));
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        using var list = await host.Client.SendAsync(Request(
            HttpMethod.Get, "/api/reports/orders", "action-admin"));
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);

        using var save = await host.Client.SendAsync(Request(
            HttpMethod.Post,
            $"/api/reports/{host.OrdersId}/saved",
            "action-admin",
            new { title = "Lifecycle", isGlobal = true, state }));
        Assert.Equal(HttpStatusCode.Created, save.StatusCode);
        var id = (await ReadJson(save)).GetProperty("id").GetInt64();

        using var makeDefault = await host.Client.SendAsync(Request(
            HttpMethod.Put,
            $"/api/reports/{id}",
            "action-admin",
            new { isDefault = true }));
        Assert.Equal(HttpStatusCode.OK, makeDefault.StatusCode);

        using var load = await host.Client.SendAsync(Request(
            HttpMethod.Get, $"/api/reports/orders/{id}", "action-admin"));
        Assert.Equal(HttpStatusCode.OK, load.StatusCode);
        using var update = await host.Client.SendAsync(Request(
            HttpMethod.Put,
            $"/api/reports/{id}",
            "action-admin",
            new
            {
                title = "Lifecycle Updated",
                owner = "next-owner",
                state,
            }));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        using var download = await host.Client.SendAsync(Request(
            HttpMethod.Get,
            $"/api/reports/admin/saved/{id}/document",
            "action-admin"));
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        using var upload = await host.Client.SendAsync(Request(
            HttpMethod.Post,
            $"/api/reports/admin/{host.OrdersId}/documents",
            "action-admin",
            new { title = "Uploaded", state }));
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        using var delete = await host.Client.SendAsync(Request(
            HttpMethod.Delete, $"/api/reports/{id}", "action-admin"));
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        using var all = await host.Client.SendAsync(Request(
            HttpMethod.Get,
            $"/api/reports/{SavedReportsListingDefinition.Name}/schema",
            "action-admin"));
        Assert.Equal(HttpStatusCode.OK, all.StatusCode);
        using var users = await host.Client.SendAsync(Request(
            HttpMethod.Get, "/api/reports/admin/users", "action-admin"));
        Assert.Equal(HttpStatusCode.OK, users.StatusCode);
        using var authorization = await host.Client.SendAsync(Request(
            HttpMethod.Get, "/api/reports/admin/authorization", "action-admin"));
        Assert.Equal(HttpStatusCode.OK, authorization.StatusCode);
        using var grantAdministrator = await host.Client.SendAsync(Request(
            HttpMethod.Post,
            "/api/reports/admin/authorization/administrators",
            "action-admin",
            new { identity = "action-admin" }));
        Assert.Equal(HttpStatusCode.NoContent, grantAdministrator.StatusCode);
        using var restriction = await host.Client.SendAsync(Request(
            HttpMethod.Put,
            "/api/reports/admin/authorization/reports/orders",
            "action-admin",
            new { restricted = false }));
        Assert.Equal(HttpStatusCode.NoContent, restriction.StatusCode);
        using var grantUser = await host.Client.SendAsync(Request(
            HttpMethod.Post,
            "/api/reports/admin/authorization/reports/orders/users",
            "action-admin",
            new { identity = "report-user" }));
        Assert.Equal(HttpStatusCode.BadRequest, grantUser.StatusCode);
        using var revokeUser = await host.Client.SendAsync(Request(
            HttpMethod.Delete,
            "/api/reports/admin/authorization/reports/orders/users",
            "action-admin",
            new { identity = "report-user" }));
        Assert.Equal(HttpStatusCode.BadRequest, revokeUser.StatusCode);
        using var revokeAdministrator = await host.Client.SendAsync(Request(
            HttpMethod.Delete,
            "/api/reports/admin/authorization/administrators",
            "action-admin",
            new { identity = "action-admin" }));
        Assert.Equal(HttpStatusCode.NoContent, revokeAdministrator.StatusCode);

        Assert.Equal(
            Enum.GetValues<InteractiveReportAction>().Order(),
            seen.Distinct().Order());
        Assert.Equal(6, seen.Count(action => action == InteractiveReportAction.ManageAuthorization));
    }

    [Fact]
    public async Task Callback_receives_intent_and_authorizes_admin_actions_when_list_is_empty()
    {
        var seen = new ConcurrentQueue<InteractiveReportAuthorizationRequest>();
        await using var host = await Start((reports, _) =>
            reports.UseAuthorization((request, _) =>
            {
                seen.Enqueue(request);
                return ValueTask.FromResult(true);
            }));

        using var save = await host.Client.SendAsync(Request(
            HttpMethod.Post,
            $"/api/reports/{host.OrdersId}/saved",
            "callback-admin",
            new { title = "Published", isGlobal = true, state = new { } }));

        Assert.Equal(HttpStatusCode.Created, save.StatusCode);
        var calls = seen.ToArray();
        Assert.Equal(
            [
                InteractiveReportAction.ReadSavedReport,
                InteractiveReportAction.CreateSavedReport,
                InteractiveReportAction.PublishGlobalReport,
            ],
            calls.Select(call => call.Action).ToArray());
        Assert.All(calls, call =>
        {
            Assert.Equal("callback-admin", call.User.FindFirstValue(ClaimTypes.NameIdentifier));
            Assert.Equal("orders", call.Resource.ReportName);
            Assert.Equal("Published", call.Resource.Candidate!.Title);
            Assert.True(call.Resource.Candidate.Public);
            Assert.True(call.Resource.Candidate.StateChanged);
            Assert.NotNull(call.Resource.Candidate.State);
        });

        using var listing = await host.Client.SendAsync(Request(
            HttpMethod.Get,
            $"/api/reports/{SavedReportsListingDefinition.Name}/schema",
            "callback-admin"));
        Assert.Equal(HttpStatusCode.OK, listing.StatusCode);
        Assert.Contains(seen, call => call.Action == InteractiveReportAction.ListAllSavedReports);
    }

    [Fact]
    public async Task Selecting_a_default_emits_update_global_and_default_actions()
    {
        var seen = new ConcurrentQueue<InteractiveReportAuthorizationRequest>();
        await using var host = await Start((reports, _) =>
            reports.UseAuthorization((request, _) =>
            {
                seen.Enqueue(request);
                return ValueTask.FromResult(true);
            }));
        using var save = await host.Client.SendAsync(Request(
            HttpMethod.Post,
            $"/api/reports/{host.OrdersId}/saved",
            "callback-admin",
            new { title = "Candidate", state = new { v = 3 } }));
        Assert.Equal(HttpStatusCode.Created, save.StatusCode);
        var id = (await ReadJson(save)).GetProperty("id").GetInt64();
        while (seen.TryDequeue(out _)) { }

        using var select = await host.Client.SendAsync(Request(
            HttpMethod.Put,
            $"/api/reports/{id}",
            "callback-admin",
            new { isDefault = true }));

        Assert.Equal(HttpStatusCode.OK, select.StatusCode);
        Assert.Equal(
            [
                InteractiveReportAction.UpdateSavedReport,
                InteractiveReportAction.PublishGlobalReport,
                InteractiveReportAction.SelectDefaultReport,
            ],
            seen.Select(call => call.Action).ToArray());
        Assert.All(seen, call =>
        {
            Assert.True(call.Resource.Candidate!.Public);
            Assert.True(call.Resource.Candidate.Default);
        });
    }

    [Fact]
    public async Task Callback_receives_typed_definition_and_can_narrow_it_before_admin_actions()
    {
        var seen = new ConcurrentQueue<InteractiveReportAction>();
        await using var host = await Start(
            (reports, _) => reports.UseAuthorization((request, _) =>
            {
                seen.Enqueue(request.Action);
                if (request.Action == InteractiveReportAction.CreateSavedReport)
                {
                    var definition = Assert.IsType<SavedReportCandidate>(
                        request.Resource.Candidate);
                    Assert.Equal("orders", definition.ReportName);
                    Assert.True(definition.Public);
                    Assert.True(definition.StateChanged);
                    var table = Assert.Single(definition.State!.Tables!).Value;
                    var filter = Assert.Single(table.Composables!, c => c.Kind == "filter");
                    Assert.IsType<FilterRule>(Assert.Single(filter.Filters!));

                    definition.Public = false;
                    definition.Title = "Server approved";
                    definition.State.Search = "server-normalized";
                }

                return ValueTask.FromResult(true);
            }),
            administrators: ["configured-admin"]);

        using var save = await host.Client.SendAsync(Request(
            HttpMethod.Post,
            $"/api/reports/{host.OrdersId}/saved",
            "ordinary-user",
            new
            {
                title = "Client proposal",
                isGlobal = true,
                state = new
                {
                    v = 3,
                    unknownClientMember = "not persisted",
                    activeTable = "orders",
                    tables = new
                    {
                        orders = new
                        {
                            from = "definition",
                            composables = new[]
                            {
                                new
                                {
                                    kind = "filter",
                                    filters = new[] { new { expr = "ID = 1" } },
                                },
                            },
                        },
                    },
                },
            }));

        Assert.Equal(HttpStatusCode.Created, save.StatusCode);
        var saved = await ReadJson(save);
        Assert.Equal("Server approved", saved.GetProperty("title").GetString());
        Assert.False(saved.GetProperty("isGlobal").GetBoolean());
        Assert.Equal(
            [InteractiveReportAction.ReadSavedReport, InteractiveReportAction.CreateSavedReport],
            seen.ToArray());

        using var load = await host.Client.SendAsync(Request(
            HttpMethod.Get,
            $"/api/reports/orders/{saved.GetProperty("id").GetInt64()}",
            "ordinary-user"));
        Assert.Equal(HttpStatusCode.OK, load.StatusCode);
        var loadedState = (await ReadJson(load)).GetProperty("state");
        Assert.Equal(
            "server-normalized",
            loadedState.GetProperty("search").GetString());
        Assert.False(loadedState.TryGetProperty("unknownClientMember", out _));
    }

    [Fact]
    public async Task Authorization_mutation_is_validated_before_any_definition_is_stored()
    {
        long? proposedId = null;
        await using var host = await Start((reports, _) =>
            reports.UseAuthorization((request, _) =>
            {
                if (request.Action == InteractiveReportAction.CreateSavedReport)
                {
                    proposedId = request.Resource.Candidate!.Id;
                    request.Resource.Candidate.State = new ReportState
                    {
                        ActiveTable = "broken",
                        Tables = new()
                        {
                            ["broken"] = new ReportTable
                            {
                                Composables =
                                [
                                    new TableComposable { Kind = "group", By = ["ORDER_ID"] },
                                ],
                            },
                        },
                    };
                }
                return ValueTask.FromResult(true);
            }));

        using var save = await host.Client.SendAsync(Request(
            HttpMethod.Post,
            $"/api/reports/{host.OrdersId}/saved",
            "ordinary-user",
            new { title = "Invalid after authorization", state = new { } }));

        Assert.Equal(HttpStatusCode.BadRequest, save.StatusCode);
        Assert.NotNull(proposedId);
        using var load = await host.Client.SendAsync(Request(
            HttpMethod.Get,
            $"/api/reports/orders/{proposedId}",
            "ordinary-user"));
        Assert.Equal(HttpStatusCode.NotFound, load.StatusCode);
    }

    [Fact]
    public async Task Update_definition_has_effective_metadata_but_only_client_authored_state()
    {
        var updateInspected = false;
        await using var host = await Start((reports, _) =>
            reports.UseAuthorization((request, _) =>
            {
                if (request.Action == InteractiveReportAction.UpdateSavedReport)
                {
                    var definition = request.Resource.Candidate!;
                    Assert.Equal(request.Resource.SavedReport!.Id, definition.Id);
                    Assert.Equal("Client update", definition.Title);
                    Assert.False(definition.Public);
                    Assert.False(definition.Default);
                    Assert.Equal("ordinary-user", definition.Owner);
                    Assert.False(definition.StateChanged);
                    Assert.Null(definition.State);

                    definition.Title = "Server update";
                    updateInspected = true;
                }
                return ValueTask.FromResult(true);
            }));

        using var save = await host.Client.SendAsync(Request(
            HttpMethod.Post,
            $"/api/reports/{host.OrdersId}/saved",
            "ordinary-user",
            new { title = "Original", state = new { v = 3, search = "untouched" } }));
        Assert.Equal(HttpStatusCode.Created, save.StatusCode);
        var id = (await ReadJson(save)).GetProperty("id").GetInt64();
        var store = host.Services.GetRequiredService<ISavedReportStore>();
        var stateBefore = (await store.Get(id))!.StateJson;

        using var update = await host.Client.SendAsync(Request(
            HttpMethod.Put,
            $"/api/reports/{id}",
            "ordinary-user",
            new { title = "Client update" }));

        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.True(updateInspected);
        Assert.Equal("Server update", (await ReadJson(update)).GetProperty("title").GetString());
        Assert.Equal(stateBefore, (await store.Get(id))!.StateJson);
    }

    [Fact]
    public async Task Privilege_added_during_authorization_emits_its_own_action()
    {
        var seen = new ConcurrentQueue<InteractiveReportAction>();
        await using var host = await Start((reports, _) =>
            reports.UseAuthorization((request, _) =>
            {
                seen.Enqueue(request.Action);
                if (request.Action == InteractiveReportAction.CreateSavedReport)
                    request.Resource.Candidate!.Public = true;
                return ValueTask.FromResult(true);
            }));

        using var save = await host.Client.SendAsync(Request(
            HttpMethod.Post,
            $"/api/reports/{host.OrdersId}/saved",
            "ordinary-user",
            new { title = "Escalated in stages", state = new { v = 3 } }));

        Assert.Equal(HttpStatusCode.Created, save.StatusCode);
        Assert.Equal(
            [
                InteractiveReportAction.ReadSavedReport,
                InteractiveReportAction.CreateSavedReport,
                InteractiveReportAction.PublishGlobalReport,
            ],
            seen.ToArray());
        var result = await ReadJson(save);
        Assert.True(result.GetProperty("isGlobal").GetBoolean());
    }

    [Fact]
    public async Task Stored_state_is_served_through_the_current_state_model()
    {
        // One model serves every document: a stored row is bound and re-serialized on read, so
        // members outside the current state model are not echoed back. Documents are not kept
        // readable across state-model changes.
        await using var host = await Start();
        var report = new SavedReport
        {
            Id = 0,
            ReportName = "orders",
            Title = "Historical",
            Owner = "ordinary-user",
            StateJson = "{\"search\":\"kept\",\"foreignShape\":{\"dropped\":true}}",
        };
        await host.Services.GetRequiredService<ISavedReportStore>().Create(report);

        using var load = await host.Client.SendAsync(Request(
            HttpMethod.Get,
            $"/api/reports/orders/{report.Id}",
            "ordinary-user"));

        Assert.Equal(HttpStatusCode.OK, load.StatusCode);
        var state = (await ReadJson(load)).GetProperty("state");
        Assert.Equal("kept", state.GetProperty("search").GetString());
        Assert.False(state.TryGetProperty("foreignShape", out _));
    }

    [Fact]
    public async Task Missing_authorizer_fails_closed_only_for_administrator_required_actions()
    {
        await using var host = await Start();

        using var privateSave = await host.Client.SendAsync(Request(
            HttpMethod.Post,
            $"/api/reports/{host.OrdersId}/saved",
            "ordinary-user",
            new { title = "Mine", state = new { v = 3 } }));
        Assert.Equal(HttpStatusCode.Created, privateSave.StatusCode);

        using var globalSave = await host.Client.SendAsync(Request(
            HttpMethod.Post,
            $"/api/reports/{host.OrdersId}/saved",
            "ordinary-user",
            new { title = "Global", isGlobal = true, state = new { v = 3 } }));
        Assert.Equal(HttpStatusCode.Forbidden, globalSave.StatusCode);

        using var listing = await host.Client.SendAsync(Request(
            HttpMethod.Get,
            $"/api/reports/{SavedReportsListingDefinition.Name}/schema",
            "ordinary-user"));
        Assert.Equal(HttpStatusCode.NotFound, listing.StatusCode);
    }

    [Fact]
    public async Task Explicit_administrator_list_is_authoritative_and_callback_can_restrict_it()
    {
        await using var host = await Start(
            (reports, _) => reports.UseAuthorization((request, _) =>
                ValueTask.FromResult(request.User.IsInRole("release"))),
            administrators: ["configured-admin"]);

        using var nonListed = await host.Client.SendAsync(Request(
            HttpMethod.Post,
            $"/api/reports/{host.OrdersId}/saved",
            "not-listed",
            new { title = "Rejected", isGlobal = true, state = new { v = 3 } },
            roles: ["release"]));
        Assert.Equal(HttpStatusCode.Forbidden, nonListed.StatusCode);

        using var restrictedAdmin = await host.Client.SendAsync(Request(
            HttpMethod.Post,
            $"/api/reports/{host.OrdersId}/saved",
            "configured-admin",
            new { title = "Restricted", isGlobal = true, state = new { v = 3 } }));
        Assert.Equal(HttpStatusCode.Forbidden, restrictedAdmin.StatusCode);

        using var allowedAdmin = await host.Client.SendAsync(Request(
            HttpMethod.Post,
            $"/api/reports/{host.OrdersId}/saved",
            "configured-admin",
            new { title = "Allowed", isGlobal = true, state = new { v = 3 } },
            roles: ["release"]));
        Assert.Equal(HttpStatusCode.Created, allowedAdmin.StatusCode);
    }

    [Fact]
    public async Task Native_AspNetCore_resource_handler_can_grant_actions()
    {
        var handler = new RecordingHandler(
            InteractiveReportAction.ReadSavedReport,
            InteractiveReportAction.CreateSavedReport,
            InteractiveReportAction.PublishGlobalReport);
        await using var host = await Start((reports, services) =>
        {
            reports.UseAspNetCoreAuthorization();
            services.AddSingleton<IAuthorizationHandler>(handler);
        });

        using var response = await host.Client.SendAsync(Request(
            HttpMethod.Post,
            $"/api/reports/{host.OrdersId}/saved",
            "native-admin",
            new { title = "Native", isGlobal = true, state = new { v = 3 } }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(
            [
                InteractiveReportAction.ReadSavedReport,
                InteractiveReportAction.CreateSavedReport,
                InteractiveReportAction.PublishGlobalReport,
            ],
            handler.Seen.Select(item => item.Action).ToArray());
        Assert.All(handler.Seen, item => Assert.Equal("orders", item.Resource.ReportName));
    }

    [Fact]
    public async Task Callback_can_delegate_to_an_AspNetCore_policy()
    {
        await using var host = await Start((reports, services) =>
        {
            services.AddAuthorization(options =>
                options.AddPolicy("CanQuery", policy => policy.RequireRole("query")));
            reports.UseAuthorization(async (request, _) =>
            {
                if (request.Action == InteractiveReportAction.ReadSavedReport) return true;
                if (request.Action != InteractiveReportAction.Query) return false;
                var authorization = request.RequestServices.GetRequiredService<IAuthorizationService>();
                return (await authorization.AuthorizeAsync(
                    request.User,
                    request.Resource,
                    "CanQuery")).Succeeded;
            });
        });

        using var allowed = await host.Client.SendAsync(Request(
            HttpMethod.Post,
            "/api/reports/orders/query",
            "query-user",
            new { v = 3 },
            roles: ["query"]));
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);

        using var denied = await host.Client.SendAsync(Request(
            HttpMethod.Post,
            "/api/reports/orders/query",
            "other-user",
            new { v = 3 }));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Theory]
    [InlineData(false, HttpStatusCode.Forbidden)]
    [InlineData(true, HttpStatusCode.InternalServerError)]
    public async Task Expected_denial_and_unexpected_failure_are_distinct(
        bool unexpected,
        HttpStatusCode expected)
    {
        await using var host = await Start((reports, _) =>
            reports.UseAuthorization((_, _) => unexpected
                ? throw new InvalidOperationException("authorization store unavailable")
                : throw new InteractiveReportAuthorizationDeniedException()));

        using var response = await host.Client.SendAsync(Request(
            HttpMethod.Post,
            "/api/reports/orders/query",
            "query-user",
            new { v = 3 }));

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Definition_policy_infrastructure_errors_are_sanitized()
    {
        await using var host = await Start((_, services) =>
            services.PostConfigure<InteractiveReportOptions>(options =>
                options.Reports["orders"].Authorization = new ReportAuthorization
                {
                    Policy = "MissingPolicyInfrastructure",
                }));

        using var response = await host.Client.SendAsync(Request(
            HttpMethod.Get, "/api/reports/orders/schema", "policy-user"));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var problem = await ReadJson(response);
        Assert.Equal("IR-1005", problem.GetProperty("code").GetString());
        Assert.Equal("Report authorization failed", problem.GetProperty("title").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("description").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("traceId").GetString()));
        Assert.DoesNotContain("MissingPolicyInfrastructure", problem.ToString());
    }

    private static async Task<RunningHost> Start(
        Action<InteractiveReportBuilder, IServiceCollection>? configure = null,
        IReadOnlyList<string>? administrators = null)
    {
        var tempRoot = Directory.CreateTempSubdirectory("interactive-report-authorization-").FullName;
        var dataPath = Path.Combine(tempRoot, "data.db");
        var connectionString = $"Data Source={dataPath};Pooling=False";
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE ORDERS (ID INTEGER PRIMARY KEY, LABEL TEXT NOT NULL);"
                                  + "INSERT INTO ORDERS (ID, LABEL) VALUES (1, 'first');";
            await command.ExecuteNonQueryAsync();
        }

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = tempRoot,
            EnvironmentName = Environments.Development,
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var settings = new Dictionary<string, string?>
        {
            ["InteractiveReport:Reports:orders:Connection"] = "Data",
            ["InteractiveReport:Reports:orders:Dialect"] = "Sqlite",
            ["InteractiveReport:Reports:orders:Sql"] = "SELECT ID, LABEL FROM ORDERS",
            ["InteractiveReport:Reports:orders:Authorization:AllowAnonymous"] = "true",
            ["InteractiveReport:SavedReports:Connection"] = "Data",
        };
        if (administrators is not null)
        {
            for (var i = 0; i < administrators.Count; i++)
                settings[$"InteractiveReport:Administrators:{i}"] = administrators[i];
        }
        builder.Configuration.AddInMemoryCollection(settings);

        var reports = builder.Services
            .AddInteractiveReports(builder.Configuration)
            .AddConnection("Data", _ => new SqliteConnection(connectionString));
        configure?.Invoke(reports, builder.Services);
        builder.Services.AddInteractiveReportFileDownload();

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (context.Request.Headers.TryGetValue("X-Test-Identity", out var identity)
                && !string.IsNullOrEmpty(identity))
            {
                var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, identity!) };
                foreach (var role in context.Request.Headers["X-Test-Role"])
                    claims.Add(new Claim(ClaimTypes.Role, role!));
                context.User = new ClaimsPrincipal(new ClaimsIdentity(
                    claims,
                    authenticationType: "AuthorizationTest"));
            }
            await next();
        });
        app.MapInteractiveReportJson("/api/reports");
        app.MapInteractiveReportFileDownload("/api/download");
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var ordersId = await ReportDocumentTestIds.Default(app.Services, "orders");
        return new RunningHost(
            app,
            new HttpClient { BaseAddress = new Uri(address) },
            tempRoot,
            ordersId);
    }

    private static HttpRequestMessage Request(
        HttpMethod method,
        string path,
        string? identity,
        object? body = null,
        IReadOnlyCollection<string>? roles = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (identity is not null) request.Headers.Add("X-Test-Identity", identity);
        if (roles is not null)
        {
            foreach (var role in roles) request.Headers.Add("X-Test-Role", role);
        }
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(stream);
        return document.RootElement.Clone();
    }

    private sealed class RecordingHandler(params InteractiveReportAction[] allowed)
        : AuthorizationHandler<
            InteractiveReportAuthorizationRequirement,
            InteractiveReportAuthorizationResource>
    {
        private readonly HashSet<InteractiveReportAction> _allowed = [.. allowed];
        public ConcurrentQueue<(
            InteractiveReportAction Action,
            InteractiveReportAuthorizationResource Resource)> Seen
            { get; } = new();

        protected override Task HandleRequirementAsync(
            AuthorizationHandlerContext context,
            InteractiveReportAuthorizationRequirement requirement,
            InteractiveReportAuthorizationResource resource)
        {
            Seen.Enqueue((requirement.Action, resource));
            if (_allowed.Contains(requirement.Action)) context.Succeed(requirement);
            return Task.CompletedTask;
        }
    }

    private sealed class RunningHost(
        WebApplication app,
        HttpClient client,
        string tempRoot,
        long ordersId) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;
        public IServiceProvider Services => app.Services;
        public long OrdersId { get; } = ordersId;

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
