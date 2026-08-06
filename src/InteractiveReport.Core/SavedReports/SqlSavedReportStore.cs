using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;
using InteractiveReport.Core.Composition;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;
using SqlKata;
using SqlKata.Compilers;

namespace InteractiveReport.Core.SavedReports;

/// <summary>
/// SqlKata-backed saved-report store. Works against any supported dialect, so the table
/// can live in a local SQLite file (the zero-config default) or in the same database as
/// the report data. Values are stored cross-dialect-uniform: timestamps as ISO-8601 UTC
/// text (sortable), the global flag as 0/1.
/// </summary>
public sealed partial class SqlSavedReportStore : ISavedReportStore
{
    private const int TimeoutSeconds = 30;
    private static readonly IReadOnlyDictionary<string, object?> NoParams = new Dictionary<string, object?>();

    private readonly Func<SavedReportStoreConfig> _config;
    private readonly IReportConnectionFactory _connections;
    private readonly SemaphoreSlim _createLock = new(1, 1);
    private readonly HashSet<StoreTarget> _createdTargets = [];

    public SqlSavedReportStore(Func<SavedReportStoreConfig> config, IReportConnectionFactory connections)
    {
        _config = config;
        _connections = connections;
    }

    private static SavedReportStoreConfig Validated(SavedReportStoreConfig cfg)
    {
        if (!TableNamePattern().IsMatch(cfg.TableName))
            throw new InvalidOperationException($"Saved-report table name '{cfg.TableName}' is not a plain identifier.");
        return cfg;
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex TableNamePattern();

    public async Task<SavedReport?> Get(string id, CancellationToken ct = default)
    {
        var rows = await Select(q => q.Where("ID", id), ct);
        return rows.SingleOrDefault();
    }

    public Task<IReadOnlyList<SavedReport>> ListVisible(string reportName, string? identity, CancellationToken ct = default)
        => Select(q =>
        {
            q.Where("REPORT_NAME", reportName);
            if (identity is null)
                q.Where("IS_GLOBAL", 1);
            else
                q.Where(sub => sub.Where("IS_GLOBAL", 1).OrWhere("OWNER", identity));
            return q.OrderByDesc("IS_GLOBAL").OrderBy("TITLE");
        }, ct);

    public Task<IReadOnlyList<SavedReport>> ListAll(CancellationToken ct = default)
        => Select(q => q.OrderBy("REPORT_NAME").OrderBy("TITLE"), ct);

    public async Task Create(SavedReport report, CancellationToken ct = default)
    {
        report.ModifiedUtc = DateTime.UtcNow;
        await Execute(config => new Query(config.TableName).AsInsert(ToRow(report)), ct);
    }

    public async Task<bool> Update(SavedReport report, CancellationToken ct = default)
    {
        report.ModifiedUtc = DateTime.UtcNow;
        var row = ToRow(report);
        row.Remove("ID");
        return await Execute(
            config => new Query(config.TableName).Where("ID", report.Id).AsUpdate(row),
            ct) == 1;
    }

    public async Task<bool> Delete(string id, CancellationToken ct = default)
        => await Execute(
            config => new Query(config.TableName).Where("ID", id).AsDelete(),
            ct) == 1;

    // --- plumbing ------------------------------------------------------------

    private static Dictionary<string, object> ToRow(SavedReport r) => new()
    {
        ["ID"] = r.Id,
        ["REPORT_NAME"] = r.ReportName,
        ["TITLE"] = r.Title,
        ["OWNER"] = r.Owner,
        ["IS_GLOBAL"] = r.IsGlobal ? 1 : 0,
        ["STATE_JSON"] = r.StateJson,
        ["MODIFIED_UTC"] = r.ModifiedUtc.ToString("o", CultureInfo.InvariantCulture),
    };

    private async Task<IReadOnlyList<SavedReport>> Select(Func<Query, Query> shape, CancellationToken ct)
    {
        var cfg = Validated(_config());
        var query = shape(new Query(cfg.TableName)
            .Select("ID", "REPORT_NAME", "TITLE", "OWNER", "IS_GLOBAL", "STATE_JSON", "MODIFIED_UTC"));

        await using var conn = await OpenConnection(cfg, ct);
        var compiled = DialectSupport.GetCompiler(cfg.Dialect).Compile(query);
        await using var cmd = CommandBuilder.Build(conn, compiled, NoParams, TimeoutSeconds, cfg.Dialect);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var result = new List<SavedReport>();
        while (await reader.ReadAsync(ct))
        {
            result.Add(new SavedReport
            {
                Id = reader.GetString(0),
                ReportName = reader.GetString(1),
                Title = reader.GetString(2),
                Owner = reader.GetString(3),
                IsGlobal = Convert.ToBoolean(reader.GetValue(4), CultureInfo.InvariantCulture),
                StateJson = reader.GetString(5),
                ModifiedUtc = DateTime.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            });
        }
        return result;
    }

    private async Task<int> Execute(
        Func<SavedReportStoreConfig, Query> buildQuery,
        CancellationToken ct)
    {
        var cfg = Validated(_config());
        var query = buildQuery(cfg);
        await using var conn = await OpenConnection(cfg, ct);
        var compiled = DialectSupport.GetCompiler(cfg.Dialect).Compile(query);
        await using var cmd = CommandBuilder.Build(conn, compiled, NoParams, TimeoutSeconds, cfg.Dialect);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<DbConnection> OpenConnection(SavedReportStoreConfig cfg, CancellationToken ct)
    {
        var conn = _connections.CreateConnection(cfg.ConnectionName);
        try
        {
            await conn.OpenAsync(ct);
            if (cfg.AutoCreate)
                await EnsureCreated(conn, cfg, ct);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    private async Task EnsureCreated(DbConnection conn, SavedReportStoreConfig cfg, CancellationToken ct)
    {
        var target = new StoreTarget(cfg.ConnectionName, cfg.Dialect, cfg.TableName);
        await _createLock.WaitAsync(ct);
        try
        {
            if (_createdTargets.Contains(target)) return;
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = CreateTableSql(cfg);
            await cmd.ExecuteNonQueryAsync(ct);
            _createdTargets.Add(target);
        }
        finally
        {
            _createLock.Release();
        }
    }

    private static string CreateTableSql(SavedReportStoreConfig cfg) => cfg.Dialect switch
    {
        ReportDialect.Sqlite => $"""
            CREATE TABLE IF NOT EXISTS {cfg.TableName} (
                ID           TEXT PRIMARY KEY,
                REPORT_NAME  TEXT NOT NULL,
                TITLE        TEXT NOT NULL,
                OWNER        TEXT NOT NULL,
                IS_GLOBAL    INTEGER NOT NULL,
                STATE_JSON   TEXT NOT NULL,
                MODIFIED_UTC TEXT NOT NULL
            )
            """,
        ReportDialect.SqlServer => $"""
            IF OBJECT_ID(N'{cfg.TableName}', N'U') IS NULL
            CREATE TABLE {cfg.TableName} (
                ID           NVARCHAR(64) PRIMARY KEY,
                REPORT_NAME  NVARCHAR(200) NOT NULL,
                TITLE        NVARCHAR(200) NOT NULL,
                OWNER        NVARCHAR(400) NOT NULL,
                IS_GLOBAL    INT NOT NULL,
                STATE_JSON   NVARCHAR(MAX) NOT NULL,
                MODIFIED_UTC NVARCHAR(40) NOT NULL
            )
            """,
        ReportDialect.Oracle => $"""
            BEGIN
                EXECUTE IMMEDIATE 'CREATE TABLE {cfg.TableName} (
                    ID           VARCHAR2(64) PRIMARY KEY,
                    REPORT_NAME  VARCHAR2(200) NOT NULL,
                    TITLE        VARCHAR2(200) NOT NULL,
                    OWNER        VARCHAR2(400) NOT NULL,
                    IS_GLOBAL    NUMBER(1) NOT NULL,
                    STATE_JSON   CLOB NOT NULL,
                    MODIFIED_UTC VARCHAR2(40) NOT NULL
                )';
            EXCEPTION WHEN OTHERS THEN
                IF SQLCODE != -955 THEN RAISE; END IF;
            END;
            """,
        // Identifiers are quoted: unquoted names would fold to lowercase and never
        // match the quoted uppercase identifiers SqlKata emits in queries.
        ReportDialect.Postgres => $"""
            CREATE TABLE IF NOT EXISTS "{cfg.TableName}" (
                "ID"           VARCHAR(64) PRIMARY KEY,
                "REPORT_NAME"  VARCHAR(200) NOT NULL,
                "TITLE"        VARCHAR(200) NOT NULL,
                "OWNER"        VARCHAR(400) NOT NULL,
                "IS_GLOBAL"    INT NOT NULL,
                "STATE_JSON"   TEXT NOT NULL,
                "MODIFIED_UTC" VARCHAR(40) NOT NULL
            )
            """,
        _ => throw new ArgumentOutOfRangeException(nameof(cfg), cfg.Dialect, null),
    };

    private readonly record struct StoreTarget(
        string ConnectionName,
        ReportDialect Dialect,
        string TableName);
}
