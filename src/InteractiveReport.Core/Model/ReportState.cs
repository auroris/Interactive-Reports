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

    public List<ComputedColumn>? Computed { get; set; }
    public List<string>? Breaks { get; set; }
    public List<AggregateRule>? Aggregates { get; set; }
    public List<HighlightRule>? Highlights { get; set; }
    public ViewSpec? View { get; set; }

    public PageRequest? Page { get; set; }
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
    /// <summary>"grid" (default), "groupBy", "pivot".</summary>
    public string Mode { get; set; } = "grid";

    public List<string>? GroupBy { get; set; }
    public List<string>? Rows { get; set; }
    public List<string>? Cols { get; set; }
    public List<AggregateRule>? Values { get; set; }
}
