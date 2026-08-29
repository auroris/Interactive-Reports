using System.Text.RegularExpressions;

namespace InteractiveReport.Core.SavedReports;

/// <summary>
/// Persistence for saved report state documents. The store is storage and retrieval
/// only — who may see or mutate what is decided at the endpoint layer.
/// </summary>
public interface ISavedReportStore
{
    /// <summary>
    /// Returns one detached, coherent version of the complete row. Metadata and
    /// StateJson must come from the same authoritative version, and later store
    /// mutations must not mutate the returned instance. Authorization paths rely on
    /// this snapshot boundary before they inspect metadata and consume the state.
    /// </summary>
    Task<SavedReport?> Get(string id, CancellationToken ct = default);

    /// <summary>
    /// Reads only authorization and presentation metadata. Implementations should
    /// avoid fetching the state document; the default preserves compatibility for
    /// custom stores that have not added a projection yet.
    /// </summary>
    async Task<SavedReportMetadata?> GetMetadata(string id, CancellationToken ct = default)
        => (await Get(id, ct))?.Metadata();

    /// <summary>
    /// Saved reports for one report definition visible to an identity: primary and
    /// global ones plus their own.
    /// </summary>
    Task<IReadOnlyList<SavedReport>> ListVisible(string reportName, string? identity, CancellationToken ct = default);

    /// <summary>Metadata-only counterpart to <see cref="ListVisible"/>.</summary>
    async Task<IReadOnlyList<SavedReportMetadata>> ListVisibleMetadata(
        string reportName,
        string? identity,
        CancellationToken ct = default)
        => (await ListVisible(reportName, identity, ct)).Select(report => report.Metadata()).ToList();

    /// <summary>
    /// The primary report titled Default that overrides a configured definition.
    /// User-origin rows win an externally introduced configured-title collision.
    /// </summary>
    async Task<SavedReport?> FindPrimaryDefault(string reportName, CancellationToken ct = default)
        => (await ListVisible(reportName, identity: null, ct))
            .Where(report => report.IsPrimary
                && string.Equals(report.Title, "Default", StringComparison.OrdinalIgnoreCase))
            .OrderBy(report => report.Origin == SavedReportOrigin.User ? 0 : 1)
            .ThenByDescending(report => report.ModifiedUtc)
            .FirstOrDefault();

    /// <summary>
    /// Finds a title collision within one already-authorized report definition. The
    /// scoped lookup prevents endpoint uniqueness checks from loading unrelated saved
    /// reports into memory.
    /// </summary>
    async Task<SavedReport?> FindByTitle(
        string reportName,
        string title,
        string? exceptId = null,
        CancellationToken ct = default)
        => (await ListAll(ct))
            .Where(report => !string.Equals(report.Id, exceptId, StringComparison.OrdinalIgnoreCase)
                             && string.Equals(report.ReportName, reportName, StringComparison.OrdinalIgnoreCase)
                             && string.Equals(report.Title, title.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(report => report.Origin == SavedReportOrigin.Configured ? 0 : 1)
            .FirstOrDefault();

    /// <summary>Every saved-report row in the system, across reports and origins.</summary>
    Task<IReadOnlyList<SavedReport>> ListAll(CancellationToken ct = default);

    Task Create(SavedReport report, CancellationToken ct = default);

    /// <summary>
    /// Atomically replaces <paramref name="expected"/> with <paramref name="report"/>
    /// when its authorization fields and ModifiedUtc concurrency version still match
    /// the authoritative snapshot. Returns false when the row was deleted or changed
    /// after it was read. Successful updates refresh both the stored ModifiedUtc and
    /// <paramref name="report"/>'s value so callers can return the committed revision.
    /// Snapshot string fields use ordinal equality, never the storage collation.
    /// </summary>
    Task<bool> Update(
        SavedReport report,
        SavedReport expected,
        CancellationToken ct = default);

    /// <summary>
    /// Insert-or-update convenience operation. Inserts preserve the supplied
    /// ModifiedUtc. Every replacement advances ModifiedUtc beyond the stored version,
    /// even when the supplied value is unchanged or older, so it remains a valid CAS
    /// revision. Implementations must retry conditional conflicts without applying a
    /// stale replacement to a newer row.
    /// </summary>
    Task Put(SavedReport report, CancellationToken ct = default);

    /// <summary>
    /// Atomically inserts <paramref name="report"/> when <paramref name="expected"/>
    /// is null and the id is absent, or replaces the row when it still equals the
    /// detached <paramref name="expected"/> snapshot. A replacement advances
    /// ModifiedUtc in storage and on <paramref name="report"/>. Returns false on a
    /// concurrent insert, update, or delete. Snapshot strings use ordinal equality.
    /// </summary>
    Task<bool> Put(
        SavedReport report,
        SavedReport? expected,
        CancellationToken ct = default);

    /// <summary>
    /// Atomically deletes a row only when its authorization fields and ModifiedUtc
    /// concurrency version still match the authoritative snapshot. Returns false when
    /// the row was deleted or changed. Endpoint authorization paths use this overload
    /// so a decision can never be applied to a different version of the resource.
    /// Snapshot string fields use ordinal equality, never the storage collation.
    /// </summary>
    Task<bool> Delete(SavedReport expected, CancellationToken ct = default);

    /// <summary>
    /// Unconditional delete by id for internal reconciliation, where the configured
    /// manifest is authoritative and deliberately removes an orphan regardless of its
    /// stored contents.
    /// </summary>
    Task<bool> Delete(string id, CancellationToken ct = default);
}

/// <summary>Where a saved-report row comes from; the server refuses to mutate configured rows.</summary>
public enum SavedReportOrigin
{
    /// <summary>Created through the saved-report endpoints by a user or administrator.</summary>
    User,

    /// <summary>Synced from a definition's configured document files; read-only.</summary>
    Configured,
}

/// <summary>
/// One saved report: a named state document belonging to a report definition.
/// Owner is the canonical identity value (see ReportIdentity); global reports keep the
/// owner who published them. Configured rows have no owner.
/// </summary>
public sealed record SavedReport
{
    public required string Id { get; init; }
    public required string ReportName { get; init; }
    public required string Title { get; set; }
    public required string? Owner { get; set; }
    public bool IsGlobal { get; set; }
    /// <summary>
    /// Administrator-controlled publication flag. Primary reports are visible to
    /// everyone who can access their underlying report definition. A primary report
    /// titled "Default" replaces that definition's generated default state.
    /// </summary>
    public bool IsPrimary { get; set; }
    /// <summary>The state document, stored verbatim as JSON text.</summary>
    public required string StateJson { get; set; }
    /// <summary>
    /// Persisted optimistic-concurrency revision. Create uses the current UTC time;
    /// configured-row inserts may seed it from a file mtime, but every subsequent
    /// replacement must advance it even when that source timestamp is unchanged.
    /// </summary>
    public DateTime ModifiedUtc { get; set; }
    public SavedReportOrigin Origin { get; set; } = SavedReportOrigin.User;

    public static string NewId() => Guid.NewGuid().ToString("n");

    public SavedReportMetadata Metadata() => new(
        Id,
        ReportName,
        Title,
        Owner,
        IsGlobal,
        IsPrimary,
        ModifiedUtc,
        Origin);
}

/// <summary>Saved-report fields needed for access checks and summaries, without state JSON.</summary>
public sealed record SavedReportMetadata(
    string Id,
    string ReportName,
    string Title,
    string? Owner,
    bool IsGlobal,
    bool IsPrimary,
    DateTime ModifiedUtc,
    SavedReportOrigin Origin);

/// <summary>Storage configuration; connection is a named IReportConnectionFactory entry.</summary>
public sealed partial record SavedReportStoreConfig(
    string ConnectionName,
    Model.ReportDialect Dialect,
    bool AutoCreate = true,
    string TableName = "IR_SAVED_REPORTS")
{
    /// <summary>
    /// The plain-identifier rule for table names. Anything embedding the name in SQL
    /// text (the store's DDL, the built-in listing definition) validates through this
    /// one gate.
    /// </summary>
    public static string EnsureValidTableName(string tableName)
    {
        if (!TableNamePattern().IsMatch(tableName))
            throw new InvalidOperationException($"Saved-report table name '{tableName}' is not a plain identifier.");
        return tableName;
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex TableNamePattern();
}
