namespace InteractiveReport.Core.Model;

/// <summary>Response shape for a query. Aggregates/break totals/highlights arrive in later milestones.</summary>
public sealed class ReportResult
{
    public required IReadOnlyList<ColumnInfo> Columns { get; init; }

    /// <summary>Rows as objects keyed by column name — page-granularity size cost is negligible, ergonomics win.</summary>
    public required IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows { get; init; }

    public required PageRequest Page { get; init; }

    /// <summary>Total rows in the whole filtered set (never just the visible page).</summary>
    public required long TotalRows { get; init; }

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> Aggregates { get; init; }
        = new Dictionary<string, IReadOnlyDictionary<string, object?>>();

    public IReadOnlyList<object> BreakTotals { get; init; } = [];

    public IReadOnlyList<HighlightHit> Highlights { get; init; } = [];

    /// <summary>
    /// State elements referencing columns that no longer exist (or features not yet
    /// implemented) are dropped and reported here — saved reports degrade, never 500.
    /// </summary>
    public required IReadOnlyList<IgnoredItem> Ignored { get; init; }

    public long ElapsedMs { get; init; }
}

public sealed record ColumnInfo(string Name, string Label, string Type, bool Computed);

public sealed record HighlightHit(int Row, string Id, string? Col);

public sealed record IgnoredItem(string Kind, string Detail);

public sealed class ReportSummary
{
    public required string Name { get; init; }
    public required string Title { get; init; }
}
