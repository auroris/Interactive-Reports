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
    /// not presentable. Applied at schema discovery (case-insensitive), so every label
    /// consumer — grid headers, aggregate labels, CSV headers — sees the friendly name
    /// while expressions and state keep using the real column name. Entries matching
    /// no discovered column are inert (logged once), so schema drift cannot break the
    /// report. Report states may override per column via their own labels map.
    /// </summary>
    public Dictionary<string, string>? ColumnLabels { get; set; }

    /// <summary>
    /// Server-resolved parameters (claims by default). Client-supplied values can never
    /// bind to these — they are a separate parameter class from filter values. This is
    /// the row-level security mechanism (the :APP_USER pattern).
    /// </summary>
    public Dictionary<string, ContextParamSpec>? ContextParams { get; set; }

    public ReportAuthorization? Authorization { get; set; }

    /// <summary>Hard cap on rows any composed query may return (exports included).</summary>
    public int MaxRows { get; set; } = 100_000;

    public int DefaultPageSize { get; set; } = 50;

    public int MaxPageSize { get; set; } = 500;

    /// <summary>Cap on distinct pivot-column combinations the pivot view may produce.</summary>
    public int MaxPivotColumns { get; set; } = 60;

    /// <summary>
    /// Cap on points the chart view may draw. Exceeding it is a precise validation
    /// error, never truncation — a silently truncated chart (a pie especially)
    /// misrepresents the data it claims to show.
    /// </summary>
    public int MaxChartPoints { get; set; } = 1000;

    public int CommandTimeoutSeconds { get; set; } = 30;

    /// <summary>The developer's default view (APEX "Primary Report").</summary>
    public ReportState? DefaultState { get; set; }
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
}
