// SQL saved-report persistence entrypoint: maps the store contract to provider-neutral SqlKata
// statements, then compiles them for the configured database dialect. Full detached snapshots
// provide optimistic-concurrency checks, while a revision predicate makes writes atomic. Optional
// schema creation is serialized per store target within this process.

using System.Data;
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
    public async Task<SavedReport?> Get(long id, CancellationToken ct = default)
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
        long id,
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
    public async Task<SavedReportMetadata?> GetMetadata(long id, CancellationToken ct = default)
    {
        var rows = await SelectMetadata(q => q.Where("ID", id), ct);
        return rows.SingleOrDefault();
    }

    /// <summary>Finds the database identity assigned to one configured report-document file.</summary>
    public async Task<SavedReport?> FindConfiguredFile(
        string reportName,
        string sourceFile,
        CancellationToken ct = default)
    {
        var rows = await Select(q => q
            .Where("REPORT_NAME", reportName)
            .Where("SOURCE_FILE", sourceFile), ct);
        return rows.SingleOrDefault();
    }

    /// <summary>
    /// Lists default, global, and caller-owned reports for one report definition.
    /// </summary>
    /// <param name="reportName">The configured report name whose definition or saved reports are being addressed.</param>
    /// <param name="identity">The exact owner identity to include; <see langword="null"/> includes no private reports.</param>
    /// <param name="ct">Signals that the operation should be canceled; defaults to <c>default</c>.</param>
    /// <returns>Visible reports ordered with the default and global entries first, then by title.</returns>
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
                .OrderByDesc("IS_DEFAULT").OrderByDesc("IS_GLOBAL").OrderBy("TITLE"),
            ct);
        return rows
            .Where(r => r.IsPublic
                || (identity is not null && string.Equals(r.Owner, identity, StringComparison.Ordinal)))
            .ToList();
    }

    /// <summary>
    /// Loads one configured report's complete document family in a single database query. No ownership or
    /// publication filtering is applied; callers reconcile this authoritative snapshot before
    /// filtering it in memory for the requesting identity.
    /// </summary>
    public Task<IReadOnlyList<SavedReport>> ListFamily(
        string reportName,
        CancellationToken ct = default)
    {
        var config = Validated(_config());
        return Select(
            config,
            query => query
                .Where("REPORT_NAME", reportName)
                .OrderByDesc("IS_DEFAULT")
                .OrderByDesc("IS_GLOBAL")
                .OrderBy("TITLE"),
            ct);
    }

    /// <summary>
    /// Lists metadata for default, global, and caller-owned reports without loading state JSON.
    /// </summary>
    /// <param name="reportName">The configured report name whose definition or saved reports are being addressed.</param>
    /// <param name="identity">The exact owner identity to include; <see langword="null"/> includes no private reports.</param>
    /// <param name="ct">Signals that the operation should be canceled; defaults to <c>default</c>.</param>
    /// <returns>Visible metadata ordered with the default and global entries first, then by title.</returns>
    public async Task<IReadOnlyList<SavedReportMetadata>> ListVisibleMetadata(
        string reportName,
        string? identity,
        CancellationToken ct = default)
    {
        var rows = await SelectMetadata(
            q => q.Where("REPORT_NAME", reportName)
                .OrderByDesc("IS_DEFAULT").OrderByDesc("IS_GLOBAL").OrderBy("TITLE"),
            ct);
        return rows
            .Where(r => r.IsPublic
                || (identity is not null && string.Equals(r.Owner, identity, StringComparison.Ordinal)))
            .ToList();
    }

    /// <summary>
    /// Finds the single row explicitly flagged as the default for one report definition.
    /// </summary>
    /// <param name="reportName">The configured report name whose definition or saved reports are being addressed.</param>
    /// <param name="ct">Signals that the operation should be canceled; defaults to <c>default</c>.</param>
    /// <returns>The flagged default row, or <see langword="null"/>.</returns>
    public async Task<SavedReport?> FindDefault(
        string reportName,
        CancellationToken ct = default)
    {
        var rows = await Select(
            q => q.Where("REPORT_NAME", reportName)
                .Where("IS_DEFAULT", 1),
            ct);
        return rows.SingleOrDefault();
    }

    /// <summary>
    /// Finds a saved report by report name and the store's normalized title key.
    /// </summary>
    /// <param name="reportName">The configured report name whose definition or saved reports are being addressed.</param>
    /// <param name="title">The title to trim and case-fold for comparison.</param>
    /// <param name="exceptId">An identifier to omit, typically the row being renamed; defaults to <see langword="null"/>.</param>
    /// <param name="ct">Signals that the operation should be canceled; defaults to <c>default</c>.</param>
    /// <returns>The configured match in preference to a user match, or <see langword="null"/> when none exists.</returns>
    public async Task<SavedReport?> FindTitleCollision(
        string reportName,
        string title,
        string? owner,
        bool isPublic,
        long? exceptId = null,
        CancellationToken ct = default)
    {
        var rows = await Select(
            q => q.Where("REPORT_NAME", reportName).Where("TITLE_KEY", TitleKey(title)),
            ct);
        return rows
            .Where(report => report.Id != exceptId)
            .Where(report => isPublic
                ? report.IsPublic
                : report.IsPublic || string.Equals(report.Owner, owner, StringComparison.Ordinal))
            .OrderByDescending(report => report.IsPublic)
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
        ValidateReport(report);
        var config = Validated(_config());
        report.ModifiedUtc = DateTime.UtcNow;
        try
        {
            report.Id = await Insert(config, report, ct);
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
        ValidateReport(report);
        var config = Validated(_config());
        if (report.Id != expected.Id)
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
        if (report.Id == 0)
        {
            var insertConfig = Validated(_config());
            if (!await Put(insertConfig, report, expected: null, ct))
                throw new InvalidOperationException("A generated saved-report insert did not commit.");
            return;
        }
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
        ValidateReport(report);
        if (expected is not null && report.Id != expected.Id)
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
                if (report.Id != 0)
                    throw new ArgumentException(
                        "A new report document must leave its database-generated id unset.",
                        nameof(report));
                report.Id = await Insert(config, report with { ModifiedUtc = modifiedUtc }, ct);
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
    public async Task<bool> Delete(long id, CancellationToken ct = default)
        => await Execute(
            config => new Query(config.TableName).Where("ID", id).AsDelete(),
            ct) == 1;

    /// <summary>
    /// Encodes a saved-report origin as its stable database token.
    /// </summary>
    /// <param name="origin">The origin to encode.</param>
    /// <returns>The persisted saved-report origin token.</returns>
    private static string OriginText(SavedReportOrigin origin)
        => origin switch
        {
            SavedReportOrigin.Configured => "configured",
            SavedReportOrigin.Synthetic => "synthetic",
            _ => "user",
        };

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
    /// <returns>The matching stable origin token, or <see cref="SavedReportOrigin.User"/> for an unknown value.</returns>
    private static SavedReportOrigin OriginFrom(string text)
        => text.ToLowerInvariant() switch
        {
            "configured" => SavedReportOrigin.Configured,
            "synthetic" => SavedReportOrigin.Synthetic,
            _ => SavedReportOrigin.User,
        };

    /// <summary>
    /// Maps a saved report to the provider-neutral column/value dictionary used by insert and update commands.
    /// </summary>
    /// <param name="r">The saved report to serialize for a write statement.</param>
    /// <returns>The persistence columns and their values.</returns>
    private static Dictionary<string, object?> ToRow(SavedReport r) => new()
    {
        ["ID"] = r.Id,
        ["REPORT_NAME"] = r.ReportName,
        ["SOURCE_FILE"] = r.SourceFile,
        ["TITLE"] = r.Title,
        ["TITLE_KEY"] = TitleKey(r.Title),
        ["OWNER"] = r.Owner,
        ["IS_GLOBAL"] = r.IsGlobal ? 1 : 0,
        ["IS_DEFAULT"] = r.IsDefault ? 1 : 0,
        ["TITLE_SCOPE"] = TitleScope(r),
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
        => current.Id == expected.Id
            && string.Equals(current.ReportName, expected.ReportName, StringComparison.Ordinal)
            && string.Equals(current.SourceFile, expected.SourceFile, StringComparison.Ordinal)
            && string.Equals(current.Title, expected.Title, StringComparison.Ordinal)
            && string.Equals(current.Owner, expected.Owner, StringComparison.Ordinal)
            && current.IsGlobal == expected.IsGlobal
            && current.IsDefault == expected.IsDefault
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
    /// Builds the atomic title scope used by the replacement persistence contract. Configured
    /// files have an independent scope per source identity: deployment declarations may shadow
    /// any user or configured title, while API-authored reports retain public/private uniqueness.
    /// </summary>
    internal static string TitleScope(SavedReport report)
        => report.Origin == SavedReportOrigin.Configured
            ? $"configured:{report.SourceFile}"
            : report.IsPublic ? "public" : $"private:{report.Owner}";

    private static void ValidateReport(SavedReport report)
    {
        if (report.Origin == SavedReportOrigin.Configured)
        {
            if (string.IsNullOrWhiteSpace(report.SourceFile))
                throw new ArgumentException(
                    "A configured report document requires its configured source file.",
                    nameof(report));
            return;
        }

        if (report.StateJson is null)
            throw new ArgumentException(
                "A database-backed report document requires persisted state JSON.",
                nameof(report));
        if (report.SourceFile is not null)
            throw new ArgumentException(
                "Only configured report documents may reference a source file.",
                nameof(report));
    }

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
                || ex.Message.Contains("TITLE_SCOPE", StringComparison.OrdinalIgnoreCase));
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
            .Select("ID", "REPORT_NAME", "SOURCE_FILE", "TITLE", "OWNER", "IS_GLOBAL", "IS_DEFAULT", "STATE_JSON", "MODIFIED_UTC", "ORIGIN"));

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
                Id = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                ReportName = reader.GetString(1),
                SourceFile = reader.IsDBNull(2) ? null : reader.GetString(2),
                Title = reader.GetString(3),
                Owner = reader.IsDBNull(4) ? null : reader.GetString(4),
                IsGlobal = Convert.ToBoolean(reader.GetValue(5), CultureInfo.InvariantCulture),
                IsDefault = Convert.ToBoolean(reader.GetValue(6), CultureInfo.InvariantCulture),
                StateJson = reader.IsDBNull(7) ? null : reader.GetString(7),
                ModifiedUtc = DateTime.Parse(reader.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                Origin = OriginFrom(reader.GetString(9)),
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
            .Select("ID", "REPORT_NAME", "SOURCE_FILE", "TITLE", "OWNER", "IS_GLOBAL", "IS_DEFAULT", "MODIFIED_UTC", "ORIGIN"));

        await using var conn = await OpenConnection(cfg, ct);
        var compiled = DialectSupport.GetCompiler(cfg.Dialect).Compile(query);
        await using var cmd = CommandBuilder.Build(
            conn, compiled, NoParams, TimeoutSeconds, cfg.Dialect, _logger);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var result = new List<SavedReportMetadata>();
        while (await reader.ReadAsync(ct))
        {
            result.Add(new SavedReportMetadata(
                Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                Convert.ToBoolean(reader.GetValue(5), CultureInfo.InvariantCulture),
                Convert.ToBoolean(reader.GetValue(6), CultureInfo.InvariantCulture),
                DateTime.Parse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                OriginFrom(reader.GetString(8))));
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

    /// <summary>Executes a compiled non-query on an existing transaction.</summary>
    private async Task<int> Execute(
        DbConnection connection,
        DbTransaction transaction,
        SavedReportStoreConfig config,
        Query query,
        CancellationToken ct)
    {
        var compiled = DialectSupport.GetCompiler(config.Dialect).Compile(query);
        await using var command = CommandBuilder.Build(
            connection, compiled, NoParams, TimeoutSeconds, config.Dialect, _logger);
        command.Transaction = transaction;
        return await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Inserts a new row and returns the database-generated numeric identity.</summary>
    private async Task<long> Insert(
        SavedReportStoreConfig config,
        SavedReport report,
        CancellationToken ct)
    {
        if (report.Id != 0)
            throw new ArgumentException("New report documents must not supply an id.", nameof(report));

        var row = ToRow(report);
        row.Remove("ID");
        var compiled = DialectSupport.GetCompiler(config.Dialect).Compile(
            new Query(config.TableName).AsInsert(row));
        await using var connection = await OpenConnection(config, ct);
        await using var command = CommandBuilder.Build(
            connection, compiled, NoParams, TimeoutSeconds, config.Dialect, _logger);

        if (config.Dialect == ReportDialect.Oracle)
        {
            command.CommandText += " RETURNING ID INTO :ir_generated_id";
            var output = command.CreateParameter();
            output.ParameterName = "ir_generated_id";
            output.DbType = DbType.Int64;
            output.Direction = ParameterDirection.Output;
            command.Parameters.Add(output);
            CommandBuilder.Log(command, _logger);
            await command.ExecuteNonQueryAsync(ct);
            return Convert.ToInt64(output.Value, CultureInfo.InvariantCulture);
        }

        // Provider constraint: SQLite's INSERT ... RETURNING reader can throw SQLITE_BUSY
        // while it is being disposed under concurrent writers, after the row has already been
        // produced. Complete the insert first, then read the connection-local identity instead.
        if (config.Dialect == ReportDialect.Sqlite)
        {
            CommandBuilder.Log(command, _logger);
            await command.ExecuteNonQueryAsync(ct);
            command.CommandText = "SELECT last_insert_rowid()";
            command.Parameters.Clear();
            CommandBuilder.Log(command, _logger);
            var sqliteId = await command.ExecuteScalarAsync(ct)
                ?? throw new InvalidOperationException(
                    "SQLite did not return a generated report-document id.");
            return Convert.ToInt64(sqliteId, CultureInfo.InvariantCulture);
        }

        command.CommandText += config.Dialect switch
        {
            ReportDialect.Postgres => " RETURNING \"ID\"",
            ReportDialect.SqlServer => "; SELECT CAST(SCOPE_IDENTITY() AS BIGINT)",
            _ => throw new ArgumentOutOfRangeException(nameof(config), config.Dialect, null),
        };
        CommandBuilder.Log(command, _logger);
        var generated = await command.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException("The database did not return a generated report-document id.");
        return Convert.ToInt64(generated, CultureInfo.InvariantCulture);
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
    /// Creates an auto-managed saved-report table once per process and store target.
    /// </summary>
    /// <param name="conn">The open connection on which to run DDL.</param>
    /// <param name="cfg">The validated dialect and physical table settings.</param>
    /// <param name="ct">Signals that the operation should be canceled.</param>
    /// <returns>A task that completes after the table and unique indexes are ready.</returns>
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
            await CreateIndexes(cmd, cfg, ct);
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
        ReportDialect.Sqlite => $"""
            CREATE TABLE IF NOT EXISTS {cfg.TableName} (
                ID           INTEGER PRIMARY KEY AUTOINCREMENT,
                REPORT_NAME  TEXT NOT NULL,
                SOURCE_FILE  TEXT NULL,
                TITLE        TEXT NOT NULL,
                TITLE_KEY    TEXT NOT NULL,
                TITLE_SCOPE  TEXT NOT NULL,
                OWNER        TEXT NULL,
                IS_GLOBAL    INTEGER NOT NULL,
                IS_DEFAULT   INTEGER NOT NULL,
                STATE_JSON   TEXT NULL,
                MODIFIED_UTC TEXT NOT NULL,
                ORIGIN       TEXT NOT NULL DEFAULT 'user'
            )
            """,
        ReportDialect.SqlServer => $"""
            IF OBJECT_ID(N'{cfg.TableName}', N'U') IS NULL
            CREATE TABLE {cfg.TableName} (
                ID           BIGINT IDENTITY(1,1) PRIMARY KEY,
                REPORT_NAME  NVARCHAR(200) NOT NULL,
                SOURCE_FILE  NVARCHAR(400) NULL,
                TITLE        NVARCHAR(200) NOT NULL,
                TITLE_KEY    NVARCHAR(400) NOT NULL,
                TITLE_SCOPE  NVARCHAR(420) NOT NULL,
                OWNER        NVARCHAR(400) NULL,
                IS_GLOBAL    INT NOT NULL,
                IS_DEFAULT   INT NOT NULL,
                STATE_JSON   NVARCHAR(MAX) NULL,
                MODIFIED_UTC NVARCHAR(40) NOT NULL,
                ORIGIN       NVARCHAR(20) NOT NULL DEFAULT 'user'
            )
            """,
        ReportDialect.Oracle => $"""
            BEGIN
                EXECUTE IMMEDIATE 'CREATE TABLE {cfg.TableName} (
                    ID           NUMBER(19) GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                    REPORT_NAME  VARCHAR2(200) NOT NULL,
                    SOURCE_FILE  VARCHAR2(400) NULL,
                    TITLE        VARCHAR2(200) NOT NULL,
                    TITLE_KEY    VARCHAR2(400 CHAR) NOT NULL,
                    TITLE_SCOPE  VARCHAR2(420 CHAR) NOT NULL,
                    OWNER        VARCHAR2(400) NULL,
                    IS_GLOBAL    NUMBER(1) NOT NULL,
                    IS_DEFAULT   NUMBER(1) NOT NULL,
                    STATE_JSON   CLOB NULL,
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
                "ID"           BIGINT GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                "REPORT_NAME"  VARCHAR(200) NOT NULL,
                "SOURCE_FILE"  VARCHAR(400) NULL,
                "TITLE"        VARCHAR(200) NOT NULL,
                "TITLE_KEY"    VARCHAR(400) NOT NULL,
                "TITLE_SCOPE"  VARCHAR(420) NOT NULL,
                "OWNER"        VARCHAR(400) NULL,
                "IS_GLOBAL"    INT NOT NULL,
                "IS_DEFAULT"   INT NOT NULL,
                "STATE_JSON"   TEXT NULL,
                "MODIFIED_UTC" VARCHAR(40) NOT NULL,
                "ORIGIN"       VARCHAR(20) NOT NULL DEFAULT 'user'
            )
            """,
        _ => throw new ArgumentOutOfRangeException(nameof(cfg), cfg.Dialect, null),
    };

    /// <summary>
    /// Creates the indexes that make title scopes, configured-file identities, and defaults unique.
    /// </summary>
    /// <param name="cmd">A reusable command associated with the open store connection.</param>
    /// <param name="cfg">The validated dialect and physical table settings.</param>
    /// <param name="ct">Signals that the operation should be canceled.</param>
    /// <returns>A task that completes after the database accepts the idempotent index DDL.</returns>
    /// <remarks>Replaces <paramref name="cmd"/>'s command text, logs it when enabled, and executes database DDL.</remarks>
    /// <exception cref="InvalidOperationException">Thrown when an index cannot be created.</exception>
    private async Task CreateIndexes(DbCommand cmd, SavedReportStoreConfig cfg, CancellationToken ct)
    {
        foreach (var sql in new[]
                 {
                     CreateTitleIndexSql(cfg),
                     CreateSourceKeyIndexSql(cfg),
                     CreateDefaultIndexSql(cfg),
                 })
        {
            cmd.CommandText = sql;
            try
            {
                CommandBuilder.Log(cmd, _logger);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            catch (DbException ex)
            {
                throw new InvalidOperationException(
                    $"Could not create a saved-report uniqueness index on '{cfg.TableName}'.",
                    ex);
            }
        }
    }

    /// <inheritdoc />
    public async Task<bool> ReplaceDefault(
        SavedReport report,
        SavedReport expected,
        SavedReport currentDefault,
        CancellationToken ct = default)
    {
        ValidateReport(report);
        var config = Validated(_config());
        if (report.Id != expected.Id)
            throw new ArgumentException(
                "The replacement and expected saved-report snapshots must have the same id.",
                nameof(expected));
        if (currentDefault.Id == expected.Id)
            return await Update(report, expected, ct);
        if (!report.IsDefault || !report.IsGlobal)
            throw new ArgumentException("A replacement default must also be globally published.", nameof(report));
        if (!currentDefault.IsDefault
            || !string.Equals(currentDefault.ReportName, report.ReportName, StringComparison.Ordinal))
            throw new ArgumentException(
                "The current default must belong to the replacement report's family.",
                nameof(currentDefault));
        if (!await IsCurrentSnapshot(config, expected, ct)
            || !await IsCurrentSnapshot(config, currentDefault, ct))
            return false;

        var promotedModifiedUtc = NextModifiedUtc(expected.ModifiedUtc);
        var demotedModifiedUtc = NextModifiedUtc(currentDefault.ModifiedUtc);
        var promotedRow = ToRow(report with { ModifiedUtc = promotedModifiedUtc });
        promotedRow.Remove("ID");
        var demotedRow = ToRow(currentDefault with
        {
            IsDefault = false,
            IsGlobal = true,
            ModifiedUtc = demotedModifiedUtc,
        });
        demotedRow.Remove("ID");

        try
        {
            await using var connection = await OpenConnection(config, ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            try
            {
                var demoted = await Execute(
                    connection,
                    transaction,
                    config,
                    MatchRevision(new Query(config.TableName), currentDefault).AsUpdate(demotedRow),
                    ct);
                if (demoted != 1)
                {
                    await transaction.RollbackAsync(ct);
                    return false;
                }

                var promoted = await Execute(
                    connection,
                    transaction,
                    config,
                    MatchRevision(new Query(config.TableName), expected).AsUpdate(promotedRow),
                    ct);
                if (promoted != 1)
                {
                    await transaction.RollbackAsync(ct);
                    return false;
                }

                await transaction.CommitAsync(ct);
                report.ModifiedUtc = promotedModifiedUtc;
                return true;
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }
        catch (DbException ex) when (IsTitleUniqueViolation(config, ex))
        {
            throw new SavedReportTitleConflictException(report.ReportName, report.Title, ex);
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
                ON {cfg.TableName} (REPORT_NAME, TITLE_KEY, TITLE_SCOPE)
                """,
            // Filtered-index DML needs the standard ANSI SET options; SqlClient's defaults satisfy them.
            ReportDialect.SqlServer => $"""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'{index}' AND object_id = OBJECT_ID(N'{cfg.TableName}'))
                    CREATE UNIQUE INDEX {index}
                    ON {cfg.TableName} (REPORT_NAME, TITLE_KEY, TITLE_SCOPE)
                """,
            // Provider constraint: oracle has no partial indexes; the CASE projections index
            // user rows only (rows where every keyed expression is NULL are not indexed). -955:
            // name already used; -1408: column list already indexed.
            ReportDialect.Oracle => $"""
                BEGIN
                    EXECUTE IMMEDIATE 'CREATE UNIQUE INDEX {index} ON {cfg.TableName}
                        (REPORT_NAME, TITLE_KEY, TITLE_SCOPE)';
                EXCEPTION WHEN OTHERS THEN
                    IF SQLCODE NOT IN (-955, -1408) THEN RAISE; END IF;
                END;
                """,
            ReportDialect.Postgres => $"""
                CREATE UNIQUE INDEX IF NOT EXISTS "{index}"
                ON "{cfg.TableName}" ("REPORT_NAME", "TITLE_KEY", "TITLE_SCOPE")
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(cfg), cfg.Dialect, null),
        };
    }

    private static string CreateSourceKeyIndexSql(SavedReportStoreConfig cfg)
    {
        var index = cfg.TableName + "_SOURCE_UX";
        return cfg.Dialect switch
        {
            ReportDialect.Sqlite => $"CREATE UNIQUE INDEX IF NOT EXISTS {index} ON {cfg.TableName} (REPORT_NAME, SOURCE_FILE) WHERE SOURCE_FILE IS NOT NULL",
            ReportDialect.SqlServer => $"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'{index}' AND object_id = OBJECT_ID(N'{cfg.TableName}')) CREATE UNIQUE INDEX {index} ON {cfg.TableName} (REPORT_NAME, SOURCE_FILE) WHERE SOURCE_FILE IS NOT NULL",
            ReportDialect.Oracle => $"""
                BEGIN
                    EXECUTE IMMEDIATE 'CREATE UNIQUE INDEX {index} ON {cfg.TableName} (REPORT_NAME, SOURCE_FILE)';
                EXCEPTION WHEN OTHERS THEN
                    IF SQLCODE NOT IN (-955, -1408) THEN RAISE; END IF;
                END;
                """,
            ReportDialect.Postgres => $"CREATE UNIQUE INDEX IF NOT EXISTS \"{index}\" ON \"{cfg.TableName}\" (\"REPORT_NAME\", \"SOURCE_FILE\") WHERE \"SOURCE_FILE\" IS NOT NULL",
            _ => throw new ArgumentOutOfRangeException(nameof(cfg), cfg.Dialect, null),
        };
    }

    private static string CreateDefaultIndexSql(SavedReportStoreConfig cfg)
    {
        var index = cfg.TableName + "_DEFAULT_UX";
        return cfg.Dialect switch
        {
            ReportDialect.Sqlite => $"CREATE UNIQUE INDEX IF NOT EXISTS {index} ON {cfg.TableName} (REPORT_NAME) WHERE IS_DEFAULT = 1",
            ReportDialect.SqlServer => $"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'{index}' AND object_id = OBJECT_ID(N'{cfg.TableName}')) CREATE UNIQUE INDEX {index} ON {cfg.TableName} (REPORT_NAME) WHERE IS_DEFAULT = 1",
            ReportDialect.Oracle => $"""
                BEGIN
                    EXECUTE IMMEDIATE 'CREATE UNIQUE INDEX {index} ON {cfg.TableName}
                        (CASE WHEN IS_DEFAULT = 1 THEN REPORT_NAME ELSE NULL END)';
                EXCEPTION WHEN OTHERS THEN
                    IF SQLCODE NOT IN (-955, -1408) THEN RAISE; END IF;
                END;
                """,
            ReportDialect.Postgres => $"CREATE UNIQUE INDEX IF NOT EXISTS \"{index}\" ON \"{cfg.TableName}\" (\"REPORT_NAME\") WHERE \"IS_DEFAULT\" = 1",
            _ => throw new ArgumentOutOfRangeException(nameof(cfg), cfg.Dialect, null),
        };
    }

    private readonly record struct StoreTarget(
        string ConnectionName,
        ReportDialect Dialect,
        string TableName);
}
