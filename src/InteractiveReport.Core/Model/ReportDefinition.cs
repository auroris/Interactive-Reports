namespace InteractiveReport.Core.Model;

/// <summary>
/// Defines a developer-owned report that lives server-side and is referenced by friendly name.
/// The base SQL never crosses the network.
/// </summary>
public sealed class ReportDefinition
{
    /// <summary>Gets or sets the canonical name assigned by the definition store; it is not part of the configuration payload.</summary>
    public string Name { get; set; } = "";

    /// <summary>Gets or sets the optional display title; clients prettify <see cref="Name"/> when absent.</summary>
    public string? Title { get; set; }

    /// <summary>
    /// Gets or sets the named connection resolved through <c>IReportConnectionFactory</c>. It is registered in code
    /// (AddConnection) — the programmatic alternative to <see cref="DataSource"/>;
    /// a definition sets exactly one of the two.
    /// </summary>
    public string Connection { get; set; } = "";

    /// <summary>
    /// Gets or sets the report's data source. A value containing '=' is a literal ADO.NET
    /// connection string; a value without '=' is the name of an entry under the
    /// standard ConnectionStrings configuration section (a missing name is a
    /// configuration error — it is never treated as a literal).
    /// </summary>
    public string? DataSource { get; set; }

    /// <summary>
    /// Gets or sets the ADO.NET provider token for <see cref="DataSource"/>: SQLite, sqlServer,
    /// PostgreSQL, or oracle. Optional when the data source is a ConnectionStrings
    /// name with a {name}_ProviderName companion entry (the Umbraco/legacy
    /// convention); required for literal connection strings.
    /// </summary>
    public string? Provider { get; set; }

    /// <summary>
    /// Gets or sets the SQL dialect derived from the data source or connection. The definition
    /// store stamps it before execution, superseding any configured value (dialect is
    /// a property of the connection, not a per-report choice). Hosts that resolve
    /// definitions themselves must assign it before execution.
    /// </summary>
    public ReportDialect? Dialect { get; set; }

    /// <summary>
    /// Returns the resolved dialect required by execution surfaces.
    /// </summary>
    /// <returns>The non-null dialect assigned during definition resolution.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the definition has not been resolved against a connection.</exception>
    public ReportDialect GetEffectiveDialect()
        => Dialect ?? throw new InvalidOperationException(
            $"Report '{Name}': dialect is unresolved — resolve it from the report's connection before execution.");

    /// <summary>
    /// Gets or sets an optional session timezone, such as <c>Pacific/Auckland</c> or <c>+13:00</c>,
    /// pinned when the connection opens on engines that have session timezones — Oracle
    /// (<c>ALTER SESSION</c>) and PostgreSQL (<c>SET TIME ZONE</c>). This affects
    /// developer SQL and database conversions; portable-expression NOW() is instead
    /// one request-scoped UTC value. Deliberately ignored on SqlServer and SQLite.
    /// Null means the server's setting. Note Oracle pools keep session state:
    /// definitions sharing a named connection should agree on this value.
    /// </summary>
    public string? TimeZone { get; set; }

    /// <summary>
    /// Gets or sets the base SELECT. It is composed as a derived table (<c>ir_base</c>), so it must not end with
    /// ORDER BY. Context parameter placeholders use the dialect's native style
    /// (@name on SqlServer/SQLite, :name on Oracle). Placeholder names matching
    /// p0/p1/... are reserved for the composer.
    /// </summary>
    public string Sql { get; set; } = "";

    /// <summary>
    /// Gets or sets base-column friendly labels for queries whose column names are
    /// not presentable. Never applied to the engine's schema or query results: it is
    /// delivered to the client as the labels of the default report the schema endpoint
    /// sends down (an effective Default state with its own labels wins), and it plays
    /// the same default-report role as the bottom layer of document-label resolution
    /// at ingestion, so an unlabeled export matches an untouched client. Column
    /// references crossing the wire always use the real name.
    /// </summary>
    public Dictionary<string, string>? ColumnLabels { get; set; }

    /// <summary>
    /// Gets or sets server-resolved parameters, claims by default. Client-supplied values can never
    /// bind to these — they are a separate parameter class from filter values. This is
    /// the row-level security mechanism (the :APP_USER pattern).
    /// </summary>
    public Dictionary<string, ContextParamSpec>? ContextParams { get; set; }

    /// <summary>Gets or sets report-level authentication, policy, administrator, restriction, and configured-user rules.</summary>
    public ReportAuthorization? Authorization { get; set; }

    /// <summary>
    /// Gets or sets the whitelist of end-user feature tokens from <see cref="ReportFeatures"/>. Null
    /// the default — enables everything. When present, only the listed features exist:
    /// the client hides the rest of its chrome, and the server refuses the two that
    /// persist or egress data (download at the file-client endpoint, savedReports at
    /// saved-report creation). The other tokens are presentation-level only — the query
    /// endpoint still accepts any valid state document, because hiding a dialog is not
    /// a data-security boundary (trusted context parameters are). Note the JSON config binder
    /// cannot represent an empty array ([] binds as absent = everything); to lock a
    /// report down, list the one or two features it should keep.
    /// </summary>
    public List<string>? Features { get; set; }

    /// <summary>
    /// Gets or sets the hard row cap for unpaged grid/group queries and exports, and an upper bound
    /// for numeric page-size configuration. Zero or a negative value means unlimited.
    /// </summary>
    public int MaxRows { get; set; } = 100_000;

    /// <summary>Gets or sets the positive page size used when a request does not supply paging.</summary>
    public int DefaultPageSize { get; set; } = 50;

    /// <summary>Gets or sets the largest positive page size a request may select.</summary>
    public int MaxPageSize { get; set; } = 1000;

    /// <summary>Gets or sets the cap on distinct pivot-column combinations a pivot view may produce.</summary>
    public int MaxPivotColumns { get; set; } = 60;

    /// <summary>
    /// Gets or sets the cap on points a chart view may draw. Exceeding it is a precise validation
    /// error, never truncation — a silently truncated chart (a pie especially)
    /// misrepresents the data it claims to show.
    /// </summary>
    public int MaxChartPoints { get; set; } = 1000;

    /// <summary>Gets or sets the positive timeout applied to every database command, in seconds.</summary>
    public int CommandTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Consistency selects the read policy for report paths that require more than one query. None is
    /// the default and performs no transaction setup. Snapshot asks the provider for
    /// a stable view and fails explicitly when that guarantee is not available; it
    /// never silently falls back to independent statements or a different strategy.
    /// </summary>
    public ReportConsistency Consistency { get; set; } = ReportConsistency.None;

    /// <summary>
    /// Gets or sets the developer-owned state used to create the synthetic default or repair a database-backed default.
    /// </summary>
    public ReportState? DefaultState { get; set; }

    /// <summary>
    /// Gets or sets report-document JSON files, resolved relative to the host content root unless
    /// absolute. Files are exposed as global, read-only saved reports. Their default
    /// value seeds the durable default flag when first synchronized. Configured titles
    /// are deployment declarations and may collide with any configured or database report.
    /// </summary>
    public List<string>? DocumentFiles { get; set; }

    /// <summary>
    /// Gets or sets an APEX-style per-row edit pencil, rendered by the client as a leading synthetic
    /// grid column. This is definition chrome, not report state and not a feature token;
    /// configuring it is what enables it.
    /// </summary>
    public ReportEditLink? EditLink { get; set; }

    /// <summary>
    /// Gets or sets per-column presentation and behavior overrides, keyed by base column name
    /// (case-insensitive). Unknown names are tolerated like columnLabels (schema
    /// drift); labels here supersede columnLabels, and configuring the same column's
    /// label in both maps is rejected at load.
    /// </summary>
    public Dictionary<string, ReportColumnOverride>? Columns { get; set; }

    /// <summary>
    /// Merges <see cref="ColumnLabels"/> with <see cref="Columns"/> labels, with column overrides winning.
    /// Returns <see langword="null"/> when neither map contributes a label, preserving
    /// the public null-means-absent contract.
    /// </summary>
    /// <returns>A detached case-insensitive label map, or <see langword="null"/> when neither source contributes a label.</returns>
    public Dictionary<string, string>? GetEffectiveColumnLabels()
    {
        Dictionary<string, string>? merged = null;
        if (ColumnLabels is { Count: > 0 })
            merged = new Dictionary<string, string>(ColumnLabels, StringComparer.OrdinalIgnoreCase);
        if (Columns is not null)
        {
            foreach (var (name, over) in Columns)
            {
                if (string.IsNullOrWhiteSpace(over?.Label)) continue;
                merged ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                merged[name] = over.Label;
            }
        }
        return merged;
    }
}

/// <summary>
/// Defines the report's per-row edit pencil. URL-template <c>{COLUMN}</c> placeholders reference
/// definition-schema columns; the referenced values are projected as hidden row data and
/// substituted client-side with URL encoding, so no markup ever crosses the wire.
/// </summary>
public sealed class ReportEditLink
{
    /// <summary>
    /// Gets or sets the URL template, for example <c>/orders/{ORDER_ID}/edit</c>. At least one placeholder is
    /// required; a row whose placeholder value is null renders no pencil.
    /// </summary>
    public string UrlTemplate { get; set; } = "";

    /// <summary>Gets or sets the accessible name and tooltip; the client defaults it to <c>Edit</c>.</summary>
    public string? Label { get; set; }

    /// <summary>Gets or sets <c>_self</c> by default or <c>_blank</c>; the client adds <c>rel="noopener"</c>.</summary>
    public string? Target { get; set; }
}

/// <summary>Contains developer-set overrides for one base column; absent properties change nothing.</summary>
public sealed class ReportColumnOverride
{
    /// <summary>Gets or sets a nonblank display-name override that supersedes <c>columnLabels</c>.</summary>
    public string? Label { get; set; }

    /// <summary>
    /// Gets or sets whether to render the table header cell without visible text. The accessible name and
    /// every menu, dialog, and picker keep the real label — the APEX empty-heading
    /// pattern without the ambiguity of a report full of unnameable columns.
    /// </summary>
    public bool? HideLabel { get; set; }

    /// <summary>
    /// Gets or sets whether the column may be sorted. False removes sort controls and control breaks, which imply
    /// sorting; the server strips violating state into <c>ignored</c>. Null means allowed.
    /// </summary>
    public bool? Sortable { get; set; }

    /// <summary>
    /// Gets or sets whether the column may be filtered. False removes filter controls, and rules referencing the
    /// column are stripped into <c>ignored</c>. Null means allowed.
    /// </summary>
    public bool? Filterable { get; set; }

    /// <summary>Gets or sets optional help text shown at the bottom of the column's header menu.</summary>
    public string? HelpText { get; set; }
}

/// <summary>Defines how one server-owned base-query context parameter is resolved.</summary>
public sealed class ContextParamSpec
{
    /// <summary>Gets or sets the authenticated-user claim type resolved for this context parameter.</summary>
    public string? Claim { get; set; }
}

/// <summary>
/// Defines report-level access. An absent block still requires authentication; anonymous access requires
/// the explicit opt-in. The lazy path is the safe path.
/// </summary>
public sealed class ReportAuthorization
{
    /// <summary>Gets or sets an optional ASP.NET Core authorization policy that must also succeed.</summary>
    public string? Policy { get; set; }
    /// <summary>Gets or sets whether this report explicitly permits unauthenticated callers.</summary>
    public bool AllowAnonymous { get; set; }

    /// <summary>
    /// Gets or sets whether this report is limited to explicitly granted identities. Configuration grants
    /// in <see cref="Users"/> and database grants made in the administration center
    /// are additive. A database restriction marker can also enable this gate.
    /// </summary>
    public bool Restricted { get; set; }

    /// <summary>
    /// Gets or sets canonical identity values granted access when the report is restricted. These
    /// source-controlled grants are additive with administration-center grants.
    /// </summary>
    public List<string> Users { get; set; } = [];

    /// <summary>
    /// Gets or sets whether the report is restricted to configured or database administrators;
    /// non-administrators receive 404, matching the saved-report admin surface. If
    /// both administrator stores are empty, the application operation authorizer must
    /// affirmatively grant each request. A policy may stack on top. Contradicts
    /// AllowAnonymous and named-user restriction (rejected at load).
    /// </summary>
    public bool AdministratorsOnly { get; set; }
}
