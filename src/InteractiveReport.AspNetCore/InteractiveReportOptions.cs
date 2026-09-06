using InteractiveReport.Core.Model;

namespace InteractiveReport.AspNetCore;

/// <summary>
/// Options bound from the <c>InteractiveReport</c> configuration section. Config-declared default
/// states bind fully, including string-expression filter, computed-column, and highlight rules.
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
    /// administrator rights: list all saved reports, publish/unpublish globals, reassign or delete
    /// anyone's saved reports, and manage the administrator list. Matched by ordinal,
    /// case-sensitive equality against the resolved identity value (use whoami to see the exact
    /// value). This list and the database grants made in the administration center are the
    /// built-in fallback; they are consulted only when the application has neither registered
    /// UseAdministrators nor configured <see cref="AdministratorPolicy"/>.
    /// </summary>
    public List<string> Administrators { get; set; } = [];

    /// <summary>
    /// Gets or sets the name of an ASP.NET Core authorization policy that decides who administers
    /// Interactive Reports. While it is set, the policy is the authority and
    /// <see cref="Administrators"/> and the administration center's database grants are ignored. A
    /// callback registered with UseAdministrators takes precedence over the policy. Leave it unset
    /// to keep the built-in fallbacks.
    /// </summary>
    public string? AdministratorPolicy { get; set; }

    /// <summary>
    /// Gets or sets the optional explicit claim type for the canonical identity value. The default chain is
    /// NameIdentifier → "sub" → Identity.Name.
    /// </summary>
    public string? IdentityClaim { get; set; }

    /// <summary>Gets or sets saved-report persistence configuration.</summary>
    public SavedReportsOptions SavedReports { get; set; } = new();

    /// <summary>
    /// Gets or sets the storage of database-authored administrator grants. It always uses the
    /// resolved SavedReports connection and dialect so the grants live beside saved reports.
    /// </summary>
    public AuthorizationStoreOptions Authorization { get; set; } = new();

    /// <summary>
    /// Gets or sets the limits of administration account lookups (GET {prefix}/admin/users), which
    /// merge the application user directory with the identities Interactive Reports already knows.
    /// </summary>
    public UserDirectoryOptions UserDirectory { get; set; } = new();

    /// <summary>
    /// Gets or sets whether to serve the packaged browser pages at GET {prefix}/{name}/view and
    /// GET {prefix}/admin. On by default; the pages are anonymous shells (the data
    /// endpoints keep their own authorization), so disabling them only matters to
    /// hosts that author every page themselves.
    /// </summary>
    public bool ViewerPagesEnabled { get; set; } = true;
}

/// <summary>Configures the database table that holds administrator grants.</summary>
public sealed class AuthorizationStoreOptions
{
    /// <summary>
    /// Gets or sets the base name of the administrator table on the saved-report connection. The
    /// SavedReports table prefix, when present, is prepended to this value.
    /// </summary>
    public string TableName { get; set; } = "IR_ADMINISTRATORS";
}

/// <summary>Configures administration account lookups.</summary>
public sealed class UserDirectoryOptions
{
    /// <summary>
    /// Gets or sets the most accounts one lookup returns, counting application-directory entries
    /// and identities already known to Interactive Reports together. Between 1 and 1000; 50 by
    /// default. Search text narrows the list before the limit applies.
    /// </summary>
    public int MaxResults { get; set; } = 50;

    /// <summary>
    /// Gets or sets how many seconds the application directory's answer to a no-search browse is
    /// reused for the same administrator before the directory is asked again. 60 by default; 0
    /// asks the directory on every lookup. Searched lookups are never reused, and the identities
    /// known from Interactive Reports storage are always read fresh.
    /// </summary>
    public int CacheSeconds { get; set; } = 60;
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
    /// APP_IR_ADMINISTRATORS with the default base names.
    /// </summary>
    public string TablePrefix { get; set; } = "";

    /// <summary>Gets or sets the saved-report base table name to which <see cref="TablePrefix"/> is prepended.</summary>
    public string TableName { get; set; } = "IR_SAVED_REPORTS";
}
