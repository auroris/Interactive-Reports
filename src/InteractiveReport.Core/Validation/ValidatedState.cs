using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Validation;

/// <summary>
/// The typed, schema-checked form of a state document. Only this — never the raw DTO —
/// reaches the query composer.
/// </summary>
public sealed class ValidatedState
{
    public required IReadOnlyList<ValidFilter> Filters { get; init; }
    public string? Search { get; init; }
    public required IReadOnlyList<ValidSort> Sorts { get; init; }
    public required IReadOnlyList<ColumnModel> SelectColumns { get; init; }
    public required int PageIndex { get; init; }
    public required int PageSize { get; init; }
    public required IReadOnlyList<IgnoredItem> Ignored { get; init; }
}

public sealed record ValidFilter(
    ColumnModel Column,
    FilterOp Op,
    object? Value = null,
    object? Value2 = null,
    IReadOnlyList<object>? Values = null);

public sealed record ValidSort(ColumnModel Column, SortDir Dir);
