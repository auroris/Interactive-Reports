namespace InteractiveReport.Core.Model;

/// <summary>
/// The report state document: simultaneously the query request body, the saved report,
/// and the shareable view state. Everything in it is data validated against the report's
/// discovered schema — never code. Versioned for forward migration.
/// </summary>
public sealed class ReportState
{
    public const int CurrentVersion = 2;

    public int V { get; set; } = CurrentVersion;

    /// <summary>Toolbar search: OR of case-insensitive contains across visible text columns.</summary>
    public string? Search { get; set; }

    public List<FilterRule>? Filters { get; set; }

    public List<SortRule>? Sorts { get; set; }

    /// <summary>Visible columns in display order. Null/empty = all schema columns.</summary>
    public List<string>? Columns { get; set; }

    /// <summary>
    /// Real column name → display label. Presentation, never a program: it does not
    /// gate execution or validation, and query responses keep server-derived labels —
    /// the client renders its own. The server consumes it in exactly one place:
    /// an export renders what the user sees, so the posted document's labels apply
    /// there. Computed columns keep their label on the computed rule.
    /// </summary>
    public Dictionary<string, string>? Labels { get; set; }

    public List<ComputedColumn>? Computed { get; set; }
    public List<string>? Breaks { get; set; }
    public List<AggregateRule>? Aggregates { get; set; }
    public List<HighlightRule>? Highlights { get; set; }
    public ViewSpec? View { get; set; }

    public PageRequest? Page { get; set; }

    /// <summary>
    /// Real column name → display formatting (mask, alignment, styling). Presentation
    /// like <see cref="Labels"/>: never validated, never gating execution. Unlike
    /// labels the server does not consume it anywhere — a mask is a lens on the value,
    /// so exports keep raw values (headers are captions and get labels; cells are data).
    /// </summary>
    public Dictionary<string, ColumnFormat>? Formats { get; set; }
}

/// <summary>
/// Per-column display settings, all optional. Mask tokens are a closed client-side
/// vocabulary (per column type); style properties are the same constrained set the
/// highlight rules use — deliberately not a CSS class (host classes cannot pierce
/// the shadow root) and not freeform CSS (a style string in a globally published
/// report is an injection surface).
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
}

public sealed class PageRequest
{
    /// <summary>1-based.</summary>
    public int Index { get; set; } = 1;

    public int Size { get; set; } = 50;
}

public sealed class ComputedColumn : ExpressionRule
{
    /// <summary>Separate namespace from schema columns ("c1", "c2", ...); may not shadow them.</summary>
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

public sealed class ViewSpec
{
    /// <summary>"grid" (default), "groupBy", "pivot", "chart".</summary>
    public string Mode { get; set; } = "grid";

    public List<string>? GroupBy { get; set; }
    public List<string>? Rows { get; set; }
    public List<string>? Cols { get; set; }
    public List<AggregateRule>? Values { get; set; }

    // Chart mode: one chart per report — a label dimension and a single numeric metric.

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

/// <summary>Chart ordering lives inside the chart spec; grid sorts never apply to charts.</summary>
public sealed class ChartSortSpec
{
    /// <summary>"label" (default) or "value".</summary>
    public string By { get; set; } = "label";

    public SortDir Dir { get; set; } = SortDir.Asc;
}
