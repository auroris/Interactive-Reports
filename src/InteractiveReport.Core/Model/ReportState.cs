namespace InteractiveReport.Core.Model;

/// <summary>
/// The report-state document: simultaneously the query request body, the saved report,
/// and the shareable view state. Everything in it is data validated against the report's
/// discovered schema — never code. Its structure is self-describing; documents do not
/// carry a protocol version field.
/// </summary>
public sealed class ReportState
{
    /// <summary>Gets or sets toolbar search text, applied as case-insensitive contains across eligible input text columns.</summary>
    public string? Search { get; set; }

    /// <summary>Gets or sets paging for the active table.</summary>
    public PageRequest? Page { get; set; }

    /// <summary>
    /// Gets or sets the table rendered by this request. The identifier is document-owned and has no
    /// execution semantics; "base", "groupBy", and "pivot" are only UI conventions.
    /// </summary>
    public string? ActiveTable { get; set; }

    /// <summary>
    /// Gets or sets named table compositions. A table whose From is "definition" reads the SQL from
    /// the report definition. Any other From value names another document table whose
    /// relational output is composed first. Inactive tables are retained configuration;
    /// they enter execution validation only when selected or when a null schema cache
    /// asks the server to derive their output schema.
    /// </summary>
    public Dictionary<string, ReportTable>? Tables { get; set; }
}

/// <summary>
/// One named table: an explicit input relation plus composable declarations. Array
/// position is storage order; composable semantics determine execution order.
/// </summary>
public sealed class ReportTable
{
    /// <summary>Gets or sets <c>definition</c> or another table identifier in this document as the input relation.</summary>
    public string? From { get; set; }

    /// <summary>
    /// Gets or sets a non-authoritative cache of the complete output schema most recently produced
    /// for this table, before a select composable hides columns. The server never uses
    /// this cache to authorize or bind an expression.
    /// </summary>
    public List<ColumnInfo>? Schema { get; set; }

    /// <summary>Gets or sets the authored operations composed over <see cref="From"/>.</summary>
    public List<TableComposable>? Composables { get; set; }
}

/// <summary>
/// One operation composed onto a table. <see cref="Kind"/> selects the payload fields.
/// Relation-changing composables (group, pivot, chart) and other composables (compute,
/// filter, sort, select, labels, formats, highlight, break, aggregate) deliberately
/// share one protocol. The engine interprets Kind and its natural phase; the owning
/// table's name and array position do not.
/// </summary>
public sealed class TableComposable
{
    /// <summary>Gets or sets the case-insensitive operation token.</summary>
    public string Kind { get; set; } = "";

    // Group.

    /// <summary>Gets or sets group dimensions from the relation immediately before this composable.</summary>
    public List<string>? By { get; set; }

    /// <summary>Gets or sets aggregate metrics with stable ids. Empty means the implicit <c>__count</c> alone.</summary>
    public List<MetricRule>? Values { get; set; }

    // Pivot.

    /// <summary>Gets or sets pivot row dimensions resolved directly against the source table.</summary>
    public List<string>? Rows { get; set; }

    /// <summary>
    /// Gets or sets source columns whose distinct values become pivot cell columns.
    /// </summary>
    public List<string>? Cols { get; set; }

    /// <summary>Gets or sets whether to show correctly re-aggregated total rows below the pivot matrix.</summary>
    public bool? Totals { get; set; }

    // Chart shape (a label dimension and a single numeric metric)

    /// <summary>Gets or sets the chart type: <c>bar</c>, <c>line</c>, <c>area</c>, or <c>pie</c>.</summary>
    public string? Type { get; set; }

    /// <summary>Gets or sets the chart's text, number, date, or Boolean category column.</summary>
    public string? Label { get; set; }

    /// <summary>Gets or sets the metric source column. It is optional only with <c>count</c>, which becomes COUNT(*).</summary>
    public string? Value { get; set; }

    /// <summary>Gets or sets optional aggregation over <see cref="Value"/> grouped by <see cref="Label"/>. Absence means one point per filtered row.</summary>
    public AggregateFn? Fn { get; set; }

    /// <summary>Gets or sets chart orientation: <c>vertical</c> by default or <c>horizontal</c>.</summary>
    public string? Orientation { get; set; }

    /// <summary>Gets or sets default chart point ordering.</summary>
    public ChartSortSpec? Sort { get; set; }

    /// <summary>Gets or sets the optional category-axis title.</summary>
    public string? LabelAxisTitle { get; set; }
    /// <summary>Gets or sets the optional value-axis title.</summary>
    public string? ValueAxisTitle { get; set; }

    // Ordinary table composables.

    /// <summary>Gets or sets visible columns for a select operation.</summary>
    public List<string>? Columns { get; set; }
    /// <summary>Gets or sets presentation labels keyed by logical column name.</summary>
    public Dictionary<string, string>? Labels { get; set; }
    /// <summary>Gets or sets presentation formats keyed by logical column name.</summary>
    public Dictionary<string, ColumnFormat>? Formats { get; set; }
    /// <summary>Gets or sets computed-column rules.</summary>
    public List<ComputedColumn>? Computed { get; set; }
    /// <summary>Gets or sets row-filter rules.</summary>
    public List<FilterRule>? Filters { get; set; }
    /// <summary>Gets or sets terminal sort rules.</summary>
    public List<SortRule>? Sorts { get; set; }
    /// <summary>Gets or sets row and cell highlight rules.</summary>
    public List<HighlightRule>? Highlights { get; set; }
    /// <summary>Gets or sets control-break columns.</summary>
    public List<string>? Breaks { get; set; }
    /// <summary>Gets or sets terminal aggregate rules.</summary>
    public List<AggregateRule>? Aggregates { get; set; }
}

/// <summary>
/// One aggregate metric of a group or pivot composable. The id is a stable synthetic output-column
/// name such as "ir1". Metrics and computed columns use the same logical namespace. An id
/// is unique across the report document and never derives from compiler traversal order.
/// </summary>
public sealed class MetricRule
{
    /// <summary>Gets or sets the document-wide stable synthetic output id.</summary>
    public string Id { get; set; } = "";
    /// <summary>Gets or sets the aggregate input column.</summary>
    public string Col { get; set; } = "";
    /// <summary>Gets or sets the aggregate function.</summary>
    public AggregateFn Fn { get; set; }
}

/// <summary>
/// Per-column display settings, all optional. Mask tokens are a closed protocol
/// vocabulary (per column type); style properties are the same constrained set the
/// highlight rules use. Classes select rules from the report definition's trusted
/// shadow-root stylesheet; report state can never supply CSS or a stylesheet URL.
/// </summary>
public sealed class ColumnFormat
{
    /// <summary>Gets or sets the type-specific display mask.</summary>
    public string? Mask { get; set; }

    /// <summary>Gets or sets <c>left</c>, <c>center</c>, or <c>right</c>; null uses the column type's default.</summary>
    public string? Align { get; set; }

    /// <summary>Gets or sets whether cells use bold text.</summary>
    public bool? Bold { get; set; }
    /// <summary>Gets or sets whether cells use italic text.</summary>
    public bool? Italic { get; set; }

    /// <summary>Gets or sets the text color, using the same syntax as <see cref="HighlightStyle"/>.</summary>
    public string? Fg { get; set; }
    /// <summary>Gets or sets the background color, using the same syntax as <see cref="HighlightStyle"/>.</summary>
    public string? Bg { get; set; }

    /// <summary>
    /// Gets or sets trusted custom class tokens for this column's header and cells. The client accepts a
    /// conservative identifier subset and refuses the component's reserved ir- prefix.
    /// </summary>
    public List<string>? Classes { get; set; }

    /// <summary>Gets or sets <c>link</c>, <c>image</c>, or <c>action</c>; null or unknown values render as ordinary text.</summary>
    public string? DisplayAs { get; set; }

    /// <summary>
    /// Gets or sets the row column supplying the URL for link and image renderers. Null selects the
    /// formatted column itself.
    /// </summary>
    public string? UrlColumn { get; set; }

    /// <summary>
    /// Gets or sets the row column supplying link text. Null selects the formatted column itself.
    /// Ignored by image and ordinary-text renderers.
    /// </summary>
    public string? TextColumn { get; set; }

    /// <summary>
    /// Gets or sets the opaque host command token for the action renderer. The cell's own value is
    /// the button label; a null/blank label renders no button. Presentation data —
    /// never validated against the schema.
    /// </summary>
    public string? Command { get; set; }

    /// <summary>
    /// Gets or sets the row column whose value an action event must carry, typically a row id.
    /// Delivered as a hidden projection column, like <see cref="UrlColumn"/>;
    /// null binds nothing.
    /// </summary>
    public string? KeyColumn { get; set; }
}

/// <summary>
/// A typed expression instruction that is independently enabled or disabled.
/// Computed columns, filters, and highlights share this protocol shape; their
/// effect determines the required result type and where the expression is applied.
/// </summary>
public abstract class ExpressionRule
{
    /// <summary>Gets or sets whether the rule participates in validation and execution.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Gets or sets the portable expression source text.</summary>
    public string Expr { get; set; } = "";
}

/// <summary>Uses a boolean expression to include or exclude rows.</summary>
public sealed class FilterRule : ExpressionRule;

/// <summary>Orders terminal rows by one logical column.</summary>
public sealed class SortRule
{
    /// <summary>Gets or sets the logical sort column.</summary>
    public string Col { get; set; } = "";
    /// <summary>Gets or sets the sort direction.</summary>
    public SortDir Dir { get; set; } = SortDir.Asc;

    /// <summary>
    /// Gets or sets optional explicit null placement. Null preserves the database dialect's
    /// existing default; serialized values are "first" and "last".
    /// </summary>
    public NullPlacement? Nulls { get; set; }
}

/// <summary>Requests one page from the active table.</summary>
public sealed class PageRequest
{
    /// <summary>Gets or sets the one-based page index.</summary>
    public int Index { get; set; } = 1;

    /// <summary>
    /// Gets or sets rows per page. Zero is the explicit allow-listed value for every matching row
    /// in one unpaged result; positive values are clamped to MaxPageSize.
    /// </summary>
    public int Size { get; set; } = 50;
}

/// <summary>Defines a synthetic output column from a portable value expression.</summary>
public sealed class ComputedColumn : ExpressionRule
{
    /// <summary>
    /// Gets or sets a document-wide stable synthetic id such as <c>ir1</c>; it may not shadow an input column.
    /// </summary>
    public string Id { get; set; } = "";
    /// <summary>Gets or sets the optional display label.</summary>
    public string? Label { get; set; }
}

/// <summary>Requests one aggregate over a terminal result column.</summary>
public sealed class AggregateRule
{
    /// <summary>Gets or sets the aggregate input column.</summary>
    public string Col { get; set; } = "";
    /// <summary>Gets or sets the aggregate function.</summary>
    public AggregateFn Fn { get; set; }
}

/// <summary>Applies a style when a portable boolean expression matches a row or cell.</summary>
public sealed class HighlightRule : ExpressionRule
{
    /// <summary>Gets or sets the report-wide stable highlight id.</summary>
    public string Id { get; set; } = "";

    /// <summary>Gets or sets the human-readable rule name. Legacy documents fall back to <see cref="Id"/>.</summary>
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets a positive precedence value. Row rules apply before cell rules. Within the
    /// same scope, rules apply from low to high sequence, so the higher sequence
    /// wins when matching rules set the same property and target.
    /// Documents without a sequence receive stable ten-step values ordered by highlight id.
    /// </summary>
    public int? Sequence { get; set; }

    /// <summary>Gets or sets <c>row</c> or <c>cell</c> scope.</summary>
    public string Scope { get; set; } = "row";

    /// <summary>Gets or sets the target column required by cell scope.</summary>
    public string? Col { get; set; }

    /// <summary>Gets or sets the colors applied when the expression matches.</summary>
    public HighlightStyle? Style { get; set; }
}

/// <summary>Contains optional background and foreground colors for a matched highlight.</summary>
public sealed class HighlightStyle
{
    /// <summary>Gets or sets the background color.</summary>
    public string? Bg { get; set; }
    /// <summary>Gets or sets the foreground text color.</summary>
    public string? Fg { get; set; }
}

/// <summary>Default point ordering owned by a chart composable; a table-local sort may reorder its output.</summary>
public sealed class ChartSortSpec
{
    /// <summary>Gets or sets <c>label</c> by default or <c>value</c>.</summary>
    public string By { get; set; } = "label";

    /// <summary>Gets or sets the point sort direction.</summary>
    public SortDir Dir { get; set; } = SortDir.Asc;
}
