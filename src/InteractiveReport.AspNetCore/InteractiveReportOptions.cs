using InteractiveReport.Core.Model;

namespace InteractiveReport.AspNetCore;

/// <summary>
/// Bound from the "InteractiveReport" configuration section. Note: config-declared
/// default states support sorts/columns/page; filters with values belong to saved
/// states (JSON documents), not to the configuration binder.
/// </summary>
public sealed class InteractiveReportOptions
{
    public Dictionary<string, ReportDefinition> Reports { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Exposes GET {prefix}/whoami so an operator can see the exact identity value to
    /// put in Administrators. Off by default; enable deliberately.
    /// </summary>
    public bool WhoamiEnabled { get; set; }

    /// <summary>
    /// Identity values (as resolved by ReportIdentity / shown by whoami) granted
    /// administrator rights: list all saved reports, publish/unpublish globals,
    /// reassign or delete anyone's saved reports. Case-insensitive exact match.
    /// </summary>
    public List<string> Administrators { get; set; } = [];

    /// <summary>
    /// Optional explicit claim type for the canonical identity value. Default chain:
    /// NameIdentifier → "sub" → Identity.Name.
    /// </summary>
    public string? IdentityClaim { get; set; }

    public SavedReportsOptions SavedReports { get; set; } = new();
}

public sealed class SavedReportsOptions
{
    /// <summary>
    /// Named connection (see AddConnection) for saved-report storage. Null = the
    /// zero-config default: a local SQLite database under App_Data. Point it at your
    /// data connection to keep saved reports in the same database as the report data.
    /// </summary>
    public string? Connection { get; set; }

    /// <summary>Dialect of the saved-report connection. Only relevant when Connection is set.</summary>
    public ReportDialect Dialect { get; set; } = ReportDialect.Sqlite;

    /// <summary>Create the table automatically if missing. Disable if DDL is operator-managed.</summary>
    public bool AutoCreate { get; set; } = true;

    public string TableName { get; set; } = "IR_SAVED_REPORTS";
}
