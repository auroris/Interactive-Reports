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
    /// These source-controlled grants are additive with database administrators
    /// created in the administration center. When either source contains entries, the
    /// union is authoritative and application authorization may only restrict it.
    /// With neither source populated, an affirmative application authorization
    /// decision may supply administrator authority; otherwise actions fail closed.
    /// </summary>
    public List<string> Administrators { get; set; } = [];

    /// <summary>
    /// Optional explicit claim type for the canonical identity value. Default chain:
    /// NameIdentifier → "sub" → Identity.Name.
    /// </summary>
    public string? IdentityClaim { get; set; }

    public SavedReportsOptions SavedReports { get; set; } = new();

    /// <summary>
    /// Database authorization storage. It always uses the resolved SavedReports
    /// connection and dialect so authorization rows live beside saved reports.
    /// </summary>
    public AuthorizationStoreOptions Authorization { get; set; } = new();

    /// <summary>
    /// Serve the packaged browser pages — GET {prefix}/{name}/view and
    /// GET {prefix}/admin. On by default; the pages are anonymous shells (the data
    /// endpoints keep their own authorization), so disabling them only matters to
    /// hosts that author every page themselves.
    /// </summary>
    public bool ViewerPagesEnabled { get; set; } = true;
}

public sealed class AuthorizationStoreOptions
{
    /// <summary>
    /// Base name of the authorization table on the saved-report connection. The
    /// SavedReports table prefix, when present, is prepended to this value.
    /// </summary>
    public string TableName { get; set; } = "IR_REPORT_AUTHORIZATION";
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
    /// <see cref="Connection"/>, not both. Persistence and administration storage is
    /// unavailable when neither value is configured; installing the package never
    /// creates a local database implicitly.
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

    /// <summary>
    /// Create the saved-report and adjacent authorization tables automatically if
    /// missing. Disable if DDL is operator-managed.
    /// </summary>
    public bool AutoCreate { get; set; } = true;

    /// <summary>
    /// Optional prefix prepended to both the saved-report and authorization table
    /// names. For example, APP_ produces APP_IR_SAVED_REPORTS and
    /// APP_IR_REPORT_AUTHORIZATION with the default base names.
    /// </summary>
    public string TablePrefix { get; set; } = "";

    /// <summary>Base table name, after <see cref="TablePrefix"/>.</summary>
    public string TableName { get; set; } = "IR_SAVED_REPORTS";
}
