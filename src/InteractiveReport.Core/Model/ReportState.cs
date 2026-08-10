namespace InteractiveReport.Core.Model;

/// <summary>
/// The report state document: simultaneously the query request body, the saved report,
/// and the shareable view state. Everything in it is data validated against the report's
/// discovered schema — never code. Version 3 is a literal pipeline of table stages;
/// versions 1 and 2 are rejected (no external consumers, no migrator by owner decision).
/// </summary>
public sealed class ReportState
{
    public const int CurrentVersion = 3;

    public int V { get; set; } = CurrentVersion;

    /// <summary>
    /// The discovered schema snapshot (column name → logical kind) this document was
    /// authored against. A client-side contract: the client stamps it on save and
    /// compares it against the live schema on load (removed/retyped columns are a
    /// mismatch; additions pass). The server never reads it.
    /// </summary>
    public Dictionary<string, string>? Schema { get; set; }

    /// <summary>Toolbar search: OR of case-insensitive contains across the source layer's visible text columns.</summary>
    public string? Search { get; set; }

    public PageRequest? Page { get; set; }

    /// <summary>
    /// The executing pipeline. The first stage is always "source"; T0 accepts the tails
    /// [], [group], [group, spread], and [chart]. The client's view mode is derived
    /// from the tail, never stored. Null or empty means the bare source stage.
    /// </summary>
    public List<PipelineStage>? Pipeline { get; set; }

    /// <summary>
    /// Parked alternate tails (stage arrays after source), keyed by their derived mode
    /// name (groupBy, pivot, chart). Never validated or executed — inert retained
    /// configuration the toolbar swaps into the pipeline.
    /// </summary>
    public Dictionary<string, List<PipelineStage>>? Shelf { get; set; }
}

/// <summary>One pipeline stage: how the input table is reshaped plus the per-table layer.</summary>
public sealed class PipelineStage
{
    public StageShape? Shape { get; set; }
    public StageLayer? Layer { get; set; }
}

/// <summary>
/// The reshaping half of a stage. Kind selects which fields apply: "source" uses none,
/// "group" uses By/Values, "spread" uses Cols/Totals, "chart" uses the chart fields.
/// </summary>
public sealed class StageShape
{
    /// <summary>"source", "group", "spread", or "chart".</summary>
    public string Kind { get; set; } = "source";

    // group

    /// <summary>Group dimensions (schema or computed columns of the previous stage).</summary>
    public List<string>? By { get; set; }

    /// <summary>Aggregate metrics with stable ids. Empty means the implicit __count alone.</summary>
    public List<MetricRule>? Values { get; set; }

    // spread

    /// <summary>
    /// The subset of the preceding group stage's By columns whose values spread into
    /// cell columns; the remaining By columns key the rows.
    /// </summary>
    public List<string>? Cols { get; set; }

    /// <summary>Show correctly re-aggregated total rows below the matrix.</summary>
    public bool? Totals { get; set; }

    // chart (one chart per report — a label dimension and a single numeric metric)

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
}

/// <summary>
/// One aggregate metric of a group stage. The id ("m1", "m2", …) is the metric's stable
/// column name in the stage's output — a namespace like computed columns' c1, unique
/// within the stage, never shadowing input columns — so reordering the values list can
/// never silently change what downstream references mean.
/// </summary>
public sealed class MetricRule
{
    public string Id { get; set; } = "";
    public string Col { get; set; } = "";
    public AggregateFn Fn { get; set; }
}

/// <summary>
/// The per-table layer of a stage, every field bound to that stage's own output schema.
/// T0 restrictions: filters, breaks, and aggregates are source-only; spread layers carry
/// labels/formats only; chart layers stay empty. Absent fields inherit nothing — the
/// layer is data, and an absent list simply means none.
/// </summary>
public sealed class StageLayer
{
    /// <summary>Visible columns in display order when this stage is terminal. Null/empty = all output columns.</summary>
    public List<string>? Columns { get; set; }

    /// <summary>
    /// Stage-output column name → display label. Presentation, never a program: unknown
    /// keys are unused display data. The server consumes labels only for export.
    /// </summary>
    public Dictionary<string, string>? Labels { get; set; }

    /// <summary>
    /// Stage-output column name → display formatting. Renderer source columns
    /// (displayAs/urlColumn/textColumn) are honored on the source layer only.
    /// </summary>
    public Dictionary<string, ColumnFormat>? Formats { get; set; }

    public List<ComputedColumn>? Computed { get; set; }

    /// <summary>Row predicates over this stage's table. T0: validated on the source stage only.</summary>
    public List<FilterRule>? Filters { get; set; }

    public List<SortRule>? Sorts { get; set; }
    public List<HighlightRule>? Highlights { get; set; }

    /// <summary>Control-break columns (source layer only at T0).</summary>
    public List<string>? Breaks { get; set; }

    /// <summary>Footer aggregates (source layer only at T0).</summary>
    public List<AggregateRule>? Aggregates { get; set; }
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

    /// <summary>"link" or "image"; null/unknown values render as ordinary text.</summary>
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
    /// <summary>Separate namespace from schema columns ("c1", "c2", ...); may not shadow the owning stage's columns.</summary>
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
    /// Legacy documents derive sequence from their list position.
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

/// <summary>Chart ordering lives inside the chart stage's shape; table sorts never apply to charts.</summary>
public sealed class ChartSortSpec
{
    /// <summary>"label" (default) or "value".</summary>
    public string By { get; set; } = "label";

    public SortDir Dir { get; set; } = SortDir.Asc;
}
