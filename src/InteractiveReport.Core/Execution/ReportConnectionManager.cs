using System.Data;
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
            await ApplySessionTimeZone(connection, definition, ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Opens the exact consistency scope requested by the definition. A configured
    /// guarantee is either established or the request fails; there is no implicit
    /// downgrade. Oracle's READ ONLY transaction is issued directly because ADO.NET
    /// has no read-only isolation level and mapping it to Serializable loses the
    /// provider's more precise, writer-friendly semantics.
    /// </summary>
    public async Task<ReportReadScope> BeginReadScope(
        DbConnection connection,
        ReportDefinition definition,
        CancellationToken ct)
    {
        if (definition.Consistency == ReportConsistency.None)
            return ReportReadScope.None;

        if (definition.Consistency != ReportConsistency.Snapshot)
            throw new InvalidOperationException(
                $"Report '{definition.Name}': unsupported consistency strategy '{definition.Consistency}'.");

        var dialect = definition.GetEffectiveDialect();
        if (dialect == ReportDialect.Oracle)
        {
            // ODP.NET auto-commits commands executed outside an explicit local
            // transaction. Start one first so SET TRANSACTION remains in force for
            // every cursor opened by this report scope. READ COMMITTED is only the
            // provider API's bootstrap mode; the first SQL statement changes the
            // Oracle transaction itself to READ ONLY.
            var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            var scope = ReportReadScope.FromTransaction(transaction, logger);
            try
            {
                await ExecuteControlStatement(
                    connection,
                    definition,
                    "SET TRANSACTION READ ONLY",
                    ct,
                    transaction);
                return scope;
            }
            catch
            {
                await scope.DisposeAsync();
                throw;
            }
        }

        if (dialect == ReportDialect.SqlServer
            && !await SqlServerSnapshotEnabled(connection, definition, ct))
            throw new InvalidOperationException(
                $"Report '{definition.Name}' requests snapshot consistency, but SQL Server "
                + "ALLOW_SNAPSHOT_ISOLATION is disabled for this database. Enable it or configure consistency 'none'.");

        var isolation = dialect switch
        {
            ReportDialect.SqlServer => IsolationLevel.Snapshot,
            ReportDialect.Postgres => IsolationLevel.RepeatableRead,
            ReportDialect.Sqlite => IsolationLevel.Serializable,
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null),
        };

        var readTransaction = await connection.BeginTransactionAsync(isolation, ct);
        return ReportReadScope.FromTransaction(readTransaction, logger);
    }

    private async Task<bool> SqlServerSnapshotEnabled(
        DbConnection connection,
        ReportDefinition definition,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT snapshot_isolation_state FROM sys.databases WHERE database_id = DB_ID()";
        command.CommandTimeout = definition.CommandTimeoutSeconds;
        logger?.LogDebug("Executing report SQL:\n{Sql}", command.CommandText);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct) ?? 0) == 1;
    }

    private async Task ExecuteControlStatement(
        DbConnection connection,
        ReportDefinition definition,
        string sql,
        CancellationToken ct,
        DbTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = definition.CommandTimeoutSeconds;
        command.Transaction = transaction;
        logger?.LogDebug("Executing report SQL:\n{Sql}", command.CommandText);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Only Oracle and Postgres expose a session timezone. The configured value has the
    /// same trust level as the report SQL. These statements do not accept parameters, so
    /// the value is escaped before it is placed in the statement.
    /// </summary>
    private async Task ApplySessionTimeZone(
        DbConnection connection,
        ReportDefinition definition,
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
        await ExecuteControlStatement(connection, definition, sql, ct);
    }
}
