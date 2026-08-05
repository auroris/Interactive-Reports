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
    /// The base SELECT. Composed as a derived table (ir_base), so it must not end with
    /// ORDER BY. Context parameter placeholders use the dialect's native style
    /// (@name on SqlServer/Sqlite, :name on Oracle). Placeholder names matching
    /// p0/p1/... are reserved for the composer.
    /// </summary>
    public string Sql { get; set; } = "";

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
