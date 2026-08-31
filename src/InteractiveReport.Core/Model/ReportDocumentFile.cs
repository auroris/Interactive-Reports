namespace InteractiveReport.Core.Model;

/// <summary>
/// The source-controlled envelope stored in a report-document JSON file. The state
/// remains the same versioned document used by query, export, and saved reports;
/// title and primary only describe how the host initially publishes it.
/// </summary>
public sealed class ReportDocumentFile
{
    /// <summary>Gets or sets the initial display title; the synchronizer falls back to the configured report name when omitted.</summary>
    public string? Title { get; set; }

    /// <summary>
    /// Seeds the administrator-controlled primary flag when a configured document is
    /// first synchronized or when an administrator uploads the envelope. Subsequent
    /// flag changes live in the saved-report store and do not modify the source file.
    /// </summary>
    public bool Primary { get; set; }

    /// <summary>Gets or sets the versioned report-state document to publish.</summary>
    public ReportState? State { get; set; }
}
