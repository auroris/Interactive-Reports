using InteractiveReport.Core.Model;

namespace InteractiveReport.AspNetCore;

/// <summary>
/// Bound from the "InteractiveReport" configuration section. Config-declared default
/// states bind fully, expression-rule lists included (filters/computed/highlights are
/// string expressions since the M7 pipeline — the old typed-value caveat is gone).
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
    /// A nonempty list is authoritative and application authorization may only
    /// restrict it. When empty, actions requiring administrator authority need an affirmative
    /// application authorization decision and otherwise fail closed.
    /// </summary>
    public List<string> Administrators { get; set; } = [];

    /// <summary>
    /// Optional explicit claim type for the canonical identity value. Default chain:
    /// NameIdentifier → "sub" → Identity.Name.
    /// </summary>
    public string? IdentityClaim { get; set; }

    public SavedReportsOptions SavedReports { get; set; } = new();

    /// <summary>
    /// Serve the packaged browser pages — GET {prefix}/{name}/view and
    /// GET {prefix}/admin. On by default; the pages are anonymous shells (the data
    /// endpoints keep their own authorization), so disabling them only matters to
    /// hosts that author every page themselves.
    /// </summary>
    public bool ViewerPagesEnabled { get; set; } = true;
}

/// <summary>
/// Saved-report storage. The dialect is always derived from the target connection —
/// it is not configured here.
/// </summary>
public sealed class SavedReportsOptions
{
    /// <summary>
    /// Data source for saved-report storage: a ConnectionStrings name (no '=') or a
    /// literal connection string, exactly as on a report definition. Set this or
    /// <see cref="Connection"/>, not both. Null/absent with no Connection = the
    /// zero-config default: a local SQLite database under App_Data.
    /// </summary>
    public string? DataSource { get; set; }

    /// <summary>Provider token for <see cref="DataSource"/> (sqlite, sqlServer, postgres, oracle); same resolution rules as a report definition's.</summary>
    public string? Provider { get; set; }

    /// <summary>
    /// Named connection (see AddConnection) for saved-report storage — the
    /// programmatic alternative to <see cref="DataSource"/>. Point either at your
    /// data connection to keep saved reports in the same database as the report data.
    /// </summary>
    public string? Connection { get; set; }

    /// <summary>Create the table automatically if missing. Disable if DDL is operator-managed.</summary>
    public bool AutoCreate { get; set; } = true;

    public string TableName { get; set; } = "IR_SAVED_REPORTS";
}
