using System.Text.RegularExpressions;

namespace InteractiveReport.Core.SavedReports;

/// <summary>
/// Persistence for saved report state documents. The store is storage and retrieval
/// only — who may see or mutate what is decided at the endpoint layer.
/// </summary>
public interface ISavedReportStore
{
    Task<SavedReport?> Get(string id, CancellationToken ct = default);

    /// <summary>
    /// Saved reports for one report definition visible to an identity: primary and
    /// global ones plus their own.
    /// </summary>
    Task<IReadOnlyList<SavedReport>> ListVisible(string reportName, string? identity, CancellationToken ct = default);

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

    /// <summary>Full-row update by id (last write wins); refreshes ModifiedUtc.</summary>
    Task<bool> Update(SavedReport report, CancellationToken ct = default);

    /// <summary>
    /// Insert-or-update by id that persists the row — including ModifiedUtc — exactly
    /// as given. The configured-document synchronizer's write path: synced rows carry
    /// their file's timestamp, so this deliberately never stamps, unlike
    /// <see cref="Create"/> and <see cref="Update"/>.
    /// </summary>
    Task Put(SavedReport report, CancellationToken ct = default);

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
    public DateTime ModifiedUtc { get; set; }
    public SavedReportOrigin Origin { get; set; } = SavedReportOrigin.User;

    public static string NewId() => Guid.NewGuid().ToString("n");
}

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
