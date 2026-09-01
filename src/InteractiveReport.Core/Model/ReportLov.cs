using InteractiveReport.Core.Execution;

namespace InteractiveReport.Core.Model;

/// <summary>
/// Selects one column from the active table of a complete report document for a bounded
/// list-of-values lookup.
/// </summary>
/// <remarks>
/// <see cref="Document"/> is required because the client may be querying an unsaved working
/// document. The server compiles that document before it reads values, so its current filters,
/// computed columns, search, and table ancestry participate in the lookup.
/// </remarks>
public sealed class ReportLovRequest
{
    /// <summary>Gets or sets the complete current report document.</summary>
    public ReportState? Document { get; set; }

    /// <summary>Gets or sets the document's current active table identifier.</summary>
    public string? Table { get; set; }

    /// <summary>Gets or sets the one logical column whose distinct values are requested.</summary>
    public string? Column { get; set; }

    /// <summary>
    /// Gets or sets optional user-entered text matched as a case-insensitive substring of
    /// the column's textual representation. No wildcard character is required.
    /// </summary>
    public string? Search { get; set; }
}

/// <summary>Contains one bounded list-of-values lookup over the submitted report document.</summary>
/// <param name="Table">The canonical active table identifier.</param>
/// <param name="Column">The canonical logical column identifier.</param>
/// <param name="Type">The column's protocol type name.</param>
/// <param name="Items">Up to <see cref="ReportExecutor.MaxLovItems"/> distinct values.</param>
/// <param name="Truncated">Whether another matching distinct value exists beyond <paramref name="Items"/>.</param>
public sealed record ReportLovResult(
    string Table,
    string Column,
    string Type,
    IReadOnlyList<object?> Items,
    bool Truncated);
