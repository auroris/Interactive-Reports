using System.Data.Common;
using System.Globalization;
using InteractiveReport.Core.Composition;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.SavedReports;
using Microsoft.Extensions.Logging;
using SqlKata;

namespace InteractiveReport.Core.Authorization;

/// <summary>
/// Persists administrator grants through provider-neutral SQL on the saved-report database
/// connection. The identity is the key, compared ordinally, and optional table creation is
/// serialized per target.
/// </summary>
public sealed class SqlAdministratorStore : IAdministratorStore
{
    private const int TimeoutSeconds = 30;
    private static readonly IReadOnlyDictionary<string, object?> NoParams =
        new Dictionary<string, object?>();

    private readonly Func<AdministratorStoreConfig> _config;
    private readonly IReportConnectionFactory _connections;
    private readonly ILogger<SqlAdministratorStore>? _logger;
    private readonly SemaphoreSlim _createLock = new(1, 1);
    private readonly HashSet<StoreTarget> _createdTargets = [];

    /// <summary>
    /// Initializes the store without SQL diagnostic logging.
    /// </summary>
    /// <param name="config">The administrator-store connection, dialect, and table configuration.</param>
    /// <param name="connections">Creates unopened connections by configured name.</param>
    public SqlAdministratorStore(
        Func<AdministratorStoreConfig> config,
        IReportConnectionFactory connections)
        : this(config, connections, logger: null)
    {
    }

    /// <summary>
    /// Initializes the store with optional SQL diagnostic logging.
    /// </summary>
    /// <param name="config">The administrator-store connection, dialect, and table configuration.</param>
    /// <param name="connections">Creates unopened connections by configured name.</param>
    /// <param name="logger">The host-provided logger that receives diagnostic events; <see langword="null"/> disables logging.</param>
    /// <remarks>The configuration callback is evaluated for each operation so option reloads can redirect the store.</remarks>
    public SqlAdministratorStore(
        Func<AdministratorStoreConfig> config,
        IReportConnectionFactory connections,
        ILogger<SqlAdministratorStore>? logger)
    {
        _config = config;
        _connections = connections;
        _logger = logger;
    }

    /// <summary>
    /// Lists every granted identity.
    /// </summary>
    /// <param name="ct">Cancels connection opening, optional table creation, query execution, and reading.</param>
    /// <returns>Granted identities ordered by the database's own collation; callers order for presentation.</returns>
    public Task<IReadOnlyList<string>> List(CancellationToken ct = default)
        => Select(query => query.OrderBy("IDENTITY_VALUE"), ct);

    /// <summary>
    /// Determines whether an identity holds a grant.
    /// </summary>
    /// <param name="identity">The canonical identity compared ordinally.</param>
    /// <param name="ct">Cancels persistence access.</param>
    /// <returns>Whether a grant row carries exactly this identity.</returns>
    /// <remarks>The database may compare case-insensitively; the ordinal comparison here is authoritative.</remarks>
    public async Task<bool> IsAdministrator(string identity, CancellationToken ct = default)
    {
        var key = Key(identity);
        var rows = await Select(query => query.Where("IDENTITY_VALUE", key), ct);
        return rows.Any(row => string.Equals(row, key, StringComparison.Ordinal));
    }

    /// <summary>
    /// Creates or refreshes a grant, recovering from a concurrent insert race.
    /// </summary>
    /// <param name="identity">The canonical identity to grant.</param>
    /// <param name="ct">Cancels persistence.</param>
    /// <returns>A task that completes after update or insert commits.</returns>
    /// <exception cref="InvalidOperationException">Thrown when a unique-key race occurs but the winning row cannot be updated.</exception>
    public async Task Grant(string identity, CancellationToken ct = default)
    {
        var key = Key(identity);
        var now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        var update = new Dictionary<string, object?> { ["MODIFIED_UTC"] = now };
        var row = new Dictionary<string, object?>
        {
            ["IDENTITY_VALUE"] = key,
            ["MODIFIED_UTC"] = now,
        };

        try
        {
            if (await Execute(config => new Query(config.TableName)
                    .Where("IDENTITY_VALUE", key)
                    .AsUpdate(update), ct) >= 1)
                return;
            await Execute(config => new Query(config.TableName).AsInsert(row), ct);
        }
        catch (DbException ex) when (DbErrorClassifier.IsUniqueViolation(_config().Dialect, ex))
        {
            var updated = await Execute(config => new Query(config.TableName)
                .Where("IDENTITY_VALUE", key)
                .AsUpdate(update), ct);
            if (updated < 1)
                throw new InvalidOperationException(
                    $"Administrator grant '{key}' conflicted but could not be updated.", ex);
        }
    }

    /// <summary>
    /// Deletes a grant when present.
    /// </summary>
    /// <param name="identity">The canonical identity to revoke.</param>
    /// <param name="ct">Cancels persistence.</param>
    /// <returns>A task whose result is <see langword="true"/> when a grant was removed; otherwise, <see langword="false"/>.</returns>
    public async Task<bool> Revoke(string identity, CancellationToken ct = default)
    {
        var key = Key(identity);
        return await Execute(config => new Query(config.TableName)
            .Where("IDENTITY_VALUE", key)
            .AsDelete(), ct) >= 1;
    }

    /// <summary>
    /// Builds and executes an identity query.
    /// </summary>
    /// <param name="shape">Adds filters or ordering to the base projection.</param>
    /// <param name="ct">Cancels connection opening, optional table creation, execution, and reading.</param>
    /// <returns>Identities in query order.</returns>
    /// <remarks>Opens and disposes one connection, command, and reader.</remarks>
    private async Task<IReadOnlyList<string>> Select(Func<Query, Query> shape, CancellationToken ct)
    {
        var config = Validated(_config());
        var query = shape(new Query(config.TableName).Select("IDENTITY_VALUE"));
        await using var connection = await OpenConnection(config, ct);
        var compiled = DialectSupport.GetCompiler(config.Dialect).Compile(query);
        await using var command = CommandBuilder.Build(
            connection, compiled, NoParams, TimeoutSeconds, config.Dialect, _logger);
        try
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            var result = new List<string>();
            while (await reader.ReadAsync(ct)) result.Add(reader.GetString(0));
            return result;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogStoreError(config, ex, "select query");
            throw;
        }
    }

    /// <summary>
    /// Compiles and executes one non-query statement.
    /// </summary>
    /// <param name="build">Builds the SqlKata mutation from validated current configuration.</param>
    /// <param name="ct">Cancels connection opening, optional table creation, and execution.</param>
    /// <returns>The provider's affected-row count.</returns>
    /// <remarks>Opens and disposes one connection and command.</remarks>
    private async Task<int> Execute(Func<AdministratorStoreConfig, Query> build, CancellationToken ct)
    {
        var config = Validated(_config());
        var query = build(config);
        await using var connection = await OpenConnection(config, ct);
        var compiled = DialectSupport.GetCompiler(config.Dialect).Compile(query);
        await using var command = CommandBuilder.Build(
            connection, compiled, NoParams, TimeoutSeconds, config.Dialect, _logger);
        try
        {
            return await command.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogStoreError(config, ex, "execute statement");
            throw;
        }
    }

    /// <summary>
    /// Creates and opens a connection, optionally ensuring the configured table exists.
    /// </summary>
    /// <param name="config">The administrator-store connection, dialect, and table configuration.</param>
    /// <param name="ct">Cancels opening and table creation.</param>
    /// <returns>An open connection owned by the caller.</returns>
    /// <remarks>Disposes the connection before rethrowing when preparation fails.</remarks>
    private async Task<DbConnection> OpenConnection(AdministratorStoreConfig config, CancellationToken ct)
    {
        var connection = _connections.CreateConnection(config.ConnectionName);
        try
        {
            await connection.OpenAsync(ct);
            if (config.Dialect == ReportDialect.Oracle
                && int.TryParse(connection.ServerVersion.Split('.')[0], out var major)
                && major < 12)
            {
                config = config with { Dialect = ReportDialect.Oracle11g };
                _connections.SetDetectedDialect(config.ConnectionName, ReportDialect.Oracle11g);
                _logger?.LogInformation(
                    "Detected Oracle Database server version {ServerVersion} for administrator store on connection '{Connection}'. Enabled Oracle 11g compatibility mode.",
                    connection.ServerVersion,
                    config.ConnectionName);
            }
            if (config.AutoCreate) await EnsureCreated(connection, config, ct);
            return connection;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            await connection.DisposeAsync();
            LogStoreError(config, ex, "connection open");
            throw;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Creates the administrator table once per process and configured store target.
    /// </summary>
    /// <param name="connection">The already-open target connection.</param>
    /// <param name="config">The administrator-store connection, dialect, and table configuration.</param>
    /// <param name="ct">Cancels lock acquisition and DDL execution.</param>
    /// <returns>A task that completes when the target is known to exist.</returns>
    private async Task EnsureCreated(
        DbConnection connection,
        AdministratorStoreConfig config,
        CancellationToken ct)
    {
        var target = new StoreTarget(config.ConnectionName, config.Dialect, config.TableName);
        await _createLock.WaitAsync(ct);
        try
        {
            if (_createdTargets.Contains(target)) return;
            await using var command = connection.CreateCommand();
            command.CommandText = CreateTableSql(config);
            CommandBuilder.Log(command, _logger);
            try
            {
                await command.ExecuteNonQueryAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                LogStoreError(config, ex, "schema initialization");
                throw;
            }
            _createdTargets.Add(target);
        }
        finally
        {
            _createLock.Release();
        }
    }

    private void LogStoreError(AdministratorStoreConfig config, Exception ex, string operation)
    {
        var diagnosis = DbErrorClassifier.Classify(config.Dialect, ex);
        _logger?.LogError(
            ex,
            "Administrator store {Operation} failed on connection '{Connection}' (Dialect: {Dialect}, Table: {Table}, Category: {Category}, Code: {ProviderCode}): {Summary}. Hint: {Hint}",
            operation,
            config.ConnectionName,
            config.Dialect,
            config.TableName,
            diagnosis.Category,
            diagnosis.ProviderCode ?? "none",
            diagnosis.Summary,
            diagnosis.RemediationHint ?? "Check administrator database configuration.");
    }

    /// <summary>
    /// Validates and returns the current administrator-store configuration.
    /// </summary>
    /// <param name="config">The administrator-store connection, dialect, and table configuration.</param>
    /// <returns>The same configuration after table-name validation.</returns>
    private static AdministratorStoreConfig Validated(AdministratorStoreConfig config)
    {
        SavedReportStoreConfig.EnsureValidTableName(config.TableName);
        return config;
    }

    /// <summary>Normalizes an identity into its stored key.</summary>
    private static string Key(string identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        return identity.Trim();
    }

    /// <summary>
    /// Builds idempotent provider-specific administrator-table DDL.
    /// </summary>
    /// <param name="config">The administrator-store connection, dialect, and table configuration.</param>
    /// <returns>A CREATE TABLE statement or block for the configured dialect.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the dialect is unsupported.</exception>
    private static string CreateTableSql(AdministratorStoreConfig config)
        => config.Dialect switch
        {
            ReportDialect.Sqlite => $"""
                CREATE TABLE IF NOT EXISTS {config.TableName} (
                    IDENTITY_VALUE TEXT PRIMARY KEY,
                    MODIFIED_UTC   TEXT NOT NULL
                )
                """,
            ReportDialect.SqlServer => $"""
                IF OBJECT_ID(N'{config.TableName}', N'U') IS NULL
                CREATE TABLE {config.TableName} (
                    IDENTITY_VALUE NVARCHAR(400) PRIMARY KEY,
                    MODIFIED_UTC   NVARCHAR(40) NOT NULL
                )
                """,
            // Quoted so DDL and SqlKata's quoted query identifiers name one object; CHAR semantics
            // because the endpoint validates character counts (see the saved-report store).
            ReportDialect.Oracle or ReportDialect.Oracle11g => $"""
                BEGIN
                    EXECUTE IMMEDIATE 'CREATE TABLE "{config.TableName}" (
                        IDENTITY_VALUE VARCHAR2(400 CHAR) PRIMARY KEY,
                        MODIFIED_UTC   VARCHAR2(40) NOT NULL
                    )';
                EXCEPTION WHEN OTHERS THEN
                    IF SQLCODE != -955 THEN RAISE; END IF;
                END;
                """,
            ReportDialect.Postgres => $"""
                CREATE TABLE IF NOT EXISTS "{config.TableName}" (
                    "IDENTITY_VALUE" VARCHAR(400) PRIMARY KEY,
                    "MODIFIED_UTC"   VARCHAR(40) NOT NULL
                )
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(config), config.Dialect, null),
        };

    /// <summary>Identifies one physical table whose successful creation is cached by this store instance.</summary>
    private sealed record StoreTarget(string ConnectionName, ReportDialect Dialect, string TableName);
}
