using System.Collections.Immutable;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Schema;
using InteractiveReport.Core.Validation;

namespace InteractiveReport.Core.Planning;

/// <summary>
/// Immutable owner-local result plan bound against one completed output contract.
/// It is never exported through a table's <c>from</c> edge.
/// </summary>
internal sealed record BoundLocalResult(
    ReportSchema Schema,
    ImmutableArray<CompiledRule<HighlightEffect>> Decorations,
    ImmutableArray<ValidSort> Sorts,
    ImmutableArray<ColumnModel> SelectColumns,
    ImmutableArray<ColumnModel> ProjectionColumns,
    ImmutableArray<ValidAggregate> Aggregates,
    ImmutableArray<ColumnModel> Breaks,
    ImmutableDictionary<string, string> Labels,
    ImmutableDictionary<string, ColumnFormat> Formats)
{
    public static BoundLocalResult Empty(ReportSchema schema)
        => new(
            schema,
            [],
            [],
            schema.Columns.ToImmutableArray(),
            schema.Columns.ToImmutableArray(),
            [],
            [],
            ImmutableDictionary.Create<string, string>(StringComparer.OrdinalIgnoreCase),
            ImmutableDictionary.Create<string, ColumnFormat>(StringComparer.OrdinalIgnoreCase));
}

/// <summary>
/// Request-scoped delivery settings for the active table. This value is never part of
/// a table export and therefore cannot cross a <c>from</c> edge.
/// </summary>
internal sealed record BoundRequestOverlay(
    string? Search,
    int PageIndex,
    int PageSize,
    bool PageAll)
{
    public static BoundRequestOverlay From(
        ReportDefinition definition,
        ReportState document)
    {
        var requestedSize = document.Page?.Size ?? definition.DefaultPageSize;
        var pageAll = requestedSize == 0;
        return new BoundRequestOverlay(
            Search: string.IsNullOrWhiteSpace(document.Search)
                ? null
                : document.Search.Trim(),
            PageIndex: pageAll ? 1 : Math.Max(1, document.Page?.Index ?? 1),
            PageSize: pageAll
                ? 0
                : Math.Clamp(requestedSize, 1, definition.MaxPageSize),
            PageAll: pageAll);
    }
}
