using System.Collections.Frozen;
using System.Text.Json;
using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Validation;

/// <summary>One source of truth for aggregate/type compatibility and client capabilities.</summary>
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

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> FunctionsByColumnType { get; } =
        Enum.GetValues<ColumnKind>().ToFrozenDictionary(
            kind => KindName(kind),
            kind => (IReadOnlyList<string>)Array.AsReadOnly(All
                .Where(function => IsCompatible(kind, function))
                .Select(FunctionName)
                .ToArray()),
            StringComparer.OrdinalIgnoreCase);

    public static bool IsCompatible(ColumnKind kind, AggregateFn function) => function switch
    {
        AggregateFn.Sum or AggregateFn.Avg or AggregateFn.Median => kind == ColumnKind.Number,
        AggregateFn.Min or AggregateFn.Max => kind is ColumnKind.Number or ColumnKind.Date or ColumnKind.Text,
        AggregateFn.Count or AggregateFn.CountDistinct => true,
        _ => false,
    };

    /// <summary>Chart functions by column type, for client fn pickers in the chart dialog.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ChartFunctionsByColumnType { get; } =
        Enum.GetValues<ColumnKind>().ToFrozenDictionary(
            kind => KindName(kind),
            kind => (IReadOnlyList<string>)Array.AsReadOnly(All
                .Where(function => IsChartCompatible(kind, function))
                .Select(FunctionName)
                .ToArray()),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Chart metrics must be numeric, so charts are stricter than grid aggregation:
    /// min/max lose their date/text reach here because MIN(ORDER_DATE) is a date, not
    /// a plottable number.
    /// </summary>
    public static bool IsChartCompatible(ColumnKind kind, AggregateFn function) => function switch
    {
        AggregateFn.Sum or AggregateFn.Avg or AggregateFn.Median or AggregateFn.Min or AggregateFn.Max
            => kind == ColumnKind.Number,
        AggregateFn.Count or AggregateFn.CountDistinct => true,
        _ => false,
    };

    private static string FunctionName(AggregateFn function)
        => JsonNamingPolicy.CamelCase.ConvertName(function.ToString());

    private static string KindName(ColumnKind kind) => kind.ToString().ToLowerInvariant();
}
