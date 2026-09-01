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
                && item.Message.Contains("traceId", StringComparison.Ordinal));
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
