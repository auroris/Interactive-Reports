using System.Collections.Concurrent;
using System.Data.Common;
using System.Globalization;
using InteractiveReport.Core.Composition;
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
    /// <summary>Per connection name: whether SQL Server SNAPSHOT isolation is enabled.</summary>
    private readonly ConcurrentDictionary<string, bool> _snapshotCapable = new(StringComparer.Ordinal);

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
    /// Begins a read-only transaction so a request's count, aggregate, break-total,
    /// and page reads see one database snapshot instead of racing concurrent commits.
    /// Best-effort by design: SQL Server databases without ALLOW_SNAPSHOT_ISOLATION
    /// (probed once per connection name) and providers that refuse the isolation
    /// level fall back to null — the prior behavior of separate autocommit reads —
    /// rather than trading consistency for read locks the host never asked for.
    /// </summary>
    public async Task<DbTransaction?> TryBeginConsistentRead(
        DbConnection connection,
        ReportDefinition definition,
        CancellationToken ct)
    {
        var dialect = definition.GetEffectiveDialect();
        if (dialect == ReportDialect.SqlServer
            && !await SnapshotIsolationEnabled(connection, definition.Connection, ct))
            return null;

        try
        {
            return await connection.BeginTransactionAsync(DialectSupport.ConsistentReadIsolation(dialect), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogDebug(
                ex,
                "Report connection {Connection}: consistent-read transaction unavailable; statements run individually.",
                definition.Connection);
            return null;
        }
    }

    /// <summary>
    /// Re-probe after a mid-flight capability change: SQL Server raises 3952 when
    /// SNAPSHOT was disabled after the capability was cached. The failed request
    /// still surfaces; subsequent requests degrade instead of failing repeatedly.
    /// </summary>
    public void NoteReadFailure(ReportDefinition definition, Exception exception)
    {
        if (exception is DbException dbException
            && DbErrorClassifier.IsSnapshotIsolationUnavailable(definition.GetEffectiveDialect(), dbException))
            _snapshotCapable.TryRemove(definition.Connection, out _);
    }

    private async Task<bool> SnapshotIsolationEnabled(
        DbConnection connection,
        string connectionName,
        CancellationToken ct)
    {
        if (_snapshotCapable.TryGetValue(connectionName, out var capable)) return capable;

        try
        {
            // Rows of sys.databases are always visible for the caller's own database.
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT snapshot_isolation_state FROM sys.databases WHERE database_id = DB_ID()";
            capable = Convert.ToInt32(
                await command.ExecuteScalarAsync(ct) ?? 0,
                CultureInfo.InvariantCulture) == 1;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogDebug(ex, "Report connection {Connection}: snapshot capability probe failed.", connectionName);
            capable = false;
        }

        _snapshotCapable[connectionName] = capable;
        if (!capable)
            logger?.LogInformation(
                "Report connection {Connection}: SNAPSHOT isolation is not enabled, so multi-statement reads "
                + "run without a shared snapshot. ALTER DATABASE ... SET ALLOW_SNAPSHOT_ISOLATION ON enables it.",
                connectionName);
        return capable;
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
        var sql = definition.GetEffectiveDialect() switch
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
