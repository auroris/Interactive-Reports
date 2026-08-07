namespace InteractiveReport.Core.Model;

/// <summary>
/// The source-controlled envelope stored in a report-document JSON file. The state
/// remains the same versioned document used by query, export, and saved reports;
/// title and primary only describe how the host publishes it.
/// </summary>
public sealed class ReportDocumentFile
{
    public string? Title { get; set; }

    /// <summary>
    /// At most one configured document per report may be primary. A primary document
    /// supplies the schema endpoint's default state and is not duplicated in the saved
    /// report selector.
    /// </summary>
    public bool Primary { get; set; }

    public ReportState? State { get; set; }
}
