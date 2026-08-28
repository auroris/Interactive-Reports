using System.Data.Common;
using System.Globalization;
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
public sealed class SqlSavedReportStore : ISavedReportStore
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
        SavedReportStoreConfig.EnsureValidTableName(cfg.TableName);
        return cfg;
    }

    public async Task<SavedReport?> Get(string id, CancellationToken ct = default)
    {
        var rows = await Select(q => q.Where("ID", id), ct);
        return rows.SingleOrDefault();
    }

    public async Task<IReadOnlyList<SavedReport>> ListVisible(string reportName, string? identity, CancellationToken ct = default)
    {
        // Ownership filters in memory rather than in SQL: database string equality is
        // collation-dependent (case-sensitive on SQLite and Postgres by default),
        // while every authorization decision compares identities OrdinalIgnoreCase
        // (SavedReportAccessPolicy). One report's rows are few; identical semantics
        // beat pushing the OR into the WHERE clause.
        var rows = await Select(
            q => q.Where("REPORT_NAME", reportName)
                .OrderByDesc("IS_PRIMARY").OrderByDesc("IS_GLOBAL").OrderBy("TITLE"),
            ct);
        return rows
            .Where(r => r.IsPrimary
                || r.IsGlobal
                || (identity is not null && string.Equals(r.Owner, identity, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    public Task<IReadOnlyList<SavedReport>> ListAll(CancellationToken ct = default)
        => Select(q => q.OrderBy("REPORT_NAME").OrderBy("TITLE"), ct);

    public async Task Create(SavedReport report, CancellationToken ct = default)
    {
        report.ModifiedUtc = DateTime.UtcNow;
        try
        {
            await Execute(config => new Query(config.TableName).AsInsert(ToRow(report)), ct);
        }
        catch (DbException ex) when (IsTitleUniqueViolation(ex))
        {
            throw new SavedReportTitleConflictException(report.ReportName, report.Title, ex);
        }
    }

    public async Task<bool> Update(SavedReport report, CancellationToken ct = default)
    {
        report.ModifiedUtc = DateTime.UtcNow;
        var row = ToRow(report);
        row.Remove("ID");
        try
        {
            return await Execute(
                config => new Query(config.TableName).Where("ID", report.Id).AsUpdate(row),
                ct) == 1;
        }
        catch (DbException ex) when (IsTitleUniqueViolation(ex))
        {
            throw new SavedReportTitleConflictException(report.ReportName, report.Title, ex);
        }
    }

    public async Task Put(SavedReport report, CancellationToken ct = default)
    {
        var row = ToRow(report);
        var update = new Dictionary<string, object?>(row);
        update.Remove("ID");
        try
        {
            if (await Execute(config => new Query(config.TableName).Where("ID", report.Id).AsUpdate(update), ct) == 1)
                return;
            await Execute(config => new Query(config.TableName).AsInsert(row), ct);
        }
        catch (DbException ex) when (IsTitleUniqueViolation(ex))
        {
            throw new SavedReportTitleConflictException(report.ReportName, report.Title, ex);
        }
        catch (DbException ex) when (DbErrorClassifier.IsUniqueViolation(_config().Dialect, ex))
        {
            // Lost a concurrent first-insert race on the primary key — the row exists
            // now, so the idempotent path is to update it. Any other insert failure
            // (missing table, constraint, permissions) propagates above: reporting
            // success would let the synchronizer mark a missing row as applied.
            var updated = await Execute(
                config => new Query(config.TableName).Where("ID", report.Id).AsUpdate(update),
                ct);
            if (updated != 1)
                throw new InvalidOperationException(
                    $"Saved report '{report.Id}': the insert reported a conflict but the follow-up update matched {updated} rows.",
                    ex);
        }
    }

    public async Task<bool> Delete(string id, CancellationToken ct = default)
        => await Execute(
            config => new Query(config.TableName).Where("ID", id).AsDelete(),
            ct) == 1;

    // --- plumbing ------------------------------------------------------------

    private static string OriginText(SavedReportOrigin origin)
        => origin == SavedReportOrigin.Configured ? "configured" : "user";

    private static SavedReportOrigin OriginFrom(string text)
        => string.Equals(text, "configured", StringComparison.OrdinalIgnoreCase)
            ? SavedReportOrigin.Configured
            : SavedReportOrigin.User;

    private static Dictionary<string, object?> ToRow(SavedReport r) => new()
    {
        ["ID"] = r.Id,
        ["REPORT_NAME"] = r.ReportName,
        ["TITLE"] = r.Title,
        ["TITLE_KEY"] = TitleKey(r.Title),
        ["OWNER"] = r.Owner,
        ["IS_GLOBAL"] = r.IsGlobal ? 1 : 0,
        ["IS_PRIMARY"] = r.IsPrimary ? 1 : 0,
        ["STATE_JSON"] = r.StateJson,
        ["MODIFIED_UTC"] = r.ModifiedUtc.ToString("o", CultureInfo.InvariantCulture),
        ["ORIGIN"] = OriginText(r.Origin),
    };

    /// <summary>
    /// Normalized title-uniqueness key, computed in code so every dialect and
    /// collation compares identically — the same trim+casefold the endpoint layer's
    /// OrdinalIgnoreCase pre-check uses.
    /// </summary>
    internal static string TitleKey(string title) => title.Trim().ToUpperInvariant();

    internal static string TitleIndexName(string tableName) => tableName + "_TITLE_UX";

    private bool IsTitleUniqueViolation(DbException ex)
    {
        var cfg = _config();
        return DbErrorClassifier.IsUniqueViolation(cfg.Dialect, ex)
            && (ex.Message.Contains(TitleIndexName(cfg.TableName), StringComparison.OrdinalIgnoreCase)
                // SQLite reports the violated COLUMNS, not the index name.
                || ex.Message.Contains("TITLE_KEY", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<IReadOnlyList<SavedReport>> Select(Func<Query, Query> shape, CancellationToken ct)
    {
        var cfg = Validated(_config());
        var query = shape(new Query(cfg.TableName)
            .Select("ID", "REPORT_NAME", "TITLE", "OWNER", "IS_GLOBAL", "IS_PRIMARY", "STATE_JSON", "MODIFIED_UTC", "ORIGIN"));

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
                Owner = reader.IsDBNull(3) ? null : reader.GetString(3),
                IsGlobal = Convert.ToBoolean(reader.GetValue(4), CultureInfo.InvariantCulture),
                IsPrimary = Convert.ToBoolean(reader.GetValue(5), CultureInfo.InvariantCulture),
                StateJson = reader.GetString(6),
                ModifiedUtc = DateTime.Parse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                Origin = OriginFrom(reader.GetString(8)),
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
            if (cfg.Dialect == ReportDialect.Sqlite)
            {
                await AddSqliteColumnIfMissing(cmd, cfg, "IS_PRIMARY", "INTEGER NOT NULL DEFAULT 0", ct);
                await AddSqliteColumnIfMissing(cmd, cfg, "TITLE_KEY", "TEXT NULL", ct);
            }
            else
            {
                cmd.CommandText = AddPrimaryColumnSql(cfg);
                await cmd.ExecuteNonQueryAsync(ct);
                cmd.CommandText = AddTitleKeyColumnSql(cfg);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await BackfillTitleKeys(conn, cfg, ct);
            await CreateTitleIndex(cmd, cfg, ct);
            _createdTargets.Add(target);
        }
        finally
        {
            _createLock.Release();
        }
    }

    private static string CreateTableSql(SavedReportStoreConfig cfg) => cfg.Dialect switch
    {
        // ID is 80 wide: configured-document ids are 68 chars ("cfg_" + SHA-256 hex).
        // OWNER is nullable: configured rows have no owner.
        ReportDialect.Sqlite => $"""
            CREATE TABLE IF NOT EXISTS {cfg.TableName} (
                ID           TEXT PRIMARY KEY,
                REPORT_NAME  TEXT NOT NULL,
                TITLE        TEXT NOT NULL,
                TITLE_KEY    TEXT NULL,
                OWNER        TEXT NULL,
                IS_GLOBAL    INTEGER NOT NULL,
                IS_PRIMARY   INTEGER NOT NULL DEFAULT 0,
                STATE_JSON   TEXT NOT NULL,
                MODIFIED_UTC TEXT NOT NULL,
                ORIGIN       TEXT NOT NULL DEFAULT 'user'
            )
            """,
        ReportDialect.SqlServer => $"""
            IF OBJECT_ID(N'{cfg.TableName}', N'U') IS NULL
            CREATE TABLE {cfg.TableName} (
                ID           NVARCHAR(80) PRIMARY KEY,
                REPORT_NAME  NVARCHAR(200) NOT NULL,
                TITLE        NVARCHAR(200) NOT NULL,
                TITLE_KEY    NVARCHAR(400) NULL,
                OWNER        NVARCHAR(400) NULL,
                IS_GLOBAL    INT NOT NULL,
                IS_PRIMARY   INT NOT NULL DEFAULT 0,
                STATE_JSON   NVARCHAR(MAX) NOT NULL,
                MODIFIED_UTC NVARCHAR(40) NOT NULL,
                ORIGIN       NVARCHAR(20) NOT NULL DEFAULT 'user'
            )
            """,
        ReportDialect.Oracle => $"""
            BEGIN
                EXECUTE IMMEDIATE 'CREATE TABLE {cfg.TableName} (
                    ID           VARCHAR2(80) PRIMARY KEY,
                    REPORT_NAME  VARCHAR2(200) NOT NULL,
                    TITLE        VARCHAR2(200) NOT NULL,
                    TITLE_KEY    VARCHAR2(400 CHAR) NULL,
                    OWNER        VARCHAR2(400) NULL,
                    IS_GLOBAL    NUMBER(1) NOT NULL,
                    IS_PRIMARY   NUMBER(1) DEFAULT 0 NOT NULL,
                    STATE_JSON   CLOB NOT NULL,
                    MODIFIED_UTC VARCHAR2(40) NOT NULL,
                    ORIGIN       VARCHAR2(20) DEFAULT ''user'' NOT NULL
                )';
            EXCEPTION WHEN OTHERS THEN
                IF SQLCODE != -955 THEN RAISE; END IF;
            END;
            """,
        // Identifiers are quoted: unquoted names would fold to lowercase and never
        // match the quoted uppercase identifiers SqlKata emits in queries.
        ReportDialect.Postgres => $"""
            CREATE TABLE IF NOT EXISTS "{cfg.TableName}" (
                "ID"           VARCHAR(80) PRIMARY KEY,
                "REPORT_NAME"  VARCHAR(200) NOT NULL,
                "TITLE"        VARCHAR(200) NOT NULL,
                "TITLE_KEY"    VARCHAR(400) NULL,
                "OWNER"        VARCHAR(400) NULL,
                "IS_GLOBAL"    INT NOT NULL,
                "IS_PRIMARY"   INT NOT NULL DEFAULT 0,
                "STATE_JSON"   TEXT NOT NULL,
                "MODIFIED_UTC" VARCHAR(40) NOT NULL,
                "ORIGIN"       VARCHAR(20) NOT NULL DEFAULT 'user'
            )
            """,
        _ => throw new ArgumentOutOfRangeException(nameof(cfg), cfg.Dialect, null),
    };

    private static async Task AddSqliteColumnIfMissing(
        DbCommand cmd,
        SavedReportStoreConfig cfg,
        string column,
        string definitionSql,
        CancellationToken ct)
    {
        cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{cfg.TableName}') WHERE name = '{column}'";
        if (Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) == 0)
        {
            cmd.CommandText = $"ALTER TABLE {cfg.TableName} ADD COLUMN {column} {definitionSql}";
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>
    /// Auto-created stores from earlier versions already have a table, so CREATE IF
    /// NOT EXISTS cannot add the new publication flag. Each dialect gets an
    /// idempotent, in-place upgrade; externally managed stores (AutoCreate=false)
    /// remain the host application's responsibility.
    /// </summary>
    private static string AddPrimaryColumnSql(SavedReportStoreConfig cfg) => cfg.Dialect switch
    {
        ReportDialect.Sqlite => throw new InvalidOperationException("SQLite primary-column upgrades are inspected before ALTER TABLE."),
        ReportDialect.SqlServer => $"""
            IF COL_LENGTH(N'{cfg.TableName}', N'IS_PRIMARY') IS NULL
                ALTER TABLE {cfg.TableName} ADD IS_PRIMARY INT NOT NULL DEFAULT 0
            """,
        ReportDialect.Oracle => $"""
            BEGIN
                EXECUTE IMMEDIATE 'ALTER TABLE {cfg.TableName} ADD (IS_PRIMARY NUMBER(1) DEFAULT 0 NOT NULL)';
            EXCEPTION WHEN OTHERS THEN
                IF SQLCODE != -1430 THEN RAISE; END IF;
            END;
            """,
        ReportDialect.Postgres => $"""
            ALTER TABLE "{cfg.TableName}" ADD COLUMN IF NOT EXISTS "IS_PRIMARY" INT NOT NULL DEFAULT 0
            """,
        _ => throw new ArgumentOutOfRangeException(nameof(cfg), cfg.Dialect, null),
    };

    /// <summary>
    /// Title-uniqueness key upgrade for pre-existing tables — same idempotent pattern
    /// as the primary flag. Nullable on purpose: legacy rows backfill in code
    /// (BackfillTitleKeys) so the normalization is exactly TitleKey's, never an
    /// approximation through each database's UPPER().
    /// </summary>
    private static string AddTitleKeyColumnSql(SavedReportStoreConfig cfg) => cfg.Dialect switch
    {
        ReportDialect.Sqlite => throw new InvalidOperationException("SQLite column upgrades are inspected before ALTER TABLE."),
        ReportDialect.SqlServer => $"""
            IF COL_LENGTH(N'{cfg.TableName}', N'TITLE_KEY') IS NULL
                ALTER TABLE {cfg.TableName} ADD TITLE_KEY NVARCHAR(400) NULL
            """,
        ReportDialect.Oracle => $"""
            BEGIN
                EXECUTE IMMEDIATE 'ALTER TABLE {cfg.TableName} ADD (TITLE_KEY VARCHAR2(400 CHAR) NULL)';
            EXCEPTION WHEN OTHERS THEN
                IF SQLCODE != -1430 THEN RAISE; END IF;
            END;
            """,
        ReportDialect.Postgres => $"""
            ALTER TABLE "{cfg.TableName}" ADD COLUMN IF NOT EXISTS "TITLE_KEY" VARCHAR(400) NULL
            """,
        _ => throw new ArgumentOutOfRangeException(nameof(cfg), cfg.Dialect, null),
    };

    /// <summary>Computes TITLE_KEY for rows written before the column existed.</summary>
    private static async Task BackfillTitleKeys(DbConnection conn, SavedReportStoreConfig cfg, CancellationToken ct)
    {
        var compiler = DialectSupport.GetCompiler(cfg.Dialect);
        var pending = new List<(string Id, string Title)>();
        var select = compiler.Compile(new Query(cfg.TableName).Select("ID", "TITLE").WhereNull("TITLE_KEY"));
        await using (var cmd = CommandBuilder.Build(conn, select, NoParams, TimeoutSeconds, cfg.Dialect))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                pending.Add((reader.GetString(0), reader.GetString(1)));
        }

        foreach (var (id, title) in pending)
        {
            var update = compiler.Compile(new Query(cfg.TableName)
                .Where("ID", id)
                .AsUpdate(new Dictionary<string, object?> { ["TITLE_KEY"] = TitleKey(title) }));
            await using var cmd = CommandBuilder.Build(conn, update, NoParams, TimeoutSeconds, cfg.Dialect);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>
    /// One unique index over user-origin rows makes the endpoint layer's title
    /// uniqueness guarantee atomic. Configured rows stay deliberately outside it: a
    /// checked-in document may shadow an existing user title (the listing dedupes,
    /// configured wins), and synchronization must never fail on that collision.
    /// </summary>
    private static async Task CreateTitleIndex(DbCommand cmd, SavedReportStoreConfig cfg, CancellationToken ct)
    {
        cmd.CommandText = CreateTitleIndexSql(cfg);
        try
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (DbException ex)
        {
            throw new InvalidOperationException(
                $"Could not create the saved-report title uniqueness index on '{cfg.TableName}'. "
                + "If the table predates this version, duplicate user-saved titles within one report "
                + "must be renamed or removed first.",
                ex);
        }
    }

    private static string CreateTitleIndexSql(SavedReportStoreConfig cfg)
    {
        var index = TitleIndexName(cfg.TableName);
        return cfg.Dialect switch
        {
            ReportDialect.Sqlite => $"""
                CREATE UNIQUE INDEX IF NOT EXISTS {index}
                ON {cfg.TableName} (REPORT_NAME, TITLE_KEY) WHERE ORIGIN = 'user'
                """,
            // Filtered-index DML needs the standard ANSI SET options; SqlClient's
            // defaults satisfy them (legacy tooling writing this table may not).
            ReportDialect.SqlServer => $"""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'{index}' AND object_id = OBJECT_ID(N'{cfg.TableName}'))
                    CREATE UNIQUE INDEX {index}
                    ON {cfg.TableName} (REPORT_NAME, TITLE_KEY) WHERE ORIGIN = 'user'
                """,
            // Oracle has no partial indexes; the CASE projections index user rows
            // only (rows where every keyed expression is NULL are not indexed).
            // -955: name already used; -1408: column list already indexed.
            ReportDialect.Oracle => $"""
                BEGIN
                    EXECUTE IMMEDIATE 'CREATE UNIQUE INDEX {index} ON {cfg.TableName}
                        (CASE WHEN ORIGIN = ''user'' THEN REPORT_NAME END,
                         CASE WHEN ORIGIN = ''user'' THEN TITLE_KEY END)';
                EXCEPTION WHEN OTHERS THEN
                    IF SQLCODE NOT IN (-955, -1408) THEN RAISE; END IF;
                END;
                """,
            ReportDialect.Postgres => $"""
                CREATE UNIQUE INDEX IF NOT EXISTS "{index}"
                ON "{cfg.TableName}" ("REPORT_NAME", "TITLE_KEY") WHERE "ORIGIN" = 'user'
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(cfg), cfg.Dialect, null),
        };
    }

    private readonly record struct StoreTarget(
        string ConnectionName,
        ReportDialect Dialect,
        string TableName);
}
