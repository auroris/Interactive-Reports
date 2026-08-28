using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using InteractiveReport.Core.Composition;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.SavedReports;
using SqlKata;

namespace InteractiveReport.Core.Authorization;

/// <summary>Portable authorization store on the saved-report database connection.</summary>
public sealed class SqlReportAuthorizationStore : IReportAuthorizationStore
{
    private const int TimeoutSeconds = 30;
    private static readonly IReadOnlyDictionary<string, object?> NoParams =
        new Dictionary<string, object?>();

    private readonly Func<ReportAuthorizationStoreConfig> _config;
    private readonly IReportConnectionFactory _connections;
    private readonly SemaphoreSlim _createLock = new(1, 1);
    private readonly HashSet<StoreTarget> _createdTargets = [];

    public SqlReportAuthorizationStore(
        Func<ReportAuthorizationStoreConfig> config,
        IReportConnectionFactory connections)
    {
        _config = config;
        _connections = connections;
    }

    public Task<IReadOnlyList<ReportAuthorizationEntry>> ListAll(CancellationToken ct = default)
        => Select(query => query
            .OrderBy("ENTRY_KIND")
            .OrderBy("REPORT_KEY")
            .OrderBy("IDENTITY_KEY"), ct);

    public async Task<DatabaseAdministratorAccess> GetAdministratorAccess(
        string? identity,
        CancellationToken ct = default)
    {
        var rows = await Select(query => query
            .Where("ENTRY_KIND", KindText(ReportAuthorizationEntryKind.Administrator)), ct);
        return new DatabaseAdministratorAccess(
            rows.Count != 0,
            identity is not null && rows.Any(row =>
                string.Equals(row.Identity, identity, StringComparison.OrdinalIgnoreCase)));
    }

    public async Task<bool> HasAdministrators(CancellationToken ct = default)
        => (await Select(query => query
            .Where("ENTRY_KIND", KindText(ReportAuthorizationEntryKind.Administrator))
            .Limit(1), ct)).Count != 0;

    public async Task<bool> IsAdministrator(string identity, CancellationToken ct = default)
        => (await Select(query => query
            .Where("ID", EntryId(ReportAuthorizationEntryKind.Administrator, null, identity))
            .Limit(1), ct)).Count != 0;

    public async Task<DatabaseReportAccess> GetReportAccess(
        string reportName,
        string? identity,
        CancellationToken ct = default)
    {
        var ids = new[]
        {
            EntryId(ReportAuthorizationEntryKind.ReportRestriction, reportName, null),
            identity is null
                ? null
                : EntryId(ReportAuthorizationEntryKind.ReportUser, reportName, identity),
        }.Where(id => id is not null).ToArray();
        var rows = await Select(query => query.WhereIn("ID", ids), ct);
        return new DatabaseReportAccess(
            rows.Any(row => row.Kind == ReportAuthorizationEntryKind.ReportRestriction),
            identity is not null && rows.Any(row =>
                row.Kind == ReportAuthorizationEntryKind.ReportUser
                && string.Equals(row.Identity, identity, StringComparison.OrdinalIgnoreCase)));
    }

    public Task GrantAdministrator(string identity, CancellationToken ct = default)
        => Put(ReportAuthorizationEntryKind.Administrator, reportName: null, identity, ct);

    public Task<bool> RevokeAdministrator(string identity, CancellationToken ct = default)
        => Delete(EntryId(ReportAuthorizationEntryKind.Administrator, null, identity), ct);

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

    public Task GrantReportUser(
        string reportName,
        string identity,
        CancellationToken ct = default)
        => Put(ReportAuthorizationEntryKind.ReportUser, reportName, identity, ct);

    public Task<bool> RevokeReportUser(
        string reportName,
        string identity,
        CancellationToken ct = default)
        => Delete(EntryId(ReportAuthorizationEntryKind.ReportUser, reportName, identity), ct);

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

    private async Task<bool> Delete(string id, CancellationToken ct)
        => await Execute(config => new Query(config.TableName).Where("ID", id).AsDelete(), ct) == 1;

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
            connection, compiled, NoParams, TimeoutSeconds, config.Dialect);
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

    private async Task<int> Execute(
        Func<ReportAuthorizationStoreConfig, Query> build,
        CancellationToken ct)
    {
        var config = Validated(_config());
        var query = build(config);
        await using var connection = await OpenConnection(config, ct);
        var compiled = DialectSupport.GetCompiler(config.Dialect).Compile(query);
        await using var command = CommandBuilder.Build(
            connection, compiled, NoParams, TimeoutSeconds, config.Dialect);
        return await command.ExecuteNonQueryAsync(ct);
    }

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
            await command.ExecuteNonQueryAsync(ct);
            _createdTargets.Add(target);
        }
        finally
        {
            _createLock.Release();
        }
    }

    private static ReportAuthorizationStoreConfig Validated(ReportAuthorizationStoreConfig config)
    {
        SavedReportStoreConfig.EnsureValidTableName(config.TableName);
        return config;
    }

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
            ReportDialect.Oracle => $"""
                BEGIN
                    EXECUTE IMMEDIATE 'CREATE TABLE {config.TableName} (
                        ID             VARCHAR2(80) PRIMARY KEY,
                        ENTRY_KIND     VARCHAR2(30) NOT NULL,
                        REPORT_NAME    VARCHAR2(200) NULL,
                        REPORT_KEY     VARCHAR2(200) NULL,
                        IDENTITY_VALUE VARCHAR2(400) NULL,
                        IDENTITY_KEY   VARCHAR2(400) NULL,
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

    private static string EntryId(
        ReportAuthorizationEntryKind kind,
        string? reportName,
        string? identity)
    {
        var source = $"{KindText(kind)}\n{ReportKey(reportName ?? string.Empty)}\n{IdentityKey(identity ?? string.Empty)}";
        return "auth_" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
    }

    private static string ReportKey(string value) => value.Trim().ToUpperInvariant();
    private static string IdentityKey(string value) => value.Trim().ToUpperInvariant();

    private static string KindText(ReportAuthorizationEntryKind kind) => kind switch
    {
        ReportAuthorizationEntryKind.Administrator => "administrator",
        ReportAuthorizationEntryKind.ReportRestriction => "reportRestriction",
        ReportAuthorizationEntryKind.ReportUser => "reportUser",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static ReportAuthorizationEntryKind KindFrom(string value) => value switch
    {
        "administrator" => ReportAuthorizationEntryKind.Administrator,
        "reportRestriction" => ReportAuthorizationEntryKind.ReportRestriction,
        "reportUser" => ReportAuthorizationEntryKind.ReportUser,
        _ => throw new InvalidOperationException(
            $"Authorization table contains unknown entry kind '{value}'."),
    };

    private sealed record StoreTarget(
        string ConnectionName,
        ReportDialect Dialect,
        string TableName);
}
