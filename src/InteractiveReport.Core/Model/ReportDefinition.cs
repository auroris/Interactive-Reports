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

    /// <summary>
    /// Named connection resolved through IReportConnectionFactory. Registered in code
    /// (AddConnection) — the programmatic alternative to <see cref="DataSource"/>;
    /// a definition sets exactly one of the two.
    /// </summary>
    public string Connection { get; set; } = "";

    /// <summary>
    /// The report's data source: a value containing '=' is a literal ADO.NET
    /// connection string; a value without '=' is the name of an entry under the
    /// standard ConnectionStrings configuration section (a missing name is a
    /// configuration error — it is never treated as a literal).
    /// </summary>
    public string? DataSource { get; set; }

    /// <summary>
    /// ADO.NET provider token for <see cref="DataSource"/>: sqlite, sqlServer,
    /// postgres, or oracle. Optional when the data source is a ConnectionStrings
    /// name with a {name}_ProviderName companion entry (the Umbraco/legacy
    /// convention); required for literal connection strings.
    /// </summary>
    public string? Provider { get; set; }

    /// <summary>
    /// The SQL dialect. Derived from the data source or connection — the definition
    /// store stamps it before execution, superseding any configured value (dialect is
    /// a property of the connection, not a per-report choice). Hosts that resolve
    /// definitions themselves must assign it before execution.
    /// </summary>
    public ReportDialect? Dialect { get; set; }

    /// <summary>The resolved dialect; execution surfaces call this, never the raw property.</summary>
    public ReportDialect GetEffectiveDialect()
        => Dialect ?? throw new InvalidOperationException(
            $"Report '{Name}': dialect is unresolved — resolve it from the report's connection before execution.");

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
    /// Hard row cap for unpaged grid/group queries and exports, and an upper bound
    /// for numeric page-size configuration. Zero or a negative value means unlimited.
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
    /// Consistency policy for report paths that require more than one query. None is
    /// the default and performs no transaction setup. Snapshot asks the provider for
    /// a stable view and fails explicitly when that guarantee is not available; it
    /// never silently falls back to independent statements or a different strategy.
    /// </summary>
    public ReportConsistency Consistency { get; set; } = ReportConsistency.None;

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

    /// <summary>
    /// APEX-style per-row edit pencil, rendered by the client as a leading synthetic
    /// grid column. Definition chrome like styleSheet — not report state and not a
    /// feature token; configuring it is what enables it.
    /// </summary>
    public ReportEditLink? EditLink { get; set; }

    /// <summary>
    /// Per-column presentation and behavior overrides, keyed by base column name
    /// (case-insensitive). Unknown names are tolerated like columnLabels (schema
    /// drift); labels here supersede columnLabels, and configuring the same column's
    /// label in both maps is rejected at load.
    /// </summary>
    public Dictionary<string, ReportColumnOverride>? Columns { get; set; }

    /// <summary>
    /// columnLabels overlaid with columns[*].label (overrides win). Null when neither
    /// map contributes a label — callers keep their existing null-means-absent paths.
    /// </summary>
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
/// The definition's edit pencil. The URL template's {COLUMN} placeholders reference
/// base schema columns; the referenced values are projected as hidden row data and
/// substituted client-side with URL encoding, so no markup ever crosses the wire.
/// </summary>
public sealed class ReportEditLink
{
    /// <summary>
    /// URL template, e.g. "/orders/{ORDER_ID}/edit". At least one placeholder is
    /// required; a row whose placeholder value is null renders no pencil.
    /// </summary>
    public string UrlTemplate { get; set; } = "";

    /// <summary>Accessible name and tooltip of the pencil. Default "Edit".</summary>
    public string? Label { get; set; }

    /// <summary>"_self" (default) or "_blank" (the client adds rel="noopener").</summary>
    public string? Target { get; set; }
}

/// <summary>One column's developer-set overrides. Absent properties change nothing.</summary>
public sealed class ReportColumnOverride
{
    /// <summary>Display-name override; supersedes columnLabels. Blank is rejected.</summary>
    public string? Label { get; set; }

    /// <summary>
    /// Render the table header cell without visible text (the accessible name and
    /// every menu, dialog, and picker keep the real label) — the APEX empty-heading
    /// pattern without the ambiguity of a report full of unnameable columns.
    /// </summary>
    public bool? HideLabel { get; set; }

    /// <summary>
    /// False removes the column's sort controls (and control breaks, which imply
    /// sorting); the server strips violating state into ignored[]. Null = allowed.
    /// </summary>
    public bool? Sortable { get; set; }

    /// <summary>
    /// False removes the column's filter controls; filter rules referencing the
    /// column are stripped into ignored[]. Null = allowed.
    /// </summary>
    public bool? Filterable { get; set; }

    /// <summary>Shown as a note at the bottom of the column's header menu.</summary>
    public string? HelpText { get; set; }
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
    /// Restricts this report to explicitly granted identities. Configuration grants
    /// in <see cref="Users"/> and database grants made in the administration center
    /// are additive. A database restriction marker can also enable this gate.
    /// </summary>
    public bool Restricted { get; set; }

    /// <summary>
    /// Canonical identity values granted access when the report is restricted. These
    /// source-controlled grants are additive with administration-center grants.
    /// </summary>
    public List<string> Users { get; set; } = [];

    /// <summary>
    /// Restricts the report to configured or database administrators;
    /// non-administrators receive 404, matching the saved-report admin surface. If
    /// both administrator stores are empty, the application operation authorizer must
    /// affirmatively grant each request. A policy may stack on top. Contradicts
    /// AllowAnonymous and named-user restriction (rejected at load).
    /// </summary>
    public bool AdministratorsOnly { get; set; }
}
