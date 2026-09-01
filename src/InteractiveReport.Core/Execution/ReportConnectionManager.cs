using System.Data;
using System.Data.Common;
using InteractiveReport.Core.Model;
using Microsoft.Extensions.Logging;

namespace InteractiveReport.Core.Execution;

/// <summary>
/// Owns connection creation and per-session configuration required by report
/// execution. Callers receive an open, prepared connection and remain responsible for
/// disposing it.
/// </summary>
internal sealed class ReportConnectionManager(
    IReportConnectionFactory connections,
    ILogger? logger = null)
{
    /// <summary>
    /// Creates, opens, and applies the configured session time zone to a report connection.
    /// </summary>
    /// <param name="definition">The resolved definition supplying the connection name, dialect, timeout, and optional time zone.</param>
    /// <param name="ct">Signals that the operation should be canceled.</param>
    /// <returns>A task whose result is the open database connection.</returns>
    /// <remarks>Opens a new connection and disposes it if opening or session configuration fails. The caller owns a successful result.</remarks>
    public async Task<DbConnection> Open(ReportDefinition definition, CancellationToken ct)
    {
        var connection = connections.CreateConnection(definition.Connection);
        try
        {
            await connection.OpenAsync(ct);
            if (definition.GetEffectiveDialect() == ReportDialect.Oracle)
            {
                if (int.TryParse(connection.ServerVersion.Split('.')[0], out var major) && major < 12)
                {
                    definition.Dialect = ReportDialect.Oracle11g;
                    connections.SetDetectedDialect(definition.Connection, ReportDialect.Oracle11g);
                    logger?.LogInformation(
                        "Detected Oracle Database server version {ServerVersion} on connection '{Connection}'. Enabled Oracle 11g compatibility mode (ROWNUM pagination and sequence-backed persistence).",
                        connection.ServerVersion,
                        definition.Connection);
                }
            }
            await ApplySessionTimeZone(connection, definition, ct);
            return connection;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            await connection.DisposeAsync();
            var diagnosis = DbErrorClassifier.Classify(definition.GetEffectiveDialect(), ex);
            logger?.LogError(
                ex,
                "Failed to open database connection '{Connection}' for report '{Report}' (Dialect: {Dialect}, Category: {Category}, Code: {ProviderCode}): {Summary}. Hint: {Hint}",
                definition.Connection,
                definition.Name,
                definition.GetEffectiveDialect(),
                diagnosis.Category,
                diagnosis.ProviderCode ?? "none",
                diagnosis.Summary,
                diagnosis.RemediationHint ?? "Check connection settings.");
            throw;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Opens the exact consistency scope requested by the definition. A configured
    /// guarantee is either established or the request fails; there is no implicit downgrade. Oracle's READ
    /// ONLY transaction is issued directly because ADO.NET has no read-only isolation level and mapping it
    /// to Serializable loses the provider's more precise, writer-friendly semantics.
    /// </summary>
    /// <param name="connection">The open report connection on which every scoped query will execute.</param>
    /// <param name="definition">The definition supplying consistency, dialect, name, and command timeout.</param>
    /// <param name="ct">Signals that the operation should be canceled.</param>
    /// <returns>A task containing a no-op scope for independent statements or an owned transaction scope for snapshot reads.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when snapshot consistency reaches an unknown dialect.</exception>
    /// <exception cref="InvalidOperationException">Thrown for an unsupported consistency strategy or disabled SQL Server snapshot isolation.</exception>
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
        if (dialect is ReportDialect.Oracle or ReportDialect.Oracle11g)
        {
            // Provider constraint: ODP.NET auto-commits commands executed outside an explicit
            // local transaction. Start one first so SET TRANSACTION remains in force for every
            // cursor opened by this report scope. READ COMMITTED is only the provider API's
            // bootstrap mode; the first SQL statement changes the Oracle transaction itself to
            // READ ONLY.
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

        var readTransaction = dialect == ReportDialect.Sqlite
            ? await BeginDeferredSqliteTransaction(connection, isolation, ct)
            : await connection.BeginTransactionAsync(isolation, ct);
        return ReportReadScope.FromTransaction(readTransaction, logger);
    }

    /// <summary>
    /// Begins the SQLite read scope as a deferred transaction. Microsoft.Data.Sqlite maps every
    /// non-deferred isolation level to <c>BEGIN IMMEDIATE</c>, which takes the database's single
    /// write reservation: concurrent report reads would serialize and a co-located saved-report
    /// write would wait for the whole report. A deferred <c>BEGIN</c> still pins one read snapshot
    /// from the first statement on, which is all a read scope needs. The deferred overload is
    /// provider-specific, so it is reached reflectively; a wrapper connection without it keeps the
    /// portable call.
    /// </summary>
    private static async Task<DbTransaction> BeginDeferredSqliteTransaction(
        DbConnection connection,
        IsolationLevel isolation,
        CancellationToken ct)
    {
        var deferred = connection.GetType().GetMethod(
            "BeginTransaction",
            [typeof(IsolationLevel), typeof(bool)]);
        if (deferred is null || !typeof(DbTransaction).IsAssignableFrom(deferred.ReturnType))
            return await connection.BeginTransactionAsync(isolation, ct);

        ct.ThrowIfCancellationRequested();
        return (DbTransaction)deferred.Invoke(connection, [isolation, true])!;
    }

    /// <summary>
    /// Reads whether SQL Server snapshot isolation is enabled for the current database.
    /// </summary>
    /// <param name="connection">The open SQL Server connection to inspect.</param>
    /// <param name="definition">The definition supplying the command timeout.</param>
    /// <param name="ct">Signals that the operation should be canceled.</param>
    /// <returns>A task whose result is <see langword="true"/> when snapshot isolation is enabled; otherwise, <see langword="false"/>.</returns>
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
        try
        {
            return Convert.ToInt32(await command.ExecuteScalarAsync(ct) ?? 0) == 1;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var diagnosis = DbErrorClassifier.Classify(definition.GetEffectiveDialect(), ex);
            logger?.LogError(
                ex,
                "Snapshot isolation check failed for report '{Report}' on connection '{Connection}' (Dialect: {Dialect}, Category: {Category}, Code: {ProviderCode}): {Summary}. Hint: {Hint}",
                definition.Name,
                definition.Connection,
                definition.GetEffectiveDialect(),
                diagnosis.Category,
                diagnosis.ProviderCode ?? "none",
                diagnosis.Summary,
                diagnosis.RemediationHint ?? "Verify that the database user has VIEW SERVER STATE or access to sys.databases.");
            throw;
        }
    }

    /// <summary>
    /// Executes one session or transaction control statement with the report command timeout.
    /// </summary>
    /// <param name="connection">The open connection receiving the statement.</param>
    /// <param name="definition">The definition supplying the command timeout.</param>
    /// <param name="sql">The trusted control statement to execute.</param>
    /// <param name="ct">Signals that the operation should be canceled.</param>
    /// <param name="transaction">The transaction in which to execute, or <see langword="null"/> for session-level control.</param>
    /// <returns>A task that completes after the provider accepts the statement.</returns>
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
        try
        {
            await command.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var diagnosis = DbErrorClassifier.Classify(definition.GetEffectiveDialect(), ex);
            logger?.LogError(
                ex,
                "Database control statement '{Sql}' failed for report '{Report}' on connection '{Connection}' (Dialect: {Dialect}, Category: {Category}, Code: {ProviderCode}): {Summary}. Hint: {Hint}",
                sql,
                definition.Name,
                definition.Connection,
                definition.GetEffectiveDialect(),
                diagnosis.Category,
                diagnosis.ProviderCode ?? "none",
                diagnosis.Summary,
                diagnosis.RemediationHint ?? "Check database permissions and configuration.");
            throw;
        }
    }

    /// <summary>
    /// Applies the configured session time zone for Oracle or PostgreSQL. The configured value has
    /// the same trust level as the report SQL. These statements do not accept parameters, so the value is
    /// escaped before it is placed in the statement.
    /// </summary>
    /// <param name="connection">The newly opened report connection.</param>
    /// <param name="definition">The definition supplying the optional time zone, dialect, and timeout.</param>
    /// <param name="ct">Signals that the operation should be canceled.</param>
    /// <returns>A task that completes after configuration, or immediately when no supported time-zone statement is needed.</returns>
    private async Task ApplySessionTimeZone(
        DbConnection connection,
        ReportDefinition definition,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(definition.TimeZone)) return;

        var timeZone = definition.TimeZone.Trim().Replace("'", "''");
        var sql = definition.GetEffectiveDialect() switch
        {
            ReportDialect.Oracle or ReportDialect.Oracle11g => $"ALTER SESSION SET TIME_ZONE = '{timeZone}'",
            ReportDialect.Postgres => $"SET TIME ZONE '{timeZone}'",
            _ => null,
        };
        if (sql is null) return;
        await ExecuteControlStatement(connection, definition, sql, ct);
    }
}
