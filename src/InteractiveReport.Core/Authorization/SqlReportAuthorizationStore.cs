using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using InteractiveReport.Core.Composition;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.SavedReports;
using Microsoft.Extensions.Logging;
using SqlKata;

namespace InteractiveReport.Core.Authorization;

/// <summary>
/// Persists administrator grants, report restrictions, and report-user grants through provider-neutral SQL
/// on the saved-report database connection. Entry ids are content-addressed, report names compare
/// case-insensitively, identities compare ordinally, and optional table creation is serialized per target.
/// </summary>
public sealed class SqlReportAuthorizationStore : IReportAuthorizationStore
{
    private const int TimeoutSeconds = 30;
    private static readonly IReadOnlyDictionary<string, object?> NoParams =
        new Dictionary<string, object?>();

    private readonly Func<ReportAuthorizationStoreConfig> _config;
    private readonly IReportConnectionFactory _connections;
    private readonly ILogger<SqlReportAuthorizationStore>? _logger;
    private readonly SemaphoreSlim _createLock = new(1, 1);
    private readonly HashSet<StoreTarget> _createdTargets = [];

    /// <summary>
    /// Initializes the store without SQL diagnostic logging.
    /// </summary>
    /// <param name="config">The authorization-store connection, dialect, and table configuration.</param>
    /// <param name="connections">Creates unopened connections by configured name.</param>
    public SqlReportAuthorizationStore(
        Func<ReportAuthorizationStoreConfig> config,
        IReportConnectionFactory connections)
        : this(config, connections, logger: null)
    {
    }

    /// <summary>
    /// Initializes the store with optional SQL diagnostic logging.
    /// </summary>
    /// <param name="config">The authorization-store connection, dialect, and table configuration.</param>
    /// <param name="connections">Creates unopened connections by configured name.</param>
    /// <param name="logger">The host-provided logger that receives diagnostic events; <see langword="null"/> disables logging.</param>
    /// <remarks>The configuration callback is evaluated for each operation so option reloads can redirect the store.</remarks>
    public SqlReportAuthorizationStore(
        Func<ReportAuthorizationStoreConfig> config,
        IReportConnectionFactory connections,
        ILogger<SqlReportAuthorizationStore>? logger)
    {
        _config = config;
        _connections = connections;
        _logger = logger;
    }

    /// <summary>
    /// Lists every persisted authorization entry, including restricted-report settings.
    /// </summary>
    /// <param name="ct">Cancels connection opening, optional table creation, query execution, and reading.</param>
    /// <returns>Detached entries ordered by kind, normalized report key, then identity key.</returns>
    public Task<IReadOnlyList<ReportAuthorizationEntry>> ListAll(CancellationToken ct = default)
        => Select(query => query
            .OrderBy("ENTRY_KIND")
            .OrderBy("REPORT_KEY")
            .OrderBy("IDENTITY_KEY"), ct);

    /// <summary>
    /// Loads a database-administrator grant by identity.
    /// </summary>
    /// <param name="identity">The optional canonical caller identity compared ordinally.</param>
    /// <param name="ct">Cancels persistence access.</param>
    /// <returns>Whether any database administrator is configured and whether this identity is granted.</returns>
    public async Task<DatabaseAdministratorAccess> GetAdministratorAccess(
        string? identity,
        CancellationToken ct = default)
    {
        var rows = await Select(query => query
            .Where("ENTRY_KIND", KindText(ReportAuthorizationEntryKind.Administrator)), ct);
        return new DatabaseAdministratorAccess(
            rows.Count != 0,
            identity is not null && rows.Any(row =>
                string.Equals(row.Identity, identity, StringComparison.Ordinal)));
    }

    /// <summary>
    /// Loads a report-user grant by report and identity.
    /// </summary>
    /// <param name="reportName">The configured report name compared through its case-insensitive key.</param>
    /// <param name="identity">The optional canonical caller identity compared ordinally.</param>
    /// <param name="ct">Cancels persistence access.</param>
    /// <returns>Whether the report is database-restricted and whether this identity has a database grant.</returns>
    public async Task<DatabaseReportAccess> GetReportAccess(
        string reportName,
        string? identity,
        CancellationToken ct = default)
    {
        var ids = new List<string>
        {
            EntryId(ReportAuthorizationEntryKind.ReportRestriction, reportName, null),
        };
        if (identity is not null)
            ids.Add(EntryId(ReportAuthorizationEntryKind.ReportUser, reportName, identity));
        var rows = await Select(query => query.WhereIn("ID", ids), ct);
        return new DatabaseReportAccess(
            rows.Any(row => row.Kind == ReportAuthorizationEntryKind.ReportRestriction),
            identity is not null && rows.Any(row =>
                row.Kind == ReportAuthorizationEntryKind.ReportUser
                && string.Equals(row.Identity, identity, StringComparison.Ordinal)));
    }

    /// <summary>
    /// Creates or refreshes a database administrator grant.
    /// </summary>
    /// <param name="identity">The canonical identity to grant.</param>
    /// <param name="ct">Cancels persistence.</param>
    /// <returns>A task that completes after the grant is inserted or updated.</returns>
    public Task GrantAdministrator(string identity, CancellationToken ct = default)
        => Put(ReportAuthorizationEntryKind.Administrator, reportName: null, identity, ct);

    /// <summary>
    /// Deletes a database administrator grant when present.
    /// </summary>
    /// <param name="identity">The canonical identity to revoke.</param>
    /// <param name="ct">Cancels persistence.</param>
    /// <returns>A task whose result is <see langword="true"/> when an administrator grant was removed; otherwise, <see langword="false"/>.</returns>
    public Task<bool> RevokeAdministrator(string identity, CancellationToken ct = default)
        => Delete(EntryId(ReportAuthorizationEntryKind.Administrator, null, identity), ct);

    /// <summary>
    /// Creates or removes the database restriction marker for one report.
    /// </summary>
    /// <param name="reportName">The canonical configured report name.</param>
    /// <param name="restricted">True to upsert a marker; false to delete it.</param>
    /// <param name="ct">Cancels persistence.</param>
    /// <returns>A task that completes after the marker reaches the requested state.</returns>
    public async Task SetReportRestricted(
        string reportName,
        bool restricted,
        CancellationToken ct = default)
    {
        if (restricted)
            await Put(ReportAuthorizationEntryKind.ReportRestriction, reportName, identity: null, ct);
        else
            await Delete(EntryId(ReportAuthorizationEntryKind.ReportRestriction, reportName, null), ct);
    }

    /// <summary>
    /// Creates or refreshes a database user grant for one report.
    /// </summary>
    /// <param name="reportName">The canonical configured report name.</param>
    /// <param name="identity">The canonical identity to grant.</param>
    /// <param name="ct">Cancels persistence.</param>
    /// <returns>A task that completes after the grant is inserted or updated.</returns>
    public Task GrantReportUser(
        string reportName,
        string identity,
        CancellationToken ct = default)
        => Put(ReportAuthorizationEntryKind.ReportUser, reportName, identity, ct);

    /// <summary>
    /// Deletes a database user grant for one report when present.
    /// </summary>
    /// <param name="reportName">The canonical configured report name.</param>
    /// <param name="identity">The canonical identity to revoke.</param>
    /// <param name="ct">Cancels persistence.</param>
    /// <returns>A task whose result is <see langword="true"/> when a report-user grant was removed; otherwise, <see langword="false"/>.</returns>
    public Task<bool> RevokeReportUser(
        string reportName,
        string identity,
        CancellationToken ct = default)
        => Delete(EntryId(ReportAuthorizationEntryKind.ReportUser, reportName, identity), ct);

    /// <summary>
    /// Upserts one deterministic authorization row, recovering from a concurrent insert race.
    /// </summary>
    /// <param name="kind">The authorization-entry kind to persist.</param>
    /// <param name="reportName">The report name required by report-scoped kinds; otherwise null.</param>
    /// <param name="identity">The identity required by administrator/user kinds; otherwise null.</param>
    /// <param name="ct">Cancels persistence.</param>
    /// <returns>A task that completes after update or insert commits.</returns>
    /// <exception cref="InvalidOperationException">Thrown when a unique-key race occurs but the winning row cannot be updated.</exception>
    private async Task Put(
        ReportAuthorizationEntryKind kind,
        string? reportName,
        string? identity,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        var row = new Dictionary<string, object?>
        {
            ["ID"] = EntryId(kind, reportName, identity),
            ["ENTRY_KIND"] = KindText(kind),
            ["REPORT_NAME"] = reportName?.Trim(),
            ["REPORT_KEY"] = reportName is null ? null : ReportKey(reportName),
            ["IDENTITY_VALUE"] = identity?.Trim(),
            ["IDENTITY_KEY"] = identity is null ? null : IdentityKey(identity),
            ["MODIFIED_UTC"] = now,
        };
        var update = new Dictionary<string, object?>(row);
        update.Remove("ID");

        try
        {
            if (await Execute(config => new Query(config.TableName)
                    .Where("ID", row["ID"])
                    .AsUpdate(update), ct) == 1)
                return;
            await Execute(config => new Query(config.TableName).AsInsert(row), ct);
        }
        catch (DbException ex) when (DbErrorClassifier.IsUniqueViolation(_config().Dialect, ex))
        {
            var updated = await Execute(config => new Query(config.TableName)
                .Where("ID", row["ID"])
                .AsUpdate(update), ct);
            if (updated != 1)
                throw new InvalidOperationException(
                    $"Authorization entry '{row["ID"]}' conflicted but could not be updated.", ex);
        }
    }

    /// <summary>
    /// Deletes one deterministic authorization row by id.
    /// </summary>
    /// <param name="id">The exact content-addressed entry id.</param>
    /// <param name="ct">Cancels persistence.</param>
    /// <returns>A task whose result is <see langword="true"/> when the requested row was deleted; otherwise, <see langword="false"/>.</returns>
    private async Task<bool> Delete(string id, CancellationToken ct)
        => await Execute(config => new Query(config.TableName).Where("ID", id).AsDelete(), ct) == 1;

    /// <summary>
    /// Builds the authorization query for the supplied report and identity filters.
    /// </summary>
    /// <param name="shape">Adds filters, limits, or ordering to the base authorization-table projection.</param>
    /// <param name="ct">Cancels connection opening, optional table creation, execution, and reading.</param>
    /// <returns>Detached entries in query order.</returns>
    /// <remarks>Opens and disposes one connection, command, and reader.</remarks>
    private async Task<IReadOnlyList<ReportAuthorizationEntry>> Select(
        Func<Query, Query> shape,
        CancellationToken ct)
    {
        var config = Validated(_config());
        var query = shape(new Query(config.TableName).Select(
            "ID", "ENTRY_KIND", "REPORT_NAME", "IDENTITY_VALUE", "MODIFIED_UTC"));
        await using var connection = await OpenConnection(config, ct);
        var compiled = DialectSupport.GetCompiler(config.Dialect).Compile(query);
        await using var command = CommandBuilder.Build(
            connection, compiled, NoParams, TimeoutSeconds, config.Dialect, _logger);
        await using var reader = await command.ExecuteReaderAsync(ct);

        var result = new List<ReportAuthorizationEntry>();
        while (await reader.ReadAsync(ct))
        {
            result.Add(new ReportAuthorizationEntry(
                reader.GetString(0),
                KindFrom(reader.GetString(1)),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                DateTime.Parse(
                    reader.GetString(4),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind)));
        }
        return result;
    }

    /// <summary>
    /// Compiles and executes one non-query authorization-table statement.
    /// </summary>
    /// <param name="build">Builds the SQLKata mutation from validated current configuration.</param>
    /// <param name="ct">Cancels connection opening, optional table creation, and execution.</param>
    /// <returns>The provider's affected-row count.</returns>
    /// <remarks>Opens and disposes one connection and command.</remarks>
    private async Task<int> Execute(
        Func<ReportAuthorizationStoreConfig, Query> build,
        CancellationToken ct)
    {
        var config = Validated(_config());
        var query = build(config);
        await using var connection = await OpenConnection(config, ct);
        var compiled = DialectSupport.GetCompiler(config.Dialect).Compile(query);
        await using var command = CommandBuilder.Build(
            connection, compiled, NoParams, TimeoutSeconds, config.Dialect, _logger);
        return await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Creates and opens a connection, optionally ensuring the configured table exists.
    /// </summary>
    /// <param name="config">The authorization-store connection, dialect, and table configuration.</param>
    /// <param name="ct">Cancels opening and table creation.</param>
    /// <returns>An open connection owned by the caller.</returns>
    /// <remarks>Disposes the connection before rethrowing when preparation fails.</remarks>
    private async Task<DbConnection> OpenConnection(
        ReportAuthorizationStoreConfig config,
        CancellationToken ct)
    {
        var connection = _connections.CreateConnection(config.ConnectionName);
        try
        {
            await connection.OpenAsync(ct);
            if (config.AutoCreate) await EnsureCreated(connection, config, ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Creates the authorization table once per process and configured store target.
    /// </summary>
    /// <param name="connection">The already-open target connection.</param>
    /// <param name="config">The authorization-store connection, dialect, and table configuration.</param>
    /// <param name="ct">Cancels lock acquisition and DDL execution.</param>
    /// <returns>A task that completes when the target is known to exist.</returns>
    /// <remarks>Serializes DDL, executes it at most once per target in this store instance, and records successful targets in memory.</remarks>
    private async Task EnsureCreated(
        DbConnection connection,
        ReportAuthorizationStoreConfig config,
        CancellationToken ct)
    {
        var target = new StoreTarget(
            config.ConnectionName, config.Dialect, config.TableName);
        await _createLock.WaitAsync(ct);
        try
        {
            if (_createdTargets.Contains(target)) return;
            await using var command = connection.CreateCommand();
            command.CommandText = CreateTableSql(config);
            CommandBuilder.Log(command, _logger);
            await command.ExecuteNonQueryAsync(ct);
            _createdTargets.Add(target);
        }
        finally
        {
            _createLock.Release();
        }
    }

    /// <summary>
    /// Validates and returns the current report-authorization store configuration.
    /// </summary>
    /// <param name="config">The authorization-store connection, dialect, and table configuration.</param>
    /// <returns>The report authorization store config.</returns>
    private static ReportAuthorizationStoreConfig Validated(ReportAuthorizationStoreConfig config)
    {
        SavedReportStoreConfig.EnsureValidTableName(config.TableName);
        return config;
    }

    /// <summary>
    /// Builds idempotent provider-specific authorization-table DDL.
    /// </summary>
    /// <param name="config">The authorization-store connection, dialect, and table configuration.</param>
    /// <returns>A CREATE TABLE statement or block for the configured dialect.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the dialect is unsupported.</exception>
    private static string CreateTableSql(ReportAuthorizationStoreConfig config)
        => config.Dialect switch
        {
            ReportDialect.Sqlite => $"""
                CREATE TABLE IF NOT EXISTS {config.TableName} (
                    ID             TEXT PRIMARY KEY,
                    ENTRY_KIND     TEXT NOT NULL,
                    REPORT_NAME    TEXT NULL,
                    REPORT_KEY     TEXT NULL,
                    IDENTITY_VALUE TEXT NULL,
                    IDENTITY_KEY   TEXT NULL,
                    MODIFIED_UTC   TEXT NOT NULL
                )
                """,
            ReportDialect.SqlServer => $"""
                IF OBJECT_ID(N'{config.TableName}', N'U') IS NULL
                CREATE TABLE {config.TableName} (
                    ID             NVARCHAR(80) PRIMARY KEY,
                    ENTRY_KIND     NVARCHAR(30) NOT NULL,
                    REPORT_NAME    NVARCHAR(200) NULL,
                    REPORT_KEY     NVARCHAR(200) NULL,
                    IDENTITY_VALUE NVARCHAR(400) NULL,
                    IDENTITY_KEY   NVARCHAR(400) NULL,
                    MODIFIED_UTC   NVARCHAR(40) NOT NULL
                )
                """,
            // Quoted so DDL and SqlKata's quoted query identifiers name one object; CHAR semantics
            // because the endpoint validates character counts (see the saved-report store).
            ReportDialect.Oracle => $"""
                BEGIN
                    EXECUTE IMMEDIATE 'CREATE TABLE "{config.TableName}" (
                        ID             VARCHAR2(80) PRIMARY KEY,
                        ENTRY_KIND     VARCHAR2(30) NOT NULL,
                        REPORT_NAME    VARCHAR2(200 CHAR) NULL,
                        REPORT_KEY     VARCHAR2(200 CHAR) NULL,
                        IDENTITY_VALUE VARCHAR2(400 CHAR) NULL,
                        IDENTITY_KEY   VARCHAR2(400 CHAR) NULL,
                        MODIFIED_UTC   VARCHAR2(40) NOT NULL
                    )';
                EXCEPTION WHEN OTHERS THEN
                    IF SQLCODE != -955 THEN RAISE; END IF;
                END;
                """,
            ReportDialect.Postgres => $"""
                CREATE TABLE IF NOT EXISTS "{config.TableName}" (
                    "ID"             VARCHAR(80) PRIMARY KEY,
                    "ENTRY_KIND"     VARCHAR(30) NOT NULL,
                    "REPORT_NAME"    VARCHAR(200) NULL,
                    "REPORT_KEY"     VARCHAR(200) NULL,
                    "IDENTITY_VALUE" VARCHAR(400) NULL,
                    "IDENTITY_KEY"   VARCHAR(400) NULL,
                    "MODIFIED_UTC"   VARCHAR(40) NOT NULL
                )
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(config), config.Dialect, null),
        };

    /// <summary>
    /// Builds the stable identifier for one authorization entry.
    /// </summary>
    /// <param name="kind">The authorization-entry kind included in the identifier.</param>
    /// <param name="reportName">The optional report name component.</param>
    /// <param name="identity">The optional identity component.</param>
    /// <returns>An <c>auth_</c>-prefixed lowercase SHA-256 identity over normalized components.</returns>
    private static string EntryId(
        ReportAuthorizationEntryKind kind,
        string? reportName,
        string? identity)
    {
        var source = $"{KindText(kind)}\n{ReportKey(reportName ?? string.Empty)}\n{IdentityKey(identity ?? string.Empty)}";
        return "auth_" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
    }

    /// <summary>
    /// Normalizes a report name into its comparison key.
    /// </summary>
    /// <param name="value">The report name to normalize for persistence comparisons.</param>
    /// <returns>The normalized key used to identify the report.</returns>
    private static string ReportKey(string value) => value.Trim().ToUpperInvariant();
    /// <summary>
    /// Normalizes an identity into its comparison key.
    /// </summary>
    /// <param name="value">The identity to normalize for persistence comparisons.</param>
    /// <returns>The normalized key used for identity comparisons.</returns>
    private static string IdentityKey(string value) => value.Trim();

    /// <summary>
    /// Serializes an authorization-entry kind to its persistence token.
    /// </summary>
    /// <param name="kind">The authorization-entry kind to serialize.</param>
    /// <returns>The persisted authorization-entry kind token.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="kind"/> is unsupported.</exception>
    private static string KindText(ReportAuthorizationEntryKind kind) => kind switch
    {
        ReportAuthorizationEntryKind.Administrator => "administrator",
        ReportAuthorizationEntryKind.ReportRestriction => "reportRestriction",
        ReportAuthorizationEntryKind.ReportUser => "reportUser",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>
    /// Parses the persisted authorization kind token into the protocol enum.
    /// </summary>
    /// <param name="value">The persisted authorization-kind token to parse.</param>
    /// <returns>The report authorization entry kind.</returns>
    /// <exception cref="InvalidOperationException">Thrown when persistence contains an unknown token.</exception>
    private static ReportAuthorizationEntryKind KindFrom(string value) => value switch
    {
        "administrator" => ReportAuthorizationEntryKind.Administrator,
        "reportRestriction" => ReportAuthorizationEntryKind.ReportRestriction,
        "reportUser" => ReportAuthorizationEntryKind.ReportUser,
        _ => throw new InvalidOperationException(
            $"Authorization table contains unknown entry kind '{value}'."),
    };

    /// <summary>Identifies one physical table whose successful creation is cached by this store instance.</summary>
    private sealed record StoreTarget(
        string ConnectionName,
        ReportDialect Dialect,
        string TableName);
}
