namespace InteractiveReport.Core.Model;

/// <summary>
/// The report state document: simultaneously the query request body, the saved report,
/// and the shareable view state. Everything in it is data validated against the report's
/// discovered schema — never code. Its structure is self-describing; documents do not
/// carry a protocol version field.
/// </summary>
public sealed class ReportState
{
    /// <summary>Toolbar search: OR of case-insensitive contains across eligible input text columns.</summary>
    public string? Search { get; set; }

    public PageRequest? Page { get; set; }

    /// <summary>
    /// The table rendered by this request. The identifier is document-owned and has no
    /// execution semantics; "base", "groupBy", and "pivot" are only UI conventions.
    /// </summary>
    public string? ActiveTable { get; set; }

    /// <summary>
    /// Named table compositions. A table whose From is "definition" reads the SQL from
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
    /// <summary>"definition" or another table identifier in this document.</summary>
    public string? From { get; set; }

    /// <summary>
    /// Non-authoritative cache of the complete output schema most recently produced
    /// for this table, before a select composable hides columns. The server never uses
    /// this cache to authorize or bind an expression.
    /// </summary>
    public List<ColumnInfo>? Schema { get; set; }

    public List<TableComposable>? Composables { get; set; }
}

/// <summary>
/// One operation composed onto a table. Kind selects the payload fields.
/// Relation-changing composables (group, pivot, chart) and other composables (compute,
/// filter, sort, select, labels, formats, highlight, break, aggregate) deliberately
/// share one protocol. The engine interprets Kind and its natural phase; the owning
/// table's name and array position do not.
/// </summary>
public sealed class TableComposable
{
    public string Kind { get; set; } = "";

    // group

    /// <summary>Group dimensions from the relation immediately before this composable.</summary>
    public List<string>? By { get; set; }

    /// <summary>Aggregate metrics with stable ids. Empty means the implicit __count alone.</summary>
    public List<MetricRule>? Values { get; set; }

    // pivot

    /// <summary>Pivot row dimensions, resolved directly against the source table.</summary>
    public List<string>? Rows { get; set; }

    /// <summary>
    /// Source columns whose distinct values become pivot cell columns.
    /// </summary>
    public List<string>? Cols { get; set; }

    /// <summary>Show correctly re-aggregated total rows below the matrix.</summary>
    public bool? Totals { get; set; }

    // chart shape (a label dimension and a single numeric metric)

    /// <summary>"bar", "line", "area", or "pie".</summary>
    public string? Type { get; set; }

    /// <summary>Label (category) column; text, number, date, or bool.</summary>
    public string? Label { get; set; }

    /// <summary>Metric source column. Optional only with fn "count", which becomes COUNT(*).</summary>
    public string? Value { get; set; }

    /// <summary>Optional aggregation over Value grouped by Label. Absent = one point per filtered row.</summary>
    public AggregateFn? Fn { get; set; }

    /// <summary>"vertical" (default) or "horizontal".</summary>
    public string? Orientation { get; set; }

    public ChartSortSpec? Sort { get; set; }

    public string? LabelAxisTitle { get; set; }
    public string? ValueAxisTitle { get; set; }

    // ordinary table composables

    public List<string>? Columns { get; set; }
    public Dictionary<string, string>? Labels { get; set; }
    public Dictionary<string, ColumnFormat>? Formats { get; set; }
    public List<ComputedColumn>? Computed { get; set; }
    public List<FilterRule>? Filters { get; set; }
    public List<SortRule>? Sorts { get; set; }
    public List<HighlightRule>? Highlights { get; set; }
    public List<string>? Breaks { get; set; }
    public List<AggregateRule>? Aggregates { get; set; }
}

/// <summary>
/// One aggregate metric of a group composable. The id is a stable synthetic output-column
/// name such as "ir1". Metrics and computed columns use the same logical namespace. An id
/// is unique across the report document and never derives from compiler traversal order.
/// </summary>
public sealed class MetricRule
{
    public string Id { get; set; } = "";
    public string Col { get; set; } = "";
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
    public string? Mask { get; set; }

    /// <summary>"left", "center", or "right"; null = the column type's default.</summary>
    public string? Align { get; set; }

    public bool? Bold { get; set; }
    public bool? Italic { get; set; }

    /// <summary>Text / background colors, as in <see cref="HighlightStyle"/>.</summary>
    public string? Fg { get; set; }
    public string? Bg { get; set; }

    /// <summary>
    /// Custom class tokens for this column's header and cells. The client accepts a
    /// conservative identifier subset and refuses the component's reserved ir- prefix.
    /// </summary>
    public List<string>? Classes { get; set; }

    /// <summary>"link", "image", or "action"; null/unknown values render as ordinary text.</summary>
    public string? DisplayAs { get; set; }

    /// <summary>
    /// Row column supplying the URL for link/image renderers. Null selects the
    /// formatted column itself.
    /// </summary>
    public string? UrlColumn { get; set; }

    /// <summary>
    /// Row column supplying link text. Null selects the formatted column itself.
    /// Ignored by image and ordinary-text renderers.
    /// </summary>
    public string? TextColumn { get; set; }

    /// <summary>
    /// Opaque host command token for the action renderer. The cell's own value is
    /// the button label; a null/blank label renders no button. Presentation data —
    /// never validated against the schema.
    /// </summary>
    public string? Command { get; set; }

    /// <summary>
    /// Row column whose value an action event must carry (typically a row id).
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
    public bool Enabled { get; set; } = true;
    public string Expr { get; set; } = "";
}

public sealed class FilterRule : ExpressionRule;

public sealed class SortRule
{
    public string Col { get; set; } = "";
    public SortDir Dir { get; set; } = SortDir.Asc;

    /// <summary>
    /// Optional explicit null placement. Null preserves the database dialect's
    /// existing default; serialized values are "first" and "last".
    /// </summary>
    public NullPlacement? Nulls { get; set; }
}

public sealed class PageRequest
{
    /// <summary>1-based.</summary>
    public int Index { get; set; } = 1;

    /// <summary>
    /// Rows per page. Zero is the explicit allow-listed value for every matching row
    /// in one unpaged result; positive values are clamped to MaxPageSize.
    /// </summary>
    public int Size { get; set; } = 50;
}

public sealed class ComputedColumn : ExpressionRule
{
    /// <summary>
    /// Document-wide stable synthetic id such as "ir1"; may not shadow an input column.
    /// </summary>
    public string Id { get; set; } = "";
    public string? Label { get; set; }
}

public sealed class AggregateRule
{
    public string Col { get; set; } = "";
    public AggregateFn Fn { get; set; }
}

public sealed class HighlightRule : ExpressionRule
{
    public string Id { get; set; } = "";

    /// <summary>Human-readable rule name. Legacy documents fall back to Id.</summary>
    public string? Name { get; set; }

    /// <summary>
    /// Positive precedence value. Rules apply from low to high sequence, so the
    /// higher sequence wins when matching rules set the same property and target.
    /// Documents without a sequence receive stable ten-step values ordered by highlight id.
    /// </summary>
    public int? Sequence { get; set; }

    /// <summary>"row" or "cell".</summary>
    public string Scope { get; set; } = "row";

    /// <summary>Target column for cell scope.</summary>
    public string? Col { get; set; }

    public HighlightStyle? Style { get; set; }
}

public sealed class HighlightStyle
{
    public string? Bg { get; set; }
    public string? Fg { get; set; }
}

/// <summary>Default point ordering owned by a chart composable; a table-local sort may reorder its output.</summary>
public sealed class ChartSortSpec
{
    /// <summary>"label" (default) or "value".</summary>
    public string By { get; set; } = "label";

    public SortDir Dir { get; set; } = SortDir.Asc;
}
