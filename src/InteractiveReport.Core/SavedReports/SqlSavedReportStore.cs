// SQL saved-report persistence entrypoint: maps the store contract to provider-neutral SqlKata
// statements, then compiles them for the configured database dialect. Full detached snapshots
// provide optimistic-concurrency checks, while a revision predicate makes writes atomic. Optional
// schema creation and legacy-column upgrades are serialized per store target within this process.

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
/// Persists user and configured saved reports in a SQL table supported by the report dialect layer.
/// Provider-independent representations keep timestamps as round-trip UTC text, Boolean flags as
/// <c>0</c>/<c>1</c>, and origins as stable text tokens.
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

    /// <summary>
    /// Creates a store that resolves its configuration for each operation.
    /// </summary>
    /// <param name="config">The saved-report store connection, dialect, and table configuration.</param>
    /// <param name="connections">The factory that creates unopened connections by configured name.</param>
    public SqlSavedReportStore(
        Func<SavedReportStoreConfig> config,
        IReportConnectionFactory connections)
        : this(config, connections, logger: null)
    {
    }

    /// <summary>
    /// Creates a store that resolves its configuration for each operation and can log generated commands.
    /// </summary>
    /// <param name="config">The saved-report store connection, dialect, and table configuration.</param>
    /// <param name="connections">The factory that creates unopened connections by configured name.</param>
    /// <param name="logger">The host-provided logger that receives diagnostic events; <see langword="null"/> disables logging.</param>
    public SqlSavedReportStore(
        Func<SavedReportStoreConfig> config,
        IReportConnectionFactory connections,
        ILogger<SqlSavedReportStore>? logger)
    {
        _config = config;
        _connections = connections;
        _logger = logger;
    }

    /// <summary>
    /// Validates the configured physical table identifier before it is interpolated into SQL.
    /// </summary>
    /// <param name="cfg">The saved-report store connection, dialect, and table configuration.</param>
    /// <returns>The same configuration instance after validation.</returns>
    private static SavedReportStoreConfig Validated(SavedReportStoreConfig cfg)
    {
        SavedReportStoreConfig.EnsureValidTableName(cfg.TableName);
        return cfg;
    }

    /// <summary>
    /// Reads one complete saved-report row by its stable identifier.
    /// </summary>
    /// <param name="id">The saved-report identifier to match.</param>
    /// <param name="ct">Signals that the operation should be canceled; defaults to <c>default</c>.</param>
    /// <returns>The matching report, or <see langword="null"/> when no row exists.</returns>
    /// <remarks>Opens and disposes a database connection and executes one query.</remarks>
    public async Task<SavedReport?> Get(string id, CancellationToken ct = default)
    {
        var rows = await Select(Validated(_config()), q => q.Where("ID", id), ct);
        return rows.SingleOrDefault();
    }

    /// <summary>
    /// Reads one complete saved-report row using an already validated store configuration.
    /// </summary>
    /// <param name="config">The saved-report store connection, dialect, and table configuration.</param>
    /// <param name="id">The saved-report identifier to match.</param>
    /// <param name="ct">Signals that the operation should be canceled.</param>
    /// <returns>The matching report, or <see langword="null"/> when no row exists.</returns>
    private async Task<SavedReport?> Get(
        SavedReportStoreConfig config,
        string id,
        CancellationToken ct)
    {
        var rows = await Select(config, q => q.Where("ID", id), ct);
        return rows.SingleOrDefault();
    }

    /// <summary>
    /// Loads saved-report metadata by identifier without reading state JSON.
    /// </summary>
    /// <param name="id">The saved-report identifier to match.</param>
    /// <param name="ct">Signals that the operation should be canceled; defaults to <c>default</c>.</param>
    /// <returns>The matching metadata, or <see langword="null"/> when no row exists.</returns>
    /// <remarks>Does not select or materialize the report's state JSON.</remarks>
    public async Task<SavedReportMetadata?> GetMetadata(string id, CancellationToken ct = default)
    {
        var rows = await SelectMetadata(q => q.Where("ID", id), ct);
        return rows.SingleOrDefault();
    }

    /// <summary>
    /// Lists primary, global, and caller-owned reports for one report definition.
    /// </summary>
    /// <param name="reportName">The configured report name whose definition or saved reports are being addressed.</param>
    /// <param name="identity">The exact owner identity to include; <see langword="null"/> includes no private reports.</param>
    /// <param name="ct">Signals that the operation should be canceled; defaults to <c>default</c>.</param>
    /// <returns>Visible reports ordered with primary and global entries first, then by title.</returns>
    /// <remarks>Ownership is filtered in memory with ordinal equality so database collations cannot change authorization behavior.</remarks>
    public async Task<IReadOnlyList<SavedReport>> ListVisible(string reportName, string? identity, CancellationToken ct = default)
    {
        // Provider constraint: ownership filters in memory rather than in SQL: database string
        // equality is collation-dependent (case-sensitive on SQLite and PostgreSQL by default),
        // while every authorization decision compares identities ordinally
        // (SavedReportAccessPolicy). One report's rows are few; identical semantics beat
        // pushing the OR into the WHERE clause.
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

    /// <summary>
    /// Lists metadata for primary, global, and caller-owned reports without loading state JSON.
    /// </summary>
    /// <param name="reportName">The configured report name whose definition or saved reports are being addressed.</param>
    /// <param name="identity">The exact owner identity to include; <see langword="null"/> includes no private reports.</param>
    /// <param name="ct">Signals that the operation should be canceled; defaults to <c>default</c>.</param>
    /// <returns>Visible metadata ordered with primary and global entries first, then by title.</returns>
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

    /// <summary>
    /// Finds the effective primary report named <c>Default</c> for one report definition.
    /// </summary>
    /// <param name="reportName">The configured report name whose definition or saved reports are being addressed.</param>
    /// <param name="ct">Signals that the operation should be canceled; defaults to <c>default</c>.</param>
    /// <returns>The newest matching user report when present, otherwise the newest configured report, or <see langword="null"/>.</returns>
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

    /// <summary>
    /// Finds a saved report by report name and the store's normalized title key.
    /// </summary>
    /// <param name="reportName">The configured report name whose definition or saved reports are being addressed.</param>
    /// <param name="title">The title to trim and case-fold for comparison.</param>
    /// <param name="exceptId">An identifier to omit, typically the row being renamed; defaults to <see langword="null"/>.</param>
    /// <param name="ct">Signals that the operation should be canceled; defaults to <c>default</c>.</param>
    /// <returns>The configured match in preference to a user match, or <see langword="null"/> when none exists.</returns>
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

    /// <summary>
    /// Lists every saved report without applying caller visibility filters.
    /// </summary>
    /// <param name="ct">Signals that the operation should be canceled; defaults to <c>default</c>.</param>
    /// <returns>All rows ordered by report name and title, without authorization filtering.</returns>
    /// <remarks>Opens and disposes a database connection and loads state JSON for every row.</remarks>
    public Task<IReadOnlyList<SavedReport>> ListAll(CancellationToken ct = default)
        => Select(q => q.OrderBy("REPORT_NAME").OrderBy("TITLE"), ct);

    /// <summary>
    /// Inserts a saved report, assigns its committed timestamp, and translates title conflicts.
    /// </summary>
    /// <param name="report">The new row to insert. Its identifier and title scope must be unique.</param>
    /// <param name="ct">Signals that the operation should be canceled; defaults to <c>default</c>.</param>
    /// <returns>A task that completes after the insert commits.</returns>
    /// <remarks>Sets <paramref name="report"/>'s <c>ModifiedUtc</c> to the current UTC time before executing the insert.</remarks>
    /// <exception cref="SavedReportTitleConflictException">Thrown when another saved report already uses the title in the same visibility scope.</exception>
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

    /// <summary>
    /// Replaces a saved report only when the caller's detached snapshot is still current.
    /// </summary>
    /// <param name="report">The replacement row. Its identifier must equal <paramref name="expected"/>'s identifier.</param>
    /// <param name="expected">The complete previously read snapshot used for optimistic concurrency.</param>
    /// <param name="ct">Signals that the operation should be canceled; defaults to <c>default</c>.</param>
    /// <returns>A task whose result is <see langword="true"/> when the expected snapshot was current and the update committed; otherwise, <see langword="false"/>.</returns>
    /// <remarks>On success, updates the database row and advances <paramref name="report"/>'s <c>ModifiedUtc</c>. A stale snapshot leaves both unchanged.</remarks>
    /// <exception cref="ArgumentException">Thrown when the replacement and expected identifiers differ.</exception>
    /// <exception cref="SavedReportTitleConflictException">Thrown when another saved report already uses the title in the same visibility scope.</exception>
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

    /// <summary>
    /// Repeatedly reads and upserts a saved report until it wins any concurrent revision race.
    /// </summary>
    /// <param name="report">The row to insert or use as the next replacement value.</param>
    /// <param name="ct">Signals that the operation should be canceled; defaults to <c>default</c>.</param>
    /// <returns>A task that completes after an insert or replacement commits.</returns>
    /// <remarks>May retry database reads and writes. On replacement, advances <paramref name="report"/>'s <c>ModifiedUtc</c>.</remarks>
    public async Task Put(SavedReport report, CancellationToken ct = default)
    {
        var config = Validated(_config());
        while (true)
        {
            var expected = await Get(config, report.Id, ct);
            if (await Put(config, report, expected, ct)) return;
        }
    }

    /// <summary>
    /// Attempts one insert or revision-checked replacement against the supplied expected state.
    /// </summary>
    /// <param name="report">The row to insert or use as the replacement value.</param>
    /// <param name="expected">The complete current snapshot, or <see langword="null"/> when the identifier is expected to be absent.</param>
    /// <param name="ct">Signals that the operation should be canceled; defaults to <c>default</c>.</param>
    /// <returns>A task whose result is <see langword="true"/> when the create or replacement committed against the expected snapshot; otherwise, <see langword="false"/>.</returns>
    public Task<bool> Put(
        SavedReport report,
        SavedReport? expected,
        CancellationToken ct = default)
        => Put(Validated(_config()), report, expected, ct);

    /// <summary>
    /// Performs one insert or revision-checked replacement using an already validated configuration.
    /// </summary>
    /// <param name="config">The saved-report store connection, dialect, and table configuration.</param>
    /// <param name="report">The row to insert or use as the replacement value.</param>
    /// <param name="expected">The complete current snapshot, or <see langword="null"/> when the identifier is expected to be absent.</param>
    /// <param name="ct">Signals that the operation should be canceled.</param>
    /// <returns>A task whose result is <see langword="true"/> when the create or replacement committed against the expected snapshot; otherwise, <see langword="false"/>.</returns>
    /// <remarks>Writes the database on success and updates <paramref name="report"/>'s <c>ModifiedUtc</c>. Returns <see langword="false"/> after a revision or identifier race.</remarks>
    /// <exception cref="ArgumentException">Thrown when non-null expected and replacement identifiers differ.</exception>
    /// <exception cref="SavedReportTitleConflictException">Thrown when another saved report already uses the title in the same visibility scope.</exception>
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
            // Concurrency rule: when the expected-absent insert lost its id race, re-reading is the
            // portable way to distinguish it even if the provider reports another unique index
            // first. The caller must reconsider the replacement from that new snapshot. Other
            // title conflicts keep their stable exception.
            if (expected is null && await Get(config, report.Id, ct) is not null) return false;
            if (IsTitleUniqueViolation(config, ex))
                throw new SavedReportTitleConflictException(report.ReportName, report.Title, ex);
            throw;
        }
    }

    /// <summary>
    /// Deletes a saved report only when the complete detached snapshot is still current.
    /// </summary>
    /// <param name="expected">The complete previously read snapshot used for optimistic concurrency.</param>
    /// <param name="ct">Signals that the operation should be canceled; defaults to <c>default</c>.</param>
    /// <returns>A task whose result is <see langword="true"/> when the requested row was deleted; otherwise, <see langword="false"/>.</returns>
    public async Task<bool> Delete(SavedReport expected, CancellationToken ct = default)
    {
        var config = Validated(_config());
        if (!await IsCurrentSnapshot(config, expected, ct)) return false;
        return await Execute(
            config,
            cfg => MatchRevision(new Query(cfg.TableName), expected).AsDelete(),
            ct) == 1;
    }

    /// <summary>
    /// Deletes a saved report by identifier without an optimistic-concurrency check.
    /// </summary>
    /// <param name="id">The saved-report identifier to delete.</param>
    /// <param name="ct">Signals that the operation should be canceled; defaults to <c>default</c>.</param>
    /// <returns>A task whose result is <see langword="true"/> when the requested row was deleted; otherwise, <see langword="false"/>.</returns>
    public async Task<bool> Delete(string id, CancellationToken ct = default)
        => await Execute(
            config => new Query(config.TableName).Where("ID", id).AsDelete(),
            ct) == 1;

    /// <summary>
    /// Encodes a saved-report origin as its stable database token.
    /// </summary>
    /// <param name="origin">The origin to encode.</param>
    /// <returns>The persisted saved-report origin token.</returns>
    private static string OriginText(SavedReportOrigin origin)
        => origin == SavedReportOrigin.Configured ? "configured" : "user";

    /// <summary>
    /// Chooses a modification timestamp strictly newer than the stored revision.
    /// </summary>
    /// <param name="current">The stored concurrency timestamp that the next revision must exceed.</param>
    /// <returns>The current UTC time when it is newer; otherwise, one tick after <paramref name="current"/>.</returns>
    /// <exception cref="InvalidOperationException">Thrown when <paramref name="current"/> is <see cref="DateTime.MaxValue"/>.</exception>
    private static DateTime NextModifiedUtc(DateTime current)
    {
        var now = DateTime.UtcNow;
        if (now > current) return now;
        if (current == DateTime.MaxValue)
            throw new InvalidOperationException(
                "A saved report with DateTime.MaxValue cannot receive a later concurrency version.");
        return current.AddTicks(1);
    }

    /// <summary>
    /// Chooses a replacement timestamp strictly newer than both compared revisions.
    /// </summary>
    /// <param name="current">The stored concurrency timestamp that the replacement must exceed.</param>
    /// <param name="requested">The replacement's requested timestamp.</param>
    /// <returns><paramref name="requested"/> when it is newer; otherwise, one tick after <paramref name="current"/>.</returns>
    /// <exception cref="InvalidOperationException">Thrown when a later value is required but <paramref name="current"/> is <see cref="DateTime.MaxValue"/>.</exception>
    private static DateTime NextReplacementModifiedUtc(DateTime current, DateTime requested)
    {
        if (requested > current) return requested;
        if (current == DateTime.MaxValue)
            throw new InvalidOperationException(
                "A saved report with DateTime.MaxValue cannot receive a later concurrency version.");
        return current.AddTicks(1);
    }

    /// <summary>
    /// Parses the persisted origin token into the protocol enum.
    /// </summary>
    /// <param name="text">The persisted origin token.</param>
    /// <returns><see cref="SavedReportOrigin.Configured"/> for <c>configured</c>, ignoring case; otherwise, <see cref="SavedReportOrigin.User"/>.</returns>
    private static SavedReportOrigin OriginFrom(string text)
        => string.Equals(text, "configured", StringComparison.OrdinalIgnoreCase)
            ? SavedReportOrigin.Configured
            : SavedReportOrigin.User;

    /// <summary>
    /// Maps a saved report to the provider-neutral column/value dictionary used by insert and update commands.
    /// </summary>
    /// <param name="r">The saved report to serialize for a write statement.</param>
    /// <returns>The persistence columns and their values.</returns>
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
    /// Reads the current row and compares the complete detached snapshot in .NET. This preserves ordinal
    /// string semantics across database collations and avoids non-portable Oracle CLOB equality. The later
    /// write still matches the revision to close the interval between this read and the update or delete.
    /// </summary>
    /// <param name="config">The saved-report store connection, dialect, and table configuration.</param>
    /// <param name="expected">The complete snapshot expected to be current.</param>
    /// <param name="ct">Signals that the operation should be canceled.</param>
    /// <returns>A task whose result is <see langword="true"/> when the supplied revision matches the current snapshot; otherwise, <see langword="false"/>.</returns>
    private async Task<bool> IsCurrentSnapshot(
        SavedReportStoreConfig config,
        SavedReport expected,
        CancellationToken ct)
        => await Get(config, expected.Id, ct) is { } current
            && SameSnapshot(current, expected);

    /// <summary>
    /// Compares every persisted saved-report field using the store's exact identity rules.
    /// </summary>
    /// <param name="current">The authoritative row read from the database.</param>
    /// <param name="expected">The caller's detached snapshot.</param>
    /// <returns><see langword="true"/> when all fields match with ordinal string equality; otherwise, <see langword="false"/>.</returns>
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

    /// <summary>
    /// Adds the identifier and exact modification timestamp predicates used for an atomic revision match.
    /// </summary>
    /// <param name="query">The update or delete query to constrain.</param>
    /// <param name="expected">The expected snapshot whose identifier and revision must match.</param>
    /// <returns>The same mutable SqlKata query with both predicates appended.</returns>
    private static Query MatchRevision(Query query, SavedReport expected)
        => query
            .Where("ID", expected.Id)
            .Where("MODIFIED_UTC", expected.ModifiedUtc.ToString("o", CultureInfo.InvariantCulture));

    /// <summary>
    /// Produces the normalized title-uniqueness key in application code so every dialect and collation uses
    /// the same trim-and-case-fold rule as the endpoint's ordinal, case-insensitive pre-check.
    /// </summary>
    /// <param name="title">The authored title to normalize.</param>
    /// <returns>The normalized key used for title uniqueness.</returns>
    internal static string TitleKey(string title) => title.Trim().ToUpperInvariant();

    /// <summary>
    /// Builds the dialect-safe name of the title-uniqueness index.
    /// </summary>
    /// <param name="tableName">The validated physical table name used for saved-report persistence.</param>
    /// <returns>The dialect-safe name of the title uniqueness index.</returns>
    internal static string TitleIndexName(string tableName) => tableName + "_TITLE_UX";

    /// <summary>
    /// Determines whether a database exception represents a saved-report title conflict.
    /// </summary>
    /// <param name="config">The saved-report store connection, dialect, and table configuration.</param>
    /// <param name="ex">The exception whose provider-specific details are being classified or logged.</param>
    /// <returns><see langword="true"/> when the exception reports a title-uniqueness violation; otherwise, <see langword="false"/>.</returns>
    private static bool IsTitleUniqueViolation(SavedReportStoreConfig config, DbException ex)
    {
        return DbErrorClassifier.IsUniqueViolation(config.Dialect, ex)
            && (ex.Message.Contains(TitleIndexName(config.TableName), StringComparison.OrdinalIgnoreCase)
                // Provider constraint: SQLite reports the violated COLUMNS, not the index name.
                || ex.Message.Contains("TITLE_KEY", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Builds and executes a full-row saved-report query using the current configuration.
    /// </summary>
    /// <param name="shape">A callback that adds predicates and ordering to the base table query.</param>
    /// <param name="ct">Signals that the operation should be canceled.</param>
    /// <returns>The materialized reports in database result order.</returns>
    private async Task<IReadOnlyList<SavedReport>> Select(Func<Query, Query> shape, CancellationToken ct)
    {
        var config = Validated(_config());
        return await Select(config, shape, ct);
    }

    /// <summary>
    /// Builds, compiles, and executes a full-row saved-report query using a validated configuration.
    /// </summary>
    /// <param name="config">The saved-report store connection, dialect, and table configuration.</param>
    /// <param name="shape">A callback that adds predicates and ordering to the base table query.</param>
    /// <param name="ct">Signals that the operation should be canceled.</param>
    /// <returns>The materialized reports in database result order.</returns>
    /// <remarks>Opens and disposes a connection, command, and reader. Auto-create may also execute schema DDL.</remarks>
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

    /// <summary>
    /// Projects only the saved-report columns required by metadata operations.
    /// </summary>
    /// <param name="shape">A callback that adds predicates and ordering to the metadata query.</param>
    /// <param name="ct">Signals that the operation should be canceled.</param>
    /// <returns>The materialized metadata rows in database result order.</returns>
    /// <remarks>Opens and disposes a connection, command, and reader; the query deliberately omits state JSON.</remarks>
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

    /// <summary>
    /// Builds and executes a non-query statement using the current store configuration.
    /// </summary>
    /// <param name="buildQuery">The callback that builds the provider-neutral query.</param>
    /// <param name="ct">Signals that the operation should be canceled.</param>
    /// <returns>The provider-reported number of affected rows.</returns>
    private async Task<int> Execute(
        Func<SavedReportStoreConfig, Query> buildQuery,
        CancellationToken ct)
    {
        var config = Validated(_config());
        return await Execute(config, buildQuery, ct);
    }

    /// <summary>
    /// Compiles and executes a non-query statement using a validated store configuration.
    /// </summary>
    /// <param name="config">The saved-report store connection, dialect, and table configuration.</param>
    /// <param name="buildQuery">The callback that builds the provider-neutral query.</param>
    /// <param name="ct">Signals that the operation should be canceled.</param>
    /// <returns>The provider-reported number of affected rows.</returns>
    /// <remarks>Opens and disposes the connection and command. Auto-create may execute schema DDL first.</remarks>
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

    /// <summary>
    /// Creates and opens a configured connection, optionally ensuring the saved-report schema exists.
    /// </summary>
    /// <param name="cfg">The validated connection, dialect, table, and auto-create settings.</param>
    /// <param name="ct">Signals that the operation should be canceled.</param>
    /// <returns>A task whose result is the open database connection.</returns>
    /// <remarks>The caller owns the returned connection. A connection that fails to open or initialize is disposed here.</remarks>
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

    /// <summary>
    /// Creates or upgrades an auto-managed saved-report table once per process and store target.
    /// </summary>
    /// <param name="conn">The open connection on which to run DDL and legacy-row backfills.</param>
    /// <param name="cfg">The validated dialect and physical table settings.</param>
    /// <param name="ct">Signals that the operation should be canceled.</param>
    /// <returns>A task that completes after the table, columns, backfill, and unique index are ready.</returns>
    /// <remarks>Serializes initialization through <c>_createLock</c>, executes database writes, and caches successful targets in <c>_createdTargets</c>.</remarks>
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

    /// <summary>
    /// Builds idempotent table-creation SQL for the configured database dialect.
    /// </summary>
    /// <param name="cfg">The validated dialect and physical table settings.</param>
    /// <returns>A dialect-specific statement or block that creates the saved-report table when absent.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="cfg"/> contains an unsupported dialect.</exception>
    private static string CreateTableSql(SavedReportStoreConfig cfg) => cfg.Dialect switch
    {
        // ID is 80 wide: configured-document ids are 68 chars ("cfg_" + SHA-256 hex). OWNER is
        // nullable: configured rows have no owner.
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
        // Invariant: identifiers are quoted: unquoted names would fold to lowercase and never
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

    /// <summary>
    /// Inspects a SQLite table and adds one legacy-upgrade column when it is absent.
    /// </summary>
    /// <param name="cmd">A reusable command associated with the open SQLite connection.</param>
    /// <param name="cfg">The validated SQLite table settings.</param>
    /// <param name="column">The trusted column name to inspect and add.</param>
    /// <param name="definitionSql">The trusted SQLite type, nullability, and default fragment for the new column.</param>
    /// <param name="ct">Signals that the operation should be canceled.</param>
    /// <returns>A task that completes after the inspection and any required <c>ALTER TABLE</c>.</returns>
    /// <remarks>Replaces <paramref name="cmd"/>'s command text and executes one or two database commands.</remarks>
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
    /// Builds an idempotent in-place upgrade for the primary-report flag. Auto-created stores from older
    /// versions may already have the table, while externally managed stores remain the host's responsibility.
    /// </summary>
    /// <param name="cfg">The validated dialect and physical table settings.</param>
    /// <returns>The SQL statement that adds the primary-report column.</returns>
    /// <exception cref="InvalidOperationException">Thrown for SQLite, whose columns must first be inspected through <see cref="AddSqliteColumnIfMissing"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="cfg"/> contains an unsupported dialect.</exception>
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
    /// Builds an idempotent upgrade for the normalized title-key column. The column remains nullable until
    /// legacy rows are backfilled in application code, preserving the exact <see cref="TitleKey"/> rule across dialects.
    /// </summary>
    /// <param name="cfg">The validated dialect and physical table settings.</param>
    /// <returns>The SQL statement that adds the normalized title-key column.</returns>
    /// <exception cref="InvalidOperationException">Thrown for SQLite, whose columns must first be inspected through <see cref="AddSqliteColumnIfMissing"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="cfg"/> contains an unsupported dialect.</exception>
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

    /// <summary>
    /// Computes and persists title keys for rows written before the normalized key column existed.
    /// </summary>
    /// <param name="conn">The open connection used for the read and subsequent updates.</param>
    /// <param name="cfg">The validated dialect and physical table settings.</param>
    /// <param name="ct">Signals that the operation should be canceled.</param>
    /// <returns>A task that completes after every row with a null key has been updated.</returns>
    /// <remarks>Buffers identifiers and titles before issuing updates so providers need not support concurrent readers and commands on one connection.</remarks>
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
    /// Creates the unique index that makes user-title uniqueness atomic. Configured rows are deliberately
    /// excluded so a checked-in document can shadow a user title without breaking synchronization.
    /// </summary>
    /// <param name="cmd">A reusable command associated with the open store connection.</param>
    /// <param name="cfg">The validated dialect and physical table settings.</param>
    /// <param name="ct">Signals that the operation should be canceled.</param>
    /// <returns>A task that completes after the database accepts the idempotent index DDL.</returns>
    /// <remarks>Replaces <paramref name="cmd"/>'s command text, logs it when enabled, and executes database DDL.</remarks>
    /// <exception cref="InvalidOperationException">Thrown when the index cannot be created, commonly because legacy user rows contain duplicate titles.</exception>
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

    /// <summary>
    /// Builds dialect-specific DDL for the partial or conditional user-title uniqueness index.
    /// </summary>
    /// <param name="cfg">The validated dialect and physical table settings.</param>
    /// <returns>The SQL statement that creates the title uniqueness index.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="cfg"/> contains an unsupported dialect.</exception>
    private static string CreateTitleIndexSql(SavedReportStoreConfig cfg)
    {
        var index = TitleIndexName(cfg.TableName);
        return cfg.Dialect switch
        {
            ReportDialect.Sqlite => $"""
                CREATE UNIQUE INDEX IF NOT EXISTS {index}
                ON {cfg.TableName} (REPORT_NAME, TITLE_KEY) WHERE ORIGIN = 'user'
                """,
            // Filtered-index DML needs the standard ANSI SET options; SqlClient's defaults
            // satisfy them (legacy tooling writing this table may not).
            ReportDialect.SqlServer => $"""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'{index}' AND object_id = OBJECT_ID(N'{cfg.TableName}'))
                    CREATE UNIQUE INDEX {index}
                    ON {cfg.TableName} (REPORT_NAME, TITLE_KEY) WHERE ORIGIN = 'user'
                """,
            // Provider constraint: oracle has no partial indexes; the CASE projections index
            // user rows only (rows where every keyed expression is NULL are not indexed). -955:
            // name already used; -1408: column list already indexed.
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
