using System.Data.Common;
using System.Globalization;
using InteractiveReport.Core.Composition;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;
using Microsoft.Extensions.Logging;
using SqlKata;
using SqlKata.Compilers;

namespace InteractiveReport.Core.SavedReports;

/// <summary>
/// SqlKata-backed saved-report store. Works against any supported dialect, so the table
/// can live in an explicitly configured SQLite file or in the same database as the
/// report data. Values are stored cross-dialect-uniform: timestamps as ISO-8601 UTC
/// text (sortable), the global flag as 0/1.
/// </summary>
public sealed class SqlSavedReportStore : ISavedReportStore
{
    private const int TimeoutSeconds = 30;
    private static readonly IReadOnlyDictionary<string, object?> NoParams = new Dictionary<string, object?>();

    private readonly Func<SavedReportStoreConfig> _config;
    private readonly IReportConnectionFactory _connections;
    private readonly ILogger<SqlSavedReportStore>? _logger;
    private readonly SemaphoreSlim _createLock = new(1, 1);
    private readonly HashSet<StoreTarget> _createdTargets = [];

    public SqlSavedReportStore(
        Func<SavedReportStoreConfig> config,
        IReportConnectionFactory connections)
        : this(config, connections, logger: null)
    {
    }

    public SqlSavedReportStore(
        Func<SavedReportStoreConfig> config,
        IReportConnectionFactory connections,
        ILogger<SqlSavedReportStore>? logger)
    {
        _config = config;
        _connections = connections;
        _logger = logger;
    }

    private static SavedReportStoreConfig Validated(SavedReportStoreConfig cfg)
    {
        SavedReportStoreConfig.EnsureValidTableName(cfg.TableName);
        return cfg;
    }

    public async Task<SavedReport?> Get(string id, CancellationToken ct = default)
    {
        var rows = await Select(Validated(_config()), q => q.Where("ID", id), ct);
        return rows.SingleOrDefault();
    }

    private async Task<SavedReport?> Get(
        SavedReportStoreConfig config,
        string id,
        CancellationToken ct)
    {
        var rows = await Select(config, q => q.Where("ID", id), ct);
        return rows.SingleOrDefault();
    }

    public async Task<SavedReportMetadata?> GetMetadata(string id, CancellationToken ct = default)
    {
        var rows = await SelectMetadata(q => q.Where("ID", id), ct);
        return rows.SingleOrDefault();
    }

    public async Task<IReadOnlyList<SavedReport>> ListVisible(string reportName, string? identity, CancellationToken ct = default)
    {
        // Ownership filters in memory rather than in SQL: database string equality is
        // collation-dependent (case-sensitive on SQLite and Postgres by default),
        // while every authorization decision compares identities ordinally
        // (SavedReportAccessPolicy). One report's rows are few; identical semantics
        // beat pushing the OR into the WHERE clause.
        var rows = await Select(
            q => q.Where("REPORT_NAME", reportName)
                .OrderByDesc("IS_PRIMARY").OrderByDesc("IS_GLOBAL").OrderBy("TITLE"),
            ct);
        return rows
            .Where(r => r.IsPrimary
                || r.IsGlobal
                || (identity is not null && string.Equals(r.Owner, identity, StringComparison.Ordinal)))
            .ToList();
    }

    public async Task<IReadOnlyList<SavedReportMetadata>> ListVisibleMetadata(
        string reportName,
        string? identity,
        CancellationToken ct = default)
    {
        var rows = await SelectMetadata(
            q => q.Where("REPORT_NAME", reportName)
                .OrderByDesc("IS_PRIMARY").OrderByDesc("IS_GLOBAL").OrderBy("TITLE"),
            ct);
        return rows
            .Where(r => r.IsPrimary
                || r.IsGlobal
                || (identity is not null && string.Equals(r.Owner, identity, StringComparison.Ordinal)))
            .ToList();
    }

    public async Task<SavedReport?> FindPrimaryDefault(
        string reportName,
        CancellationToken ct = default)
    {
        var rows = await Select(
            q => q.Where("REPORT_NAME", reportName)
                .Where("TITLE_KEY", TitleKey("Default"))
                .Where("IS_PRIMARY", 1),
            ct);
        return rows
            .OrderBy(report => report.Origin == SavedReportOrigin.User ? 0 : 1)
            .ThenByDescending(report => report.ModifiedUtc)
            .FirstOrDefault();
    }

    public async Task<SavedReport?> FindByTitle(
        string reportName,
        string title,
        string? exceptId = null,
        CancellationToken ct = default)
    {
        var rows = await Select(
            q => q.Where("REPORT_NAME", reportName).Where("TITLE_KEY", TitleKey(title)),
            ct);
        return rows
            .Where(report => !string.Equals(report.Id, exceptId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(report => report.Origin == SavedReportOrigin.Configured ? 0 : 1)
            .FirstOrDefault();
    }

    public Task<IReadOnlyList<SavedReport>> ListAll(CancellationToken ct = default)
        => Select(q => q.OrderBy("REPORT_NAME").OrderBy("TITLE"), ct);

    public async Task Create(SavedReport report, CancellationToken ct = default)
    {
        var config = Validated(_config());
        report.ModifiedUtc = DateTime.UtcNow;
        try
        {
            await Execute(config, cfg => new Query(cfg.TableName).AsInsert(ToRow(report)), ct);
        }
        catch (DbException ex) when (IsTitleUniqueViolation(config, ex))
        {
            throw new SavedReportTitleConflictException(report.ReportName, report.Title, ex);
        }
    }

    public async Task<bool> Update(
        SavedReport report,
        SavedReport expected,
        CancellationToken ct = default)
    {
        var config = Validated(_config());
        if (!string.Equals(report.Id, expected.Id, StringComparison.Ordinal))
            throw new ArgumentException(
                "The replacement and expected saved-report snapshots must have the same id.",
                nameof(expected));

        if (!await IsCurrentSnapshot(config, expected, ct)) return false;

        var modifiedUtc = NextModifiedUtc(expected.ModifiedUtc);
        var row = ToRow(report with { ModifiedUtc = modifiedUtc });
        row.Remove("ID");
        try
        {
            var updated = await Execute(
                config,
                cfg => MatchRevision(new Query(cfg.TableName), expected).AsUpdate(row),
                ct) == 1;
            if (updated) report.ModifiedUtc = modifiedUtc;
            return updated;
        }
        catch (DbException ex) when (IsTitleUniqueViolation(config, ex))
        {
            throw new SavedReportTitleConflictException(report.ReportName, report.Title, ex);
        }
    }

    public async Task Put(SavedReport report, CancellationToken ct = default)
    {
        var config = Validated(_config());
        while (true)
        {
            var expected = await Get(config, report.Id, ct);
            if (await Put(config, report, expected, ct)) return;
        }
    }

    public Task<bool> Put(
        SavedReport report,
        SavedReport? expected,
        CancellationToken ct = default)
        => Put(Validated(_config()), report, expected, ct);

    private async Task<bool> Put(
        SavedReportStoreConfig config,
        SavedReport report,
        SavedReport? expected,
        CancellationToken ct)
    {
        if (expected is not null
            && !string.Equals(report.Id, expected.Id, StringComparison.Ordinal))
            throw new ArgumentException(
                "The replacement and expected saved-report snapshots must have the same id.",
                nameof(expected));

        if (expected is not null && !await IsCurrentSnapshot(config, expected, ct)) return false;

        var modifiedUtc = expected is null
            ? report.ModifiedUtc
            : NextReplacementModifiedUtc(expected.ModifiedUtc, report.ModifiedUtc);
        var row = ToRow(report with { ModifiedUtc = modifiedUtc });
        try
        {
            bool applied;
            if (expected is null)
            {
                await Execute(config, cfg => new Query(cfg.TableName).AsInsert(row), ct);
                applied = true;
            }
            else
            {
                row.Remove("ID");
                applied = await Execute(
                    config,
                    cfg => MatchRevision(new Query(cfg.TableName), expected).AsUpdate(row),
                    ct) == 1;
            }

            if (applied) report.ModifiedUtc = modifiedUtc;
            return applied;
        }
        catch (DbException ex) when (DbErrorClassifier.IsUniqueViolation(config.Dialect, ex))
        {
            // When the expected-absent insert lost its id race, re-reading is the
            // portable way to distinguish it even if the provider reports another
            // unique index first. The caller must reconsider the replacement from
            // that new snapshot. Other title conflicts keep their stable exception.
            if (expected is null && await Get(config, report.Id, ct) is not null) return false;
            if (IsTitleUniqueViolation(config, ex))
                throw new SavedReportTitleConflictException(report.ReportName, report.Title, ex);
            throw;
        }
    }

    public async Task<bool> Delete(SavedReport expected, CancellationToken ct = default)
    {
        var config = Validated(_config());
        if (!await IsCurrentSnapshot(config, expected, ct)) return false;
        return await Execute(
            config,
            cfg => MatchRevision(new Query(cfg.TableName), expected).AsDelete(),
            ct) == 1;
    }

    public async Task<bool> Delete(string id, CancellationToken ct = default)
        => await Execute(
            config => new Query(config.TableName).Where("ID", id).AsDelete(),
            ct) == 1;

    // --- plumbing ------------------------------------------------------------

    private static string OriginText(SavedReportOrigin origin)
        => origin == SavedReportOrigin.Configured ? "configured" : "user";

    private static DateTime NextModifiedUtc(DateTime current)
    {
        var now = DateTime.UtcNow;
        if (now > current) return now;
        if (current == DateTime.MaxValue)
            throw new InvalidOperationException(
                "A saved report with DateTime.MaxValue cannot receive a later concurrency version.");
        return current.AddTicks(1);
    }

    private static DateTime NextReplacementModifiedUtc(DateTime current, DateTime requested)
    {
        if (requested > current) return requested;
        if (current == DateTime.MaxValue)
            throw new InvalidOperationException(
                "A saved report with DateTime.MaxValue cannot receive a later concurrency version.");
        return current.AddTicks(1);
    }

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
    /// Compares the complete detached snapshot in .NET so database collation can never
    /// equate authorization strings that the application compares ordinally. The
    /// subsequent DML matches the revision, which every store replacement advances,
    /// closing the interval between this coherent read and the write. This also avoids
    /// non-portable CLOB equality for Oracle STATE_JSON.
    /// </summary>
    private async Task<bool> IsCurrentSnapshot(
        SavedReportStoreConfig config,
        SavedReport expected,
        CancellationToken ct)
        => await Get(config, expected.Id, ct) is { } current
            && SameSnapshot(current, expected);

    private static bool SameSnapshot(SavedReport current, SavedReport expected)
        => string.Equals(current.Id, expected.Id, StringComparison.Ordinal)
            && string.Equals(current.ReportName, expected.ReportName, StringComparison.Ordinal)
            && string.Equals(current.Title, expected.Title, StringComparison.Ordinal)
            && string.Equals(current.Owner, expected.Owner, StringComparison.Ordinal)
            && current.IsGlobal == expected.IsGlobal
            && current.IsPrimary == expected.IsPrimary
            && string.Equals(current.StateJson, expected.StateJson, StringComparison.Ordinal)
            && current.ModifiedUtc == expected.ModifiedUtc
            && current.Origin == expected.Origin;

    private static Query MatchRevision(Query query, SavedReport expected)
        => query
            .Where("ID", expected.Id)
            .Where("MODIFIED_UTC", expected.ModifiedUtc.ToString("o", CultureInfo.InvariantCulture));

    /// <summary>
    /// Normalized title-uniqueness key, computed in code so every dialect and
    /// collation compares identically — the same trim+casefold the endpoint layer's
    /// OrdinalIgnoreCase pre-check uses.
    /// </summary>
    internal static string TitleKey(string title) => title.Trim().ToUpperInvariant();

    internal static string TitleIndexName(string tableName) => tableName + "_TITLE_UX";

    private static bool IsTitleUniqueViolation(SavedReportStoreConfig config, DbException ex)
    {
        return DbErrorClassifier.IsUniqueViolation(config.Dialect, ex)
            && (ex.Message.Contains(TitleIndexName(config.TableName), StringComparison.OrdinalIgnoreCase)
                // SQLite reports the violated COLUMNS, not the index name.
                || ex.Message.Contains("TITLE_KEY", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<IReadOnlyList<SavedReport>> Select(Func<Query, Query> shape, CancellationToken ct)
    {
        var config = Validated(_config());
        return await Select(config, shape, ct);
    }

    private async Task<IReadOnlyList<SavedReport>> Select(
        SavedReportStoreConfig config,
        Func<Query, Query> shape,
        CancellationToken ct)
    {
        var query = shape(new Query(config.TableName)
            .Select("ID", "REPORT_NAME", "TITLE", "OWNER", "IS_GLOBAL", "IS_PRIMARY", "STATE_JSON", "MODIFIED_UTC", "ORIGIN"));

        await using var conn = await OpenConnection(config, ct);
        var compiled = DialectSupport.GetCompiler(config.Dialect).Compile(query);
        await using var cmd = CommandBuilder.Build(
            conn, compiled, NoParams, TimeoutSeconds, config.Dialect, _logger);
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

    private async Task<IReadOnlyList<SavedReportMetadata>> SelectMetadata(
        Func<Query, Query> shape,
        CancellationToken ct)
    {
        var cfg = Validated(_config());
        var query = shape(new Query(cfg.TableName)
            .Select("ID", "REPORT_NAME", "TITLE", "OWNER", "IS_GLOBAL", "IS_PRIMARY", "MODIFIED_UTC", "ORIGIN"));

        await using var conn = await OpenConnection(cfg, ct);
        var compiled = DialectSupport.GetCompiler(cfg.Dialect).Compile(query);
        await using var cmd = CommandBuilder.Build(
            conn, compiled, NoParams, TimeoutSeconds, cfg.Dialect, _logger);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var result = new List<SavedReportMetadata>();
        while (await reader.ReadAsync(ct))
        {
            result.Add(new SavedReportMetadata(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                Convert.ToBoolean(reader.GetValue(4), CultureInfo.InvariantCulture),
                Convert.ToBoolean(reader.GetValue(5), CultureInfo.InvariantCulture),
                DateTime.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                OriginFrom(reader.GetString(7))));
        }
        return result;
    }

    private async Task<int> Execute(
        Func<SavedReportStoreConfig, Query> buildQuery,
        CancellationToken ct)
    {
        var config = Validated(_config());
        return await Execute(config, buildQuery, ct);
    }

    private async Task<int> Execute(
        SavedReportStoreConfig config,
        Func<SavedReportStoreConfig, Query> buildQuery,
        CancellationToken ct)
    {
        var query = buildQuery(config);
        await using var conn = await OpenConnection(config, ct);
        var compiled = DialectSupport.GetCompiler(config.Dialect).Compile(query);
        await using var cmd = CommandBuilder.Build(
            conn, compiled, NoParams, TimeoutSeconds, config.Dialect, _logger);
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
            CommandBuilder.Log(cmd, _logger);
            await cmd.ExecuteNonQueryAsync(ct);
            if (cfg.Dialect == ReportDialect.Sqlite)
            {
                await AddSqliteColumnIfMissing(cmd, cfg, "IS_PRIMARY", "INTEGER NOT NULL DEFAULT 0", ct);
                await AddSqliteColumnIfMissing(cmd, cfg, "TITLE_KEY", "TEXT NULL", ct);
            }
            else
            {
                cmd.CommandText = AddPrimaryColumnSql(cfg);
                CommandBuilder.Log(cmd, _logger);
                await cmd.ExecuteNonQueryAsync(ct);
                cmd.CommandText = AddTitleKeyColumnSql(cfg);
                CommandBuilder.Log(cmd, _logger);
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

    private async Task AddSqliteColumnIfMissing(
        DbCommand cmd,
        SavedReportStoreConfig cfg,
        string column,
        string definitionSql,
        CancellationToken ct)
    {
        cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{cfg.TableName}') WHERE name = '{column}'";
        CommandBuilder.Log(cmd, _logger);
        if (Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) == 0)
        {
            cmd.CommandText = $"ALTER TABLE {cfg.TableName} ADD COLUMN {column} {definitionSql}";
            CommandBuilder.Log(cmd, _logger);
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
    private async Task BackfillTitleKeys(DbConnection conn, SavedReportStoreConfig cfg, CancellationToken ct)
    {
        var compiler = DialectSupport.GetCompiler(cfg.Dialect);
        var pending = new List<(string Id, string Title)>();
        var select = compiler.Compile(new Query(cfg.TableName).Select("ID", "TITLE").WhereNull("TITLE_KEY"));
        await using (var cmd = CommandBuilder.Build(
                         conn, select, NoParams, TimeoutSeconds, cfg.Dialect, _logger))
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
            await using var cmd = CommandBuilder.Build(
                conn, update, NoParams, TimeoutSeconds, cfg.Dialect, _logger);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>
    /// One unique index over user-origin rows makes the endpoint layer's title
    /// uniqueness guarantee atomic. Configured rows stay deliberately outside it: a
    /// checked-in document may shadow an existing user title (the listing dedupes,
    /// configured wins), and synchronization must never fail on that collision.
    /// </summary>
    private async Task CreateTitleIndex(DbCommand cmd, SavedReportStoreConfig cfg, CancellationToken ct)
    {
        cmd.CommandText = CreateTitleIndexSql(cfg);
        try
        {
            CommandBuilder.Log(cmd, _logger);
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
