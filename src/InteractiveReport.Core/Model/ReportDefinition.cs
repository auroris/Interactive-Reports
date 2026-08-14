namespace InteractiveReport.Core.Model;

/// <summary>
/// A developer-owned report definition. Lives server-side (configuration in v1),
/// referenced by friendly name; the base SQL never crosses the network.
/// </summary>
public sealed class ReportDefinition
{
    /// <summary>Set by the definition store from its key; not part of the config payload.</summary>
    public string Name { get; set; } = "";

    public string? Title { get; set; }

    /// <summary>Named connection resolved through IReportConnectionFactory.</summary>
    public string Connection { get; set; } = "";

    public ReportDialect Dialect { get; set; }

    /// <summary>
    /// Optional session timezone (a region name like "Pacific/Auckland" or an offset
    /// like "+13:00"), pinned when the connection opens on engines that have session
    /// timezones — Oracle (ALTER SESSION) and Postgres (SET TIME ZONE) — so NOW()
    /// follows it. Deliberately ignored on SqlServer and Sqlite, whose clock is the
    /// server's own. Null means the server's setting. Note Oracle pools keep session
    /// state: definitions sharing a named connection should agree on this value.
    /// </summary>
    public string? TimeZone { get; set; }

    /// <summary>
    /// The base SELECT. Composed as a derived table (ir_base), so it must not end with
    /// ORDER BY. Context parameter placeholders use the dialect's native style
    /// (@name on SqlServer/Sqlite, :name on Oracle). Placeholder names matching
    /// p0/p1/... are reserved for the composer.
    /// </summary>
    public string Sql { get; set; } = "";

    /// <summary>
    /// Column name → friendly display label, for base queries whose column names are
    /// not presentable. Never applied to the engine's schema or query results: it is
    /// delivered to the client as the labels of the default report the schema endpoint
    /// sends down (an effective Default state with its own labels wins), and it plays
    /// the same default-report role as the bottom layer of document-label resolution
    /// at ingestion, so an unlabeled export matches an untouched client. Column
    /// references crossing the wire always use the real name.
    /// </summary>
    public Dictionary<string, string>? ColumnLabels { get; set; }

    /// <summary>
    /// Server-resolved parameters (claims by default). Client-supplied values can never
    /// bind to these — they are a separate parameter class from filter values. This is
    /// the row-level security mechanism (the :APP_USER pattern).
    /// </summary>
    public Dictionary<string, ContextParamSpec>? ContextParams { get; set; }

    public ReportAuthorization? Authorization { get; set; }

    /// <summary>
    /// Whitelist of end-user features (tokens in <see cref="ReportFeatures"/>). Null —
    /// the default — enables everything. When present, only the listed features exist:
    /// the client hides the rest of its chrome, and the server refuses the two that
    /// persist or egress data (download at the export endpoint, savedReports at
    /// saved-report creation). The other tokens are presentation-level only — the query
    /// endpoint still accepts any valid state document, because hiding a dialog is not
    /// a data-security boundary (context params are, §12). Note the JSON config binder
    /// cannot represent an empty array ([] binds as absent = everything); to lock a
    /// report down, list the one or two features it should keep.
    /// </summary>
    public List<string>? Features { get; set; }

    /// <summary>
    /// Hard export cap and upper bound for numeric page-size configuration. The
    /// explicit All page choice is deliberately unbounded.
    /// </summary>
    public int MaxRows { get; set; } = 100_000;

    public int DefaultPageSize { get; set; } = 50;

    public int MaxPageSize { get; set; } = 1000;

    /// <summary>Cap on distinct pivot-column combinations the pivot view may produce.</summary>
    public int MaxPivotColumns { get; set; } = 60;

    /// <summary>
    /// Cap on points the chart view may draw. Exceeding it is a precise validation
    /// error, never truncation — a silently truncated chart (a pie especially)
    /// misrepresents the data it claims to show.
    /// </summary>
    public int MaxChartPoints { get; set; } = 1000;

    public int CommandTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// The developer's generated Default view. An administrator-controlled primary
    /// saved report titled "Default" replaces it until that report is unflagged.
    /// </summary>
    public ReportState? DefaultState { get; set; }

    /// <summary>
    /// Report-document JSON files, resolved relative to the host content root unless
    /// absolute. Files are exposed as global, read-only saved reports. Their primary
    /// value seeds the stored administrator-controlled flag on first synchronization;
    /// configured documents take precedence over database reports with the same title.
    /// </summary>
    public List<string>? DocumentFiles { get; set; }

    /// <summary>
    /// Optional application-controlled stylesheet URL. The report component places a
    /// link to it inside its shadow root, after the packaged styles, so report-specific
    /// rules can reach the component without accepting CSS from report documents.
    /// Relative URLs resolve against the host page.
    /// </summary>
    public string? StyleSheet { get; set; }
}

public sealed class ContextParamSpec
{
    /// <summary>Claim type to resolve from the authenticated user.</summary>
    public string? Claim { get; set; }
}

/// <summary>
/// Default-deny: absent block ⇒ authenticated users only. Anonymous access requires
/// the explicit opt-in. The lazy path is the safe path.
/// </summary>
public sealed class ReportAuthorization
{
    public string? Policy { get; set; }
    public bool AllowAnonymous { get; set; }

    /// <summary>
    /// Restricts the report to identities in InteractiveReport:Administrators —
    /// non-administrators receive 404, matching the saved-report admin surface.
    /// A policy may stack on top. Contradicts AllowAnonymous (rejected at load).
    /// </summary>
    public bool AdministratorsOnly { get; set; }
}
