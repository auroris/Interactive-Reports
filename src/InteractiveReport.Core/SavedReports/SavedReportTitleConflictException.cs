namespace InteractiveReport.Core.SavedReports;

/// <summary>
/// Indicates that the store's title-uniqueness backstop found a user-origin row with the same
/// report name and normalized title already exists. The endpoint layer translates
/// this into the same 409 its advisory pre-check produces, closing the check-then-
/// insert race window without changing the response contract.
/// </summary>
/// <param name="reportName">The report definition whose saved-report namespace contains the conflict.</param>
/// <param name="title">The conflicting display title.</param>
/// <param name="inner">The database exception raised by the unique constraint.</param>
public sealed class SavedReportTitleConflictException(
    string reportName,
    string title,
    Exception inner)
    : InvalidOperationException(
        $"A saved report of '{reportName}' titled '{title}' already exists.",
        inner)
{
    /// <summary>Gets the report definition containing the conflicting title.</summary>
    public string ReportName { get; } = reportName;

    /// <summary>Gets the title rejected by the store.</summary>
    public string Title { get; } = title;
}
