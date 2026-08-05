namespace InteractiveReport.Core.SavedReports;

/// <summary>
/// Persistence for saved report state documents. The store is storage and retrieval
/// only — who may see or mutate what is decided at the endpoint layer.
/// </summary>
public interface ISavedReportStore
{
    Task<SavedReport?> Get(string id, CancellationToken ct = default);

    /// <summary>Saved reports for one report definition visible to an identity: global ones plus their own.</summary>
    Task<IReadOnlyList<SavedReport>> ListVisible(string reportName, string? identity, CancellationToken ct = default);

    /// <summary>Every saved report in the system (the administrator view).</summary>
    Task<IReadOnlyList<SavedReport>> ListAll(CancellationToken ct = default);

    Task Create(SavedReport report, CancellationToken ct = default);

    /// <summary>Full-row update by id (last write wins); refreshes ModifiedUtc.</summary>
    Task<bool> Update(SavedReport report, CancellationToken ct = default);

    Task<bool> Delete(string id, CancellationToken ct = default);
}

/// <summary>
/// One saved report: a named state document belonging to a report definition.
/// Owner is the canonical identity value (see ReportIdentity); global reports keep the
/// owner who published them.
/// </summary>
public sealed record SavedReport
{
    public required string Id { get; init; }
    public required string ReportName { get; init; }
    public required string Title { get; set; }
    public required string Owner { get; set; }
    public bool IsGlobal { get; set; }
    /// <summary>The state document, stored verbatim as JSON text.</summary>
    public required string StateJson { get; set; }
    public DateTime ModifiedUtc { get; set; }

    public static string NewId() => Guid.NewGuid().ToString("n");
}

/// <summary>Storage configuration; connection is a named IReportConnectionFactory entry.</summary>
public sealed record SavedReportStoreConfig(
    string ConnectionName,
    Model.ReportDialect Dialect,
    bool AutoCreate = true,
    string TableName = "IR_SAVED_REPORTS");
