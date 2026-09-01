using System.Collections.Immutable;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Schema;
using InteractiveReport.Core.Validation;

namespace InteractiveReport.Core.Planning;

/// <summary>
/// Immutable owner-local result plan bound against one completed output contract.
/// It is never exported through a table's <c>from</c> edge.
/// </summary>
/// <param name="Schema">The schema exposed by the completed table.</param>
/// <param name="Decorations">Query-private highlight projections evaluated for the active table.</param>
/// <param name="Sorts">Validated terminal sort rules.</param>
/// <param name="SelectColumns">Visible columns selected for the public result.</param>
/// <param name="ProjectionColumns">Public and private columns required from the execution query.</param>
/// <param name="Aggregates">Validated terminal aggregate rules.</param>
/// <param name="Breaks">Validated control-break columns.</param>
/// <param name="Labels">Effective labels keyed by logical column name.</param>
/// <param name="Formats">Effective presentation formats keyed by logical column name.</param>
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
    /// <summary>
    /// Creates an empty table-local result that owns no rules or presentation overrides.
    /// </summary>
    /// <param name="schema">The completed table schema used for both selection and projection.</param>
    /// <returns>A local result with every schema column visible and no terminal rules or presentation overrides.</returns>
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
/// <param name="Search">The normalized search text, or <see langword="null"/> when no search is active.</param>
/// <param name="PageIndex">The effective one-based page index.</param>
/// <param name="PageSize">The bounded page size, or zero for unpaged delivery.</param>
/// <param name="PageAll">Whether the request uses unpaged delivery.</param>
internal sealed record BoundRequestOverlay(
    string? Search,
    int PageIndex,
    int PageSize,
    bool PageAll)
{
    /// <summary>
    /// Normalizes search and paging from a report document against the definition's delivery limits.
    /// </summary>
    /// <param name="definition">The definition supplying default and maximum page sizes.</param>
    /// <param name="document">The active report document supplying optional search and paging overrides.</param>
    /// <returns>The normalized request-only delivery settings.</returns>
    public static BoundRequestOverlay From(
        ReportDefinition definition,
        ReportState document)
    {
        var requestedSize = document.Page?.Size ?? definition.DefaultPageSize;
        var pageAll = requestedSize == 0;
        var pageSize = pageAll ? 0 : Math.Clamp(requestedSize, 1, definition.MaxPageSize);
        // The offset is (index - 1) * size in the query builder's int arithmetic; an index past the
        // last representable offset is clamped there rather than wrapping around to page one.
        var maxIndex = pageAll ? 1 : int.MaxValue / pageSize;
        return new BoundRequestOverlay(
            Search: string.IsNullOrWhiteSpace(document.Search)
                ? null
                : document.Search.Trim(),
            PageIndex: pageAll ? 1 : Math.Clamp(document.Page?.Index ?? 1, 1, maxIndex),
            PageSize: pageSize,
            PageAll: pageAll);
    }
}
