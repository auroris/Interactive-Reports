namespace InteractiveReport.Core.Model;

/// <summary>
/// The source-controlled envelope stored in a report-document JSON file. The state
/// remains the same versioned document used by query, file download, and saved reports;
/// title and default flag describe how the host publishes it.
/// </summary>
public sealed class ReportDocumentFile
{
    /// <summary>
    /// Gets or sets the required configured display name. Configured names deliberately bypass
    /// user-document uniqueness rules and may collide with any other report document name.
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    /// Selects this file as the report family's default document. At most one configured
    /// file per report may set this flag. It takes precedence over the synthetic default.
    /// </summary>
    public bool Default { get; set; }

    /// <summary>Gets or sets the versioned report-state document to publish.</summary>
    public ReportState? State { get; set; }
}
