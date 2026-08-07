using System.Data.Common;
using InteractiveReport.Core.Model;
using Microsoft.Extensions.Logging;

namespace InteractiveReport.Core.Execution;

/// <summary>
/// Owns the connection lifecycle and per-session configuration required by report
/// execution. Callers receive an open, prepared connection and remain responsible for
/// disposing it.
/// </summary>
internal sealed class ReportConnectionManager(
    IReportConnectionFactory connections,
    ILogger? logger = null)
{
    public async Task<DbConnection> Open(ReportDefinition definition, CancellationToken ct)
    {
        var connection = connections.CreateConnection(definition.Connection);
        try
        {
            await connection.OpenAsync(ct);
            await ApplySessionTimeZone(connection, definition, logger, ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Only Oracle and Postgres expose a session timezone. The configured value has the
    /// same trust level as the report SQL. These statements do not accept parameters, so
    /// the value is escaped before it is placed in the statement.
    /// </summary>
    private static async Task ApplySessionTimeZone(
        DbConnection connection,
        ReportDefinition definition,
        ILogger? logger,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(definition.TimeZone)) return;

        var timeZone = definition.TimeZone.Trim().Replace("'", "''");
        var sql = definition.Dialect switch
        {
            ReportDialect.Oracle => $"ALTER SESSION SET TIME_ZONE = '{timeZone}'",
            ReportDialect.Postgres => $"SET TIME ZONE '{timeZone}'",
            _ => null,
        };
        if (sql is null) return;

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        logger?.LogDebug("Executing report SQL:\n{Sql}", command.CommandText);
        await command.ExecuteNonQueryAsync(ct);
    }
}
