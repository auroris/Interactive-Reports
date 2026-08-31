using System.Collections.Frozen;
using System.Text.Json;
using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Validation;

/// <summary>Provides one source of truth for aggregate/type compatibility and client capabilities.</summary>
public static class AggregateCatalog
{
    private static readonly AggregateFn[] All =
    [
        AggregateFn.Sum,
        AggregateFn.Avg,
        AggregateFn.Median,
        AggregateFn.Min,
        AggregateFn.Max,
        AggregateFn.Count,
        AggregateFn.CountDistinct,
    ];

    /// <summary>Gets the grid aggregate functions allowed for each protocol column kind.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> FunctionsByColumnType { get; } =
        Enum.GetValues<ColumnKind>().ToFrozenDictionary(
            kind => KindName(kind),
            kind => (IReadOnlyList<string>)Array.AsReadOnly(All
                .Where(function => IsCompatible(kind, function))
                .Select(FunctionName)
                .ToArray()),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Determines whether the aggregate function is compatible with the column kind for report-state
    /// validation.
    /// </summary>
    /// <param name="kind">The input column kind consumed by the aggregate.</param>
    /// <param name="function">The aggregate function to test.</param>
    /// <returns><see langword="true"/> when the aggregate function is compatible with the column kind; otherwise, <see langword="false"/>.</returns>
    public static bool IsCompatible(ColumnKind kind, AggregateFn function) => function switch
    {
        AggregateFn.Sum or AggregateFn.Avg or AggregateFn.Median => kind == ColumnKind.Number,
        AggregateFn.Min or AggregateFn.Max => kind is ColumnKind.Number or ColumnKind.Date or ColumnKind.Text,
        AggregateFn.Count or AggregateFn.CountDistinct => true,
        _ => false,
    };

    /// <summary>Gets the numeric-output aggregate functions offered for each column kind in chart controls.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ChartFunctionsByColumnType { get; } =
        Enum.GetValues<ColumnKind>().ToFrozenDictionary(
            kind => KindName(kind),
            kind => (IReadOnlyList<string>)Array.AsReadOnly(All
                .Where(function => IsChartCompatible(kind, function))
                .Select(FunctionName)
                .ToArray()),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Determines whether an aggregate produces a numeric chart metric. Charts are stricter than grid aggregation:
    /// min/max lose their date/text reach here because MIN(ORDER_DATE) is a date, not a plottable number.
    /// </summary>
    /// <param name="kind">The input column kind consumed by the chart aggregate.</param>
    /// <param name="function">The aggregate function to test.</param>
    /// <returns><see langword="true"/> when the value can be represented by the chart; otherwise, <see langword="false"/>.</returns>
    public static bool IsChartCompatible(ColumnKind kind, AggregateFn function) => function switch
    {
        AggregateFn.Sum or AggregateFn.Avg or AggregateFn.Median or AggregateFn.Min or AggregateFn.Max
            => kind == ColumnKind.Number,
        AggregateFn.Count or AggregateFn.CountDistinct => true,
        _ => false,
    };

    /// <summary>
    /// Returns the canonical aggregate-function name.
    /// </summary>
    /// <param name="function">The aggregate function to serialize.</param>
    /// <returns>The canonical function name.</returns>
    private static string FunctionName(AggregateFn function)
        => JsonNamingPolicy.CamelCase.ConvertName(function.ToString());

    /// <summary>
    /// Returns the canonical protocol name for a column kind.
    /// </summary>
    /// <param name="kind">The column kind to format for diagnostics.</param>
    /// <returns>The canonical column-kind name.</returns>
    private static string KindName(ColumnKind kind) => kind.ToString().ToLowerInvariant();
}
