using InteractiveReport.Core.Model;

namespace InteractiveReport.AspNetCore;

/// <summary>
/// Options bound from the <c>InteractiveReport</c> configuration section. Config-declared default
/// states bind fully, expression-rule lists included (filters/computed/highlights are
/// string expressions since the M7 pipeline — the old typed-value caveat is gone).
/// </summary>
public sealed class InteractiveReportOptions
{
    /// <summary>Gets or sets executable report definitions keyed by case-insensitive route name.</summary>
    public Dictionary<string, ReportDefinition> Reports { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets or sets whether GET {prefix}/whoami exposes the exact identity value an operator should
    /// put in Administrators. Off by default; enable deliberately.
    /// </summary>
    public bool WhoamiEnabled { get; set; }

    /// <summary>
    /// Gets or sets identity values, as resolved by ReportIdentity and shown by whoami, granted
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
    /// Gets or sets the optional explicit claim type for the canonical identity value. The default chain is
    /// NameIdentifier → "sub" → Identity.Name.
    /// </summary>
    public string? IdentityClaim { get; set; }

    /// <summary>Gets or sets saved-report persistence configuration.</summary>
    public SavedReportsOptions SavedReports { get; set; } = new();

    /// <summary>
    /// Gets or sets database authorization storage. It always uses the resolved SavedReports
    /// connection and dialect so authorization rows live beside saved reports.
    /// </summary>
    public AuthorizationStoreOptions Authorization { get; set; } = new();

    /// <summary>
    /// Gets or sets whether to serve the packaged browser pages at GET {prefix}/{name}/view and
    /// GET {prefix}/admin. On by default; the pages are anonymous shells (the data
    /// endpoints keep their own authorization), so disabling them only matters to
    /// hosts that author every page themselves.
    /// </summary>
    public bool ViewerPagesEnabled { get; set; } = true;
}

/// <summary>Configures the database table used for report authorization rows.</summary>
public sealed class AuthorizationStoreOptions
{
    /// <summary>
    /// Gets or sets the base name of the authorization table on the saved-report connection. The
    /// SavedReports table prefix, when present, is prepended to this value.
    /// </summary>
    public string TableName { get; set; } = "IR_REPORT_AUTHORIZATION";
}

/// <summary>
/// Configures saved-report storage. The dialect is always derived from the target connection;
/// it is not configured here.
/// </summary>
public sealed class SavedReportsOptions
{
    /// <summary>
    /// Gets or sets a data source for saved-report storage: a ConnectionStrings name without <c>=</c> or a
    /// literal connection string, exactly as on a report definition. Set this or
    /// <see cref="Connection"/>, not both. Persistence and administration storage is
    /// unavailable when neither value is configured; installing the package never
    /// creates a local database implicitly.
    /// </summary>
    public string? DataSource { get; set; }

    /// <summary>Gets or sets the SQLite, sqlServer, PostgreSQL, or oracle provider token for <see cref="DataSource"/>.</summary>
    public string? Provider { get; set; }

    /// <summary>
    /// Gets or sets a named connection registered through AddConnection. This is the
    /// programmatic alternative to <see cref="DataSource"/>. Point either at your
    /// data connection to keep saved reports in the same database as the report data.
    /// </summary>
    public string? Connection { get; set; }

    /// <summary>
    /// Gets or sets whether missing saved-report and authorization tables are created automatically.
    /// Disable this when DDL is operator-managed.
    /// </summary>
    public bool AutoCreate { get; set; } = true;

    /// <summary>
    /// Gets or sets the optional prefix prepended to both the saved-report and authorization table
    /// names. For example, APP_ produces APP_IR_SAVED_REPORTS and
    /// APP_IR_REPORT_AUTHORIZATION with the default base names.
    /// </summary>
    public string TablePrefix { get; set; } = "";

    /// <summary>Gets or sets the saved-report base table name to which <see cref="TablePrefix"/> is prepended.</summary>
    public string TableName { get; set; } = "IR_SAVED_REPORTS";
}
