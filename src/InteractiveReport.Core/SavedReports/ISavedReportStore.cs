using System.Text.RegularExpressions;

namespace InteractiveReport.Core.SavedReports;

/// <summary>
/// Defines persistence for saved report state documents. The store is storage and retrieval
/// only — who may see or mutate what is decided at the endpoint layer.
/// </summary>
public interface ISavedReportStore
{
    /// <summary>
    /// Returns one detached, coherent version of the complete row. Metadata and <c>StateJson</c> must come
    /// from the same authoritative version, and later store mutations must not mutate the returned instance.
    /// Authorization paths rely on this snapshot boundary before they inspect metadata and consume the
    /// state.
    /// </summary>
    /// <param name="id">The numeric report-document identifier.</param>
    /// <param name="ct">Cancels persistence access.</param>
    /// <returns>The detached row, or <see langword="null"/> when no id matches.</returns>
    Task<SavedReport?> Get(long id, CancellationToken ct = default);

    /// <summary>
    /// Reads only authorization and presentation metadata. Implementations should avoid
    /// fetching the state document; the default preserves compatibility for custom stores that have not
    /// added a projection yet.
    /// </summary>
    /// <param name="id">The numeric report-document identifier.</param>
    /// <param name="ct">Cancels persistence access.</param>
    /// <returns>The detached metadata, or <see langword="null"/> when no id matches.</returns>
    async Task<SavedReportMetadata?> GetMetadata(long id, CancellationToken ct = default)
        => (await Get(id, ct))?.Metadata();

    /// <summary>Finds the database identity assigned to one configured report-document file.</summary>
    async Task<SavedReport?> FindConfiguredFile(
        string reportName,
        string sourceFile,
        CancellationToken ct = default)
        => (await ListAll(ct)).SingleOrDefault(report =>
            string.Equals(report.ReportName, reportName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(report.SourceFile, sourceFile, StringComparison.Ordinal));

    /// <summary>
    /// Lists saved reports for one report definition visible to an identity: primary and global rows
    /// plus their own.
    /// </summary>
    /// <param name="reportName">The canonical report name that scopes the rows.</param>
    /// <param name="identity">The optional canonical caller identity used for ownership visibility.</param>
    /// <param name="ct">Cancels persistence access.</param>
    /// <returns>Detached visible rows in the store's defined listing order.</returns>
    Task<IReadOnlyList<SavedReport>> ListVisible(string reportName, string? identity, CancellationToken ct = default);

    /// <summary>
    /// Returns the metadata-only counterpart to <see cref="ListVisible"/>.
    /// </summary>
    /// <param name="reportName">The canonical report name that scopes the rows.</param>
    /// <param name="identity">The optional canonical caller identity used for ownership visibility.</param>
    /// <param name="ct">Cancels persistence access.</param>
    /// <returns>Detached visible metadata in the store's defined listing order.</returns>
    async Task<IReadOnlyList<SavedReportMetadata>> ListVisibleMetadata(
        string reportName,
        string? identity,
        CancellationToken ct = default)
        => (await ListVisible(reportName, identity, ct)).Select(report => report.Metadata()).ToList();

    /// <summary>
    /// Finds the specially flagged default document for one report family.
    /// </summary>
    /// <param name="reportName">The canonical report name that scopes the search.</param>
    /// <param name="ct">Cancels persistence access.</param>
    /// <returns>The flagged default document, or <see langword="null"/> when none exists.</returns>
    async Task<SavedReport?> FindDefault(string reportName, CancellationToken ct = default)
        => (await ListVisible(reportName, identity: null, ct))
            .Where(report => report.IsDefault)
            .FirstOrDefault();

    /// <summary>
    /// Finds a title collision in the proposed document's visibility scope.
    /// </summary>
    /// <param name="reportName">The canonical report name that scopes title uniqueness.</param>
    /// <param name="title">The title to trim and compare case-insensitively.</param>
    /// <param name="exceptId">A saved-report identifier to exclude from the title-collision search; <see langword="null"/> excludes none; defaults to <c>null</c>.</param>
    /// <param name="ct">Cancels persistence access.</param>
    /// <returns>The configured row first, otherwise a user row, or <see langword="null"/> when available.</returns>
    async Task<SavedReport?> FindTitleCollision(
        string reportName,
        string title,
        string? owner,
        bool isPublic,
        long? exceptId = null,
        CancellationToken ct = default)
        => (await ListAll(ct))
            .Where(report => report.Id != exceptId
                             && string.Equals(report.ReportName, reportName, StringComparison.OrdinalIgnoreCase)
                             && string.Equals(report.Title, title.Trim(), StringComparison.OrdinalIgnoreCase)
                             && (isPublic
                                 ? report.IsPublic
                                 : report.IsPublic || string.Equals(report.Owner, owner, StringComparison.Ordinal)))
            .OrderByDescending(report => report.IsPublic)
            .FirstOrDefault();

    /// <summary>
    /// Lists every saved-report row in the system across report definitions and origins.
    /// </summary>
    /// <param name="ct">Cancels persistence access.</param>
    /// <returns>Detached rows in the store's defined listing order.</returns>
    Task<IReadOnlyList<SavedReport>> ListAll(CancellationToken ct = default);

    /// <summary>
    /// Inserts a new saved report and assigns its committed modification timestamp.
    /// </summary>
    /// <param name="report">The new row; its modification timestamp is replaced with the committed revision.</param>
    /// <param name="ct">Cancels persistence.</param>
    /// <returns>A task that completes after the insert commits.</returns>
    /// <exception cref="SavedReportTitleConflictException">Thrown when another row in the report already owns the title.</exception>
    Task Create(SavedReport report, CancellationToken ct = default);

    /// <summary>
    /// Atomically replaces <paramref name="expected"/> with <paramref name="report"/> when its
    /// authorization fields and ModifiedUtc concurrency version still match the authoritative snapshot.
    /// Returns false when the row was deleted or changed after it was read. Successful updates refresh both
    /// the stored ModifiedUtc and <paramref name="report"/>'s value so callers can return the committed
    /// revision. Snapshot string fields use ordinal equality, never the storage collation.
    /// </summary>
    /// <param name="report">The desired replacement; success updates its modification timestamp to the committed revision.</param>
    /// <param name="expected">The detached authoritative snapshot used for compare-and-swap.</param>
    /// <param name="ct">Cancels persistence.</param>
    /// <returns>A task whose result is <see langword="true"/> when the expected snapshot was current and the update committed; otherwise, <see langword="false"/>.</returns>
    Task<bool> Update(
        SavedReport report,
        SavedReport expected,
        CancellationToken ct = default);

    /// <summary>
    /// Inserts or replaces a row with internal compare-and-swap retries. Inserts preserve the supplied <c>ModifiedUtc</c>. Every
    /// replacement advances ModifiedUtc beyond the stored version, even when the supplied value is unchanged
    /// or older, so it remains a valid CAS revision. Implementations must retry conditional conflicts
    /// without applying a stale replacement to a newer row.
    /// </summary>
    /// <param name="report">The desired row; replacement updates its modification timestamp to the committed revision.</param>
    /// <param name="ct">Cancels persistence and retries.</param>
    /// <returns>A task that completes after an insert or replacement commits.</returns>
    Task Put(SavedReport report, CancellationToken ct = default);

    /// <summary>
    /// Atomically inserts <paramref name="report"/> when <paramref name="expected"/> is null and the id
    /// is absent, or replaces the row when it still equals the detached <paramref name="expected"/>
    /// snapshot. A replacement advances ModifiedUtc in storage and on <paramref name="report"/>. Returns
    /// false on a concurrent insert, update, or delete. Snapshot strings use ordinal equality.
    /// </summary>
    /// <param name="report">The desired row; replacement updates its modification timestamp to the committed revision.</param>
    /// <param name="expected">The expected detached row, or <see langword="null"/> to require an absent id.</param>
    /// <param name="ct">Cancels persistence.</param>
    /// <returns>A task whose result is <see langword="true"/> when the create or replacement committed against the expected snapshot; otherwise, <see langword="false"/>.</returns>
    Task<bool> Put(
        SavedReport report,
        SavedReport? expected,
        CancellationToken ct = default);

    /// <summary>
    /// Atomically deletes a row only when its authorization fields and <c>ModifiedUtc</c> concurrency
    /// version still match the authoritative snapshot. Returns false when the row was deleted or changed.
    /// Endpoint authorization paths use this overload so a decision can never be applied to a different
    /// version of the resource. Snapshot string fields use ordinal equality, never the storage collation.
    /// </summary>
    /// <param name="expected">The detached authoritative snapshot used for compare-and-delete.</param>
    /// <param name="ct">Cancels persistence.</param>
    /// <returns>A task whose result is <see langword="true"/> when the requested row was deleted; otherwise, <see langword="false"/>.</returns>
    Task<bool> Delete(SavedReport expected, CancellationToken ct = default);

    /// <summary>
    /// Deletes unconditionally by id for internal reconciliation, where the configured manifest is
    /// authoritative and deliberately removes an orphan regardless of its stored contents.
    /// </summary>
    /// <param name="id">The exact saved-report identifier to remove.</param>
    /// <param name="ct">Cancels persistence.</param>
    /// <returns>A task whose result is <see langword="true"/> when the requested row was deleted; otherwise, <see langword="false"/>.</returns>
    Task<bool> Delete(long id, CancellationToken ct = default);
}

/// <summary>Identifies where a saved-report row originates; public persistence APIs treat configured rows as read-only.</summary>
public enum SavedReportOrigin
{
    /// <summary>Created through saved-report or report-document endpoints by a user or administrator.</summary>
    User,

    /// <summary>Mirrored from a definition's configured document files.</summary>
    Configured,
}

/// <summary>
/// Represents one named state document belonging to a report definition.
/// Owner is the canonical identity value (see ReportIdentity); global reports keep the
/// owner who published them. Configured rows have no owner.
/// </summary>
public sealed record SavedReport
{
    /// <summary>Gets the stable row identifier.</summary>
    public long Id { get; set; }
    /// <summary>Gets the canonical report definition name.</summary>
    public required string ReportName { get; init; }
    /// <summary>Gets the configured file reference for a file-backed document.</summary>
    public string? SourceFile { get; init; }
    /// <summary>
    /// Gets or sets the display title. API-authored rows are unique within the caller-visible
    /// public/private scope; configured file identities deliberately bypass title uniqueness.
    /// </summary>
    public required string Title { get; set; }
    /// <summary>Gets or sets the canonical owner identity; configured rows have no owner.</summary>
    public required string? Owner { get; set; }
    /// <summary>Gets or sets whether all callers authorized for the report may load this row.</summary>
    public bool IsGlobal { get; set; }
    /// <summary>Gets or sets whether this is the durable default document for its report family.</summary>
    public bool IsDefault { get; set; }
    /// <summary>
    /// Gets or sets the administrator-controlled publication flag. Primary reports are visible to
    /// everyone who can access their underlying report definition.
    /// </summary>
    public bool IsPrimary { get; set; }
    /// <summary>Gets or sets the state document stored as JSON text.</summary>
    public string? StateJson { get; set; }
    /// <summary>
    /// Gets or sets the persisted optimistic-concurrency revision. Create uses the current UTC time;
    /// configured-row inserts may seed it from a file mtime, but every subsequent
    /// replacement must advance it even when that source timestamp is unchanged.
    /// </summary>
    public DateTime ModifiedUtc { get; set; }
    /// <summary>Gets or sets whether the row was authored by a user or identifies a configured file.</summary>
    public SavedReportOrigin Origin { get; set; } = SavedReportOrigin.User;

    /// <summary>Gets whether the document belongs to the public name and visibility scope.</summary>
    public bool IsPublic => IsDefault || IsGlobal || IsPrimary || Origin == SavedReportOrigin.Configured;

    /// <summary>
    /// Projects a complete saved report into its metadata-only representation.
    /// </summary>
    /// <returns>A detached value containing every field except <see cref="StateJson"/>.</returns>
    public SavedReportMetadata Metadata() => new(
        Id,
        ReportName,
        SourceFile,
        Title,
        Owner,
        IsGlobal,
        IsDefault,
        IsPrimary,
        ModifiedUtc,
        Origin);
}

/// <summary>Contains saved-report fields needed for access checks and summaries, without state JSON.</summary>
public sealed record SavedReportMetadata(
    long Id,
    string ReportName,
    string? SourceFile,
    string Title,
    string? Owner,
    bool IsGlobal,
    bool IsDefault,
    bool IsPrimary,
    DateTime ModifiedUtc,
    SavedReportOrigin Origin)
{
    /// <summary>Gets whether the document belongs to the public name and visibility scope.</summary>
    public bool IsPublic => IsDefault || IsGlobal || IsPrimary || Origin == SavedReportOrigin.Configured;
}

/// <summary>Defines saved-report storage; the connection name resolves through <c>IReportConnectionFactory</c>.</summary>
public sealed partial record SavedReportStoreConfig(
    string ConnectionName,
    Model.ReportDialect Dialect,
    bool AutoCreate = true,
    string TableName = "IR_SAVED_REPORTS")
{
    /// <summary>
    /// Applies the plain-identifier rule for table names. Anything embedding the name in
    /// SQL text (the store's DDL, the built-in listing definition) validates through this one gate.
    /// </summary>
    /// <param name="tableName">The configured physical table name to validate.</param>
    /// <returns><paramref name="tableName"/> unchanged when it is a safe unquoted identifier.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the name is blank, starts with a digit, or contains non-identifier characters.</exception>
    public static string EnsureValidTableName(string tableName)
    {
        if (!TableNamePattern().IsMatch(tableName))
            throw new InvalidOperationException($"Saved-report table name '{tableName}' is not a plain identifier.");
        return tableName;
    }

    /// <summary>
    /// Builds the compiled validation pattern for safe unquoted persistence table names.
    /// </summary>
    /// <returns>The compiled regular expression.</returns>
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex TableNamePattern();
}
