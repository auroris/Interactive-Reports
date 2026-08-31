namespace InteractiveReport.Core.Model;

/// <summary>Contains the public data and metadata returned by one report query.</summary>
public sealed class ReportResult
{
    /// <summary>
    /// The submitted report document with every null table-schema cache refreshed by
    /// the server. Cached schemas remain advisory and are never used for binding.
    /// </summary>
    public ReportState? Document { get; set; }

    /// <summary>Gets every terminal-table column available to subsequent UI actions, including currently hidden columns.</summary>
    public required IReadOnlyList<ColumnInfo> AvailableColumns { get; init; }

    /// <summary>Gets the visible result columns in wire order.</summary>
    public required IReadOnlyList<ColumnInfo> Columns { get; init; }

    /// <summary>Gets the current page as row objects keyed by column name.</summary>
    public required IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows { get; init; }

    /// <summary>Gets the effective one-based page index and page size.</summary>
    public required PageRequest Page { get; init; }

    /// <summary>Gets the total rows in the whole filtered set, never just the visible page.</summary>
    public required long TotalRows { get; init; }

    /// <summary>Gets whole-filtered-set aggregates keyed by column and then aggregate function.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> Aggregates { get; init; }
        = new Dictionary<string, IReadOnlyDictionary<string, object?>>();

    /// <summary>Gets one subtotal entry per control-break group, ordered like the page rows.</summary>
    public IReadOnlyList<BreakTotal> BreakTotals { get; init; } = [];

    /// <summary>
    /// Gets whether the final visible row's control-break group continues into the next
    /// page. Clients must defer that group's subtotal until its logical end.
    /// </summary>
    public bool BreakContinues { get; init; }

    /// <summary>Gets the row and cell highlight rules matched by the returned page.</summary>
    public IReadOnlyList<HighlightHit> Highlights { get; init; } = [];

    /// <summary>
    /// Gets state elements that referenced columns that no longer exist or unsupported features. Such elements
    /// implemented) are dropped and reported here — saved reports degrade, never 500.
    /// </summary>
    public required IReadOnlyList<IgnoredItem> Ignored { get; init; }

    /// <summary>Gets the measured query execution time in milliseconds.</summary>
    public long ElapsedMs { get; init; }
}

/// <summary>Describes one public result column and its presentation lineage.</summary>
/// <param name="Name">The case-insensitive logical column identifier.</param>
/// <param name="Label">The display label.</param>
/// <param name="Type">The protocol type name.</param>
/// <param name="Computed">Whether the column was authored as a computed expression.</param>
public sealed record ColumnInfo(string Name, string Label, string Type, bool Computed)
{
    /// <summary>
    /// Gets the immediate input column whose inherited presentation mask applies to this
    /// result column. Null means <see cref="Name"/>. Each shape boundary advances
    /// this identity one output at a time, so sibling columns that share an original
    /// source cannot exchange masks.
    /// </summary>
    public string? FormatSource { get; init; }

    /// <summary>
    /// Gets the stable metric identity for a data-derived pivot cell. Explicit pivot metrics
    /// use their authored value id; implicit count cells use <c>__count</c>. This is
    /// advisory result/schema metadata, not authored composable state.
    /// </summary>
    public string? PivotMetricId { get; init; }
}

/// <summary>Captures one control-break group's key values, row count, and per-column aggregates.</summary>
/// <param name="Key">The break-column values identifying the group.</param>
/// <param name="Rows">The number of rows in the group.</param>
/// <param name="Aggregates">The group's aggregates keyed by column and function.</param>
public sealed record BreakTotal(
    IReadOnlyDictionary<string, object?> Key,
    long Rows,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> Aggregates);

/// <summary>Identifies one matched highlight rule for a returned row or cell.</summary>
/// <param name="Row">The zero-based row index within the returned page.</param>
/// <param name="Id">The stable highlight-rule identifier.</param>
/// <param name="Col">The target column for a cell highlight, or <see langword="null"/> for a row highlight.</param>
public sealed record HighlightHit(int Row, string Id, string? Col);

/// <summary>Explains one non-fatal report-state element omitted during validation or execution.</summary>
/// <param name="Kind">The protocol category of the omitted element.</param>
/// <param name="Detail">The client-facing reason it was omitted.</param>
public sealed record IgnoredItem(string Kind, string Detail);
