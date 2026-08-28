namespace InteractiveReport.Core.SavedReports;

/// <summary>
/// The store's title-uniqueness backstop fired: a user-origin row with the same
/// report name and normalized title already exists. The endpoint layer translates
/// this into the same 409 its advisory pre-check produces, closing the check-then-
/// insert race window without changing the response contract.
/// </summary>
public sealed class SavedReportTitleConflictException(
    string reportName,
    string title,
    Exception inner)
    : InvalidOperationException(
        $"A saved report of '{reportName}' titled '{title}' already exists.",
        inner)
{
    public string ReportName { get; } = reportName;
    public string Title { get; } = title;
}
