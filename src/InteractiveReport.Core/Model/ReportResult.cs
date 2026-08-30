namespace InteractiveReport.Core.Model;

/// <summary>Response shape for a report query.</summary>
public sealed class ReportResult
{
    /// <summary>
    /// The submitted report document with every null table-schema cache refreshed by
    /// the server. Cached schemas remain advisory and are never used for binding.
    /// </summary>
    public ReportState? Document { get; set; }

    /// <summary>Every terminal-table column available to subsequent UI actions.</summary>
    public required IReadOnlyList<ColumnInfo> AvailableColumns { get; init; }

    public required IReadOnlyList<ColumnInfo> Columns { get; init; }

    /// <summary>Rows as objects keyed by column name — page-granularity size cost is negligible, ergonomics win.</summary>
    public required IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows { get; init; }

    public required PageRequest Page { get; init; }

    /// <summary>Total rows in the whole filtered set (never just the visible page).</summary>
    public required long TotalRows { get; init; }

    /// <summary>Column → aggregate-fn → value, computed over the whole filtered set.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> Aggregates { get; init; }
        = new Dictionary<string, IReadOnlyDictionary<string, object?>>();

    /// <summary>One entry per break group, ordered like the page rows.</summary>
    public IReadOnlyList<BreakTotal> BreakTotals { get; init; } = [];

    /// <summary>
    /// True when the final visible row's control-break group continues into the next
    /// page. Clients must defer that group's subtotal until its logical end.
    /// </summary>
    public bool BreakContinues { get; init; }

    public IReadOnlyList<HighlightHit> Highlights { get; init; } = [];

    /// <summary>
    /// State elements referencing columns that no longer exist (or features not yet
    /// implemented) are dropped and reported here — saved reports degrade, never 500.
    /// </summary>
    public required IReadOnlyList<IgnoredItem> Ignored { get; init; }

    public long ElapsedMs { get; init; }
}

public sealed record ColumnInfo(string Name, string Label, string Type, bool Computed)
{
    /// <summary>
    /// Immediate input column whose inherited presentation mask applies to this
    /// result column. Null means <see cref="Name"/>. Each shape boundary advances
    /// this identity one output at a time, so sibling columns that share an original
    /// source cannot exchange masks.
    /// </summary>
    public string? FormatSource { get; init; }

    /// <summary>
    /// Stable metric identity for a data-derived Pivot cell. Explicit Pivot metrics
    /// use their authored value id; implicit count cells use <c>__count</c>. This is
    /// advisory result/schema metadata, not authored composable state.
    /// </summary>
    public string? PivotMetricId { get; init; }
}

/// <summary>Totals for one control-break group: the break-column values, row count, and per-column aggregates.</summary>
public sealed record BreakTotal(
    IReadOnlyDictionary<string, object?> Key,
    long Rows,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> Aggregates);

public sealed record HighlightHit(int Row, string Id, string? Col);

public sealed record IgnoredItem(string Kind, string Detail);
