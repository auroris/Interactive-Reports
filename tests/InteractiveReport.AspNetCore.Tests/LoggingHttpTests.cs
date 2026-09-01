using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using InteractiveReport.Core.Model;
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

public sealed class LoggingHttpTests
{
    [Fact]
    public void Registration_without_a_logger_keeps_the_package_sink_empty()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();
        services.AddSingleton<IConfiguration>(configuration);

        services.AddInteractiveReports(configuration);

        using var provider = services.BuildServiceProvider();
        Assert.Null(provider.GetRequiredService<InteractiveReportLogging>().Logger);
    }

    [Fact]
    public void Registration_logger_becomes_the_single_package_sink()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();
        services.AddSingleton<IConfiguration>(configuration);
        var logger = new CapturingLogger();

        services.AddInteractiveReports(configuration).UseLogger(logger);

        using var provider = services.BuildServiceProvider();
        Assert.Same(
            logger,
            provider.GetRequiredService<InteractiveReportLogging>().Logger);
    }

    [Fact]
    public async Task Mapping_logger_receives_request_and_internal_events()
    {
        var tempRoot = Directory.CreateTempSubdirectory("interactive-report-logging-").FullName;
        var connectionString = $"Data Source={Path.Combine(tempRoot, "logging.db")};Pooling=False";
        WebApplication? app = null;
        HttpClient? client = null;
        try
        {
            await using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE LOG_ROWS (ID INTEGER, LABEL TEXT); INSERT INTO LOG_ROWS VALUES (1, 'private-row-value')";
                await command.ExecuteNonQueryAsync();
            }

            var logger = new CapturingLogger();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ContentRootPath = tempRoot,
                EnvironmentName = Environments.Development,
            });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["InteractiveReport:Reports:logged:Connection"] = "Data",
                ["InteractiveReport:Reports:logged:Dialect"] = "Sqlite",
                ["InteractiveReport:Reports:logged:Sql"] = "SELECT ID, LABEL FROM LOG_ROWS",
                ["InteractiveReport:Reports:logged:Authorization:AllowAnonymous"] = "true",
                ["InteractiveReport:Reports:broken:Connection"] = "Data",
                ["InteractiveReport:Reports:broken:Dialect"] = "Sqlite",
                ["InteractiveReport:Reports:broken:Sql"] = "SELECT * FROM MISSING_LOG_TABLE",
                ["InteractiveReport:Reports:broken:Authorization:AllowAnonymous"] = "true",
                ["InteractiveReport:SavedReports:Connection"] = "Data",
            });
            builder.Services
                .AddInteractiveReports(builder.Configuration)
                .AddConnection("Data", _ => new SqliteConnection(connectionString));

            app = builder.Build();
            app.MapInteractiveReportJson("/api/reports", logger);
            await app.StartAsync();

            var address = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            client = new HttpClient { BaseAddress = new Uri(address) };

            using var schema = await client.GetAsync("/api/reports/logged/schema");
            Assert.Equal(HttpStatusCode.OK, schema.StatusCode);
            using var query = await client.PostAsJsonAsync(
                "/api/reports/logged/query",
                new ReportState());
            Assert.Equal(HttpStatusCode.OK, query.StatusCode);
            using var missing = await client.GetAsync("/api/reports/missing/schema");
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            using var broken = await client.GetAsync("/api/reports/broken/schema");
            Assert.Equal(HttpStatusCode.InternalServerError, broken.StatusCode);

            Assert.Contains(logger.Events, item =>
                item.Level == LogLevel.Information
                && item.Message.Contains("startup validation completed", StringComparison.Ordinal));
            Assert.Contains(logger.Events, item =>
                item.Level == LogLevel.Information
                && item.Message.Contains("GET /api/reports/logged/schema started", StringComparison.Ordinal));
            Assert.Contains(logger.Events, item =>
                item.Level == LogLevel.Information
                && item.Message.Contains("completed with 404", StringComparison.Ordinal));
            Assert.Contains(logger.Events, item =>
                item.Level == LogLevel.Debug
                && item.Message.Contains("Authorization granted", StringComparison.Ordinal));
            Assert.Contains(logger.Events, item =>
                item.Level == LogLevel.Debug
                && item.Message.Contains("Executing report SQL", StringComparison.Ordinal));
            Assert.Contains(logger.Events, item =>
                item.Level == LogLevel.Information
                && item.Message.Contains("query completed", StringComparison.Ordinal));
            Assert.Contains(logger.Events, item =>
                item.Level == LogLevel.Error
                && item.Message.Contains("schema discovery failed", StringComparison.Ordinal)
                && item.Message.Contains("Category:", StringComparison.Ordinal)
                && item.Message.Contains("Hint:", StringComparison.Ordinal)
                && item.Message.Contains("traceId", StringComparison.Ordinal));
            Assert.Contains(logger.Events, item =>
                item.Level == LogLevel.Error
                && item.Message.Contains("Schema discovery probe failed for report 'broken'", StringComparison.Ordinal)
                && item.Message.Contains("Hint:", StringComparison.Ordinal));
            Assert.All(logger.Events, item =>
                Assert.DoesNotContain("private-row-value", item.Message));
        }
        finally
        {
            client?.Dispose();
            if (app is not null)
            {
                await app.StopAsync();
                await app.DisposeAsync();
            }
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Database_connection_failure_logs_classified_diagnostics_and_hint()
    {
        var tempRoot = Directory.CreateTempSubdirectory("interactive-report-conn-logging-").FullName;
        WebApplication? app = null;
        HttpClient? client = null;
        try
        {
            var logger = new CapturingLogger();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ContentRootPath = tempRoot,
                EnvironmentName = Environments.Development,
            });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["InteractiveReport:Reports:unreachable:Connection"] = "BadConnection",
                ["InteractiveReport:Reports:unreachable:Dialect"] = "Sqlite",
                ["InteractiveReport:Reports:unreachable:Sql"] = "SELECT 1 AS ID",
                ["InteractiveReport:Reports:unreachable:Authorization:AllowAnonymous"] = "true",
            });
            builder.Services
                .AddInteractiveReports(builder.Configuration)
                .AddConnection("BadConnection", _ => new SqliteConnection("Data Source=/invalid_path/cannot_open/db.sqlite;Mode=ReadOnly"));

            app = builder.Build();
            app.MapInteractiveReportJson("/api/reports", logger);
            await app.StartAsync();

            var address = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            client = new HttpClient { BaseAddress = new Uri(address) };

            using var response = await client.GetAsync("/api/reports/unreachable/schema");
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

            Assert.Contains(logger.Events, item =>
                item.Level == LogLevel.Error
                && item.Message.Contains("Failed to open database connection 'BadConnection' for report 'unreachable'", StringComparison.Ordinal)
                && item.Message.Contains("Hint:", StringComparison.Ordinal));

            Assert.Contains(logger.Events, item =>
                item.Level == LogLevel.Error
                && item.Message.Contains("Report unreachable: schema discovery failed with database error", StringComparison.Ordinal)
                && item.Message.Contains("Hint:", StringComparison.Ordinal));
        }
        finally
        {
            client?.Dispose();
            if (app is not null)
            {
                await app.StopAsync();
                await app.DisposeAsync();
            }
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Authorization_denials_log_specific_reasons()
    {
        var tempRoot = Directory.CreateTempSubdirectory("interactive-report-auth-logging-").FullName;
        var connectionString = $"Data Source={Path.Combine(tempRoot, "auth_logging.db")};Pooling=False";
        WebApplication? app = null;
        HttpClient? client = null;
        try
        {
            await using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE LOG_AUTH (ID INTEGER, LABEL TEXT); INSERT INTO LOG_AUTH VALUES (1, 'val')";
                await command.ExecuteNonQueryAsync();
            }

            var logger = new CapturingLogger();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ContentRootPath = tempRoot,
                EnvironmentName = Environments.Development,
            });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["InteractiveReport:Reports:authonly:Connection"] = "Data",
                ["InteractiveReport:Reports:authonly:Dialect"] = "Sqlite",
                ["InteractiveReport:Reports:authonly:Sql"] = "SELECT ID, LABEL FROM LOG_AUTH",
                ["InteractiveReport:Reports:adminonly:Connection"] = "Data",
                ["InteractiveReport:Reports:adminonly:Dialect"] = "Sqlite",
                ["InteractiveReport:Reports:adminonly:Sql"] = "SELECT ID, LABEL FROM LOG_AUTH",
                ["InteractiveReport:Reports:adminonly:Authorization:AdministratorsOnly"] = "true",
                ["InteractiveReport:Reports:restricted:Connection"] = "Data",
                ["InteractiveReport:Reports:restricted:Dialect"] = "Sqlite",
                ["InteractiveReport:Reports:restricted:Sql"] = "SELECT ID, LABEL FROM LOG_AUTH",
                ["InteractiveReport:Reports:restricted:Authorization:Restricted"] = "true",
                ["InteractiveReport:SavedReports:Connection"] = "Data",
            });
            builder.Services
                .AddInteractiveReports(builder.Configuration)
                .AddConnection("Data", _ => new SqliteConnection(connectionString));

            app = builder.Build();
            app.MapInteractiveReportJson("/api/reports", logger);
            await app.StartAsync();

            var address = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            client = new HttpClient { BaseAddress = new Uri(address) };

            using var unauth = await client.GetAsync("/api/reports/authonly/schema");
            Assert.Equal(HttpStatusCode.Unauthorized, unauth.StatusCode);

            using var adminUnauth = await client.GetAsync("/api/reports/adminonly/schema");
            Assert.Equal(HttpStatusCode.Unauthorized, adminUnauth.StatusCode);

            Assert.Contains(logger.Events, item =>
                item.Level == LogLevel.Debug
                && item.Message.Contains("Access denied for report 'authonly': caller is not authenticated", StringComparison.Ordinal));

            Assert.Contains(logger.Events, item =>
                item.Level == LogLevel.Debug
                && item.Message.Contains("Access denied for report 'adminonly': caller is not authenticated for administrators-only report", StringComparison.Ordinal));
        }
        finally
        {
            client?.Dispose();
            if (app is not null)
            {
                await app.StopAsync();
                await app.DisposeAsync();
            }
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Saved_report_lifecycle_and_admin_mutations_emit_audit_logs()
    {
        var tempRoot = Directory.CreateTempSubdirectory("interactive-report-lifecycle-logging-").FullName;
        var connectionString = $"Data Source={Path.Combine(tempRoot, "lifecycle.db")};Pooling=False";
        WebApplication? app = null;
        HttpClient? client = null;
        try
        {
            await using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE LIFE_ROWS (ID INTEGER, LABEL TEXT); INSERT INTO LIFE_ROWS VALUES (1, 'test')";
                await command.ExecuteNonQueryAsync();
            }

            var logger = new CapturingLogger();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ContentRootPath = tempRoot,
                EnvironmentName = Environments.Development,
            });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["InteractiveReport:Administrators:0"] = "admin-user",
                ["InteractiveReport:Reports:demo:Connection"] = "Data",
                ["InteractiveReport:Reports:demo:Dialect"] = "Sqlite",
                ["InteractiveReport:Reports:demo:Sql"] = "SELECT ID, LABEL FROM LIFE_ROWS",
                ["InteractiveReport:Reports:demo:Authorization:AllowAnonymous"] = "true",
                ["InteractiveReport:Reports:restricted_demo:Connection"] = "Data",
                ["InteractiveReport:Reports:restricted_demo:Dialect"] = "Sqlite",
                ["InteractiveReport:Reports:restricted_demo:Sql"] = "SELECT ID, LABEL FROM LIFE_ROWS",
                ["InteractiveReport:SavedReports:Connection"] = "Data",
            });
            builder.Services
                .AddInteractiveReports(builder.Configuration)
                .AddConnection("Data", _ => new SqliteConnection(connectionString));

            app = builder.Build();
            app.MapInteractiveReportJson("/api/reports", logger);
            await app.StartAsync();

            var server = app.Services.GetRequiredService<IInteractiveReportServer>();
            var claims = new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity(
                    [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "admin-user")],
                    "TestAuth"));
            var context = new InteractiveReportRequestContext
            {
                User = claims,
                RequestServices = app.Services,
                TraceIdentifier = "test-trace-123",
            };

            // 1. Get initial default document to find anchor
            var list = await server.ListSavedReports("demo", context);
            Assert.Null(list.Failure);
            var anchorId = list.Value!.First().Id;

            // 2. Save document (creates saved report)
            var created = await server.SaveDocument(
                anchorId,
                _ => Task.FromResult<SaveReportRequest?>(new SaveReportRequest { Title = "Custom View", State = new ReportState(), IsGlobal = false }),
                context);
            Assert.Null(created.Failure);
            var newId = created.Value!.Id;

            // 3. Update document
            var updated = await server.UpdateDocument(
                newId,
                _ => Task.FromResult<UpdateSavedReportRequest?>(new UpdateSavedReportRequest { Title = "Renamed View" }),
                context);
            Assert.Null(updated.Failure);

            // 4. Export document
            var exported = await server.ExportDocument(newId, context);
            Assert.Null(exported.Failure);

            // 5. Delete document
            var deleted = await server.DeleteDocument(newId, context);
            Assert.Null(deleted.Failure);

            // 6. Admin mutations
            var grantAdmin = await server.GrantAdministrator(_ => Task.FromResult<string?>("new-admin"), context);
            Assert.Null(grantAdmin.Failure);

            var revokeAdmin = await server.RevokeAdministrator(_ => Task.FromResult<string?>("new-admin"), context);
            Assert.Null(revokeAdmin.Failure);

            var setRestricted = await server.SetReportRestriction("restricted_demo", _ => Task.FromResult<bool?>(true), context);
            Assert.Null(setRestricted.Failure);

            var grantUser = await server.GrantReportUser("restricted_demo", _ => Task.FromResult<string?>("allowed-user"), context);
            Assert.Null(grantUser.Failure);

            var revokeUser = await server.RevokeReportUser("restricted_demo", _ => Task.FromResult<string?>("allowed-user"), context);
            Assert.Null(revokeUser.Failure);

            // Assertions on log events
            Assert.Contains(logger.Events, item =>
                item.Level == LogLevel.Information
                && item.Message.Contains("Created saved report", StringComparison.Ordinal)
                && item.Message.Contains("Custom View", StringComparison.Ordinal));

            Assert.Contains(logger.Events, item =>
                item.Level == LogLevel.Information
                && item.Message.Contains("Updated saved report", StringComparison.Ordinal)
                && item.Message.Contains("Renamed View", StringComparison.Ordinal));

            Assert.Contains(logger.Events, item =>
                item.Level == LogLevel.Information
                && item.Message.Contains("Exported saved report", StringComparison.Ordinal));

            Assert.Contains(logger.Events, item =>
                item.Level == LogLevel.Information
                && item.Message.Contains("Deleted saved report", StringComparison.Ordinal));

            Assert.Contains(logger.Events, item =>
                item.Level == LogLevel.Information
                && item.Message.Contains("Administrative authorization mutation", StringComparison.Ordinal)
                && item.Message.Contains("new-admin", StringComparison.Ordinal));

            Assert.Contains(logger.Events, item =>
                item.Level == LogLevel.Information
                && item.Message.Contains("Set report restriction for report 'restricted_demo' to True", StringComparison.Ordinal));

            Assert.Contains(logger.Events, item =>
                item.Level == LogLevel.Information
                && item.Message.Contains("Updated user grant on report 'restricted_demo' for identity 'allowed-user'", StringComparison.Ordinal));
        }
        finally
        {
            client?.Dispose();
            if (app is not null)
            {
                await app.StopAsync();
                await app.DisposeAsync();
            }
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Events { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Events.Enqueue((logLevel, formatter(state, exception)));
    }
}
