using System.Data.Common;
using InteractiveReport.Core.Authorization;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.SavedReports;
using InteractiveReport.Core.Schema;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace InteractiveReport.Core.Tests;

public sealed class SqlLoggingTests
{
    [Fact]
    public async Task Submitted_sql_is_logged_at_debug_without_parameter_values()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText = "CREATE TABLE LOG_TEST (ID INTEGER, TENANT TEXT)";
            await setup.ExecuteNonQueryAsync();
        }

        var definition = new ReportDefinition
        {
            Name = "logging",
            Connection = "unused",
            Dialect = ReportDialect.Sqlite,
            Sql = "SELECT ID FROM LOG_TEST WHERE TENANT = @tenant",
        };
        var context = new Dictionary<string, object?> { ["tenant"] = "sensitive-tenant" };

        var debugLogger = new CapturingLogger<ReportExecutor>(LogLevel.Debug);
        await SchemaDiscovery.Discover(connection, definition, context, logger: debugLogger);

        var message = Assert.Single(debugLogger.Messages);
        Assert.Contains("Executing report SQL:", message);
        Assert.Contains(definition.Sql, message);
        Assert.Contains("1 = 0", message);
        Assert.DoesNotContain("sensitive-tenant", message);

        var informationLogger = new CapturingLogger<ReportExecutor>(LogLevel.Information);
        await SchemaDiscovery.Discover(connection, definition, context, logger: informationLogger);
        Assert.Empty(informationLogger.Messages);
    }

    [Fact]
    public async Task Supplied_store_loggers_receive_sql_without_parameter_values()
    {
        var connectionString = $"Data Source=logging-{Guid.NewGuid():n};Mode=Memory;Cache=Shared";
        await using var keepAlive = new SqliteConnection(connectionString);
        await keepAlive.OpenAsync();
        var connections = new ConnectionFactory(() => new SqliteConnection(connectionString));

        const string sensitiveTitle = "private-quarterly-plan";
        const string sensitiveIdentity = "secret-user@example.test";

        var savedLogger = new CapturingLogger<SqlSavedReportStore>(LogLevel.Debug);
        var savedReports = new SqlSavedReportStore(
            () => new SavedReportStoreConfig("logging", ReportDialect.Sqlite),
            connections,
            savedLogger);
        await savedReports.Create(new SavedReport
        {
            Id = "saved-sensitive",
            ReportName = "orders",
            Title = sensitiveTitle,
            Owner = sensitiveIdentity,
            StateJson = "{\"private\":true}",
        });

        Assert.Contains(savedLogger.Messages, message => message.Contains("CREATE TABLE", StringComparison.Ordinal));
        Assert.Contains(savedLogger.Messages, message => message.Contains("INSERT", StringComparison.OrdinalIgnoreCase));
        Assert.All(savedLogger.Messages, message =>
        {
            Assert.DoesNotContain(sensitiveTitle, message);
            Assert.DoesNotContain(sensitiveIdentity, message);
            Assert.DoesNotContain("saved-sensitive", message);
            Assert.DoesNotContain("private", message);
        });

        var authorizationLogger = new CapturingLogger<SqlReportAuthorizationStore>(LogLevel.Debug);
        var authorization = new SqlReportAuthorizationStore(
            () => new ReportAuthorizationStoreConfig("logging", ReportDialect.Sqlite),
            connections,
            authorizationLogger);
        await authorization.GrantAdministrator(sensitiveIdentity);

        Assert.Contains(
            authorizationLogger.Messages,
            message => message.Contains("IR_REPORT_AUTHORIZATION", StringComparison.Ordinal));
        Assert.All(
            authorizationLogger.Messages,
            message => Assert.DoesNotContain(sensitiveIdentity, message));
    }

    [Fact]
    public async Task Store_logging_obeys_the_supplied_log_level()
    {
        var connectionString = $"Data Source=logging-level-{Guid.NewGuid():n};Mode=Memory;Cache=Shared";
        await using var keepAlive = new SqliteConnection(connectionString);
        await keepAlive.OpenAsync();
        var logger = new CapturingLogger<SqlSavedReportStore>(LogLevel.Information);
        var store = new SqlSavedReportStore(
            () => new SavedReportStoreConfig("logging", ReportDialect.Sqlite),
            new ConnectionFactory(() => new SqliteConnection(connectionString)),
            logger);

        await store.ListAll();

        Assert.Empty(logger.Messages);
    }

    private sealed class CapturingLogger<T>(LogLevel minimumLevel) : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= minimumLevel;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) Messages.Add(formatter(state, exception));
        }
    }

    private sealed class ConnectionFactory(Func<DbConnection> create) : IReportConnectionFactory
    {
        public DbConnection CreateConnection(string name) => create();
    }
}
