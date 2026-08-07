using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;
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
}
