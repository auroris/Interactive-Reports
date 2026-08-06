using System.Text.Json;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Validation;

namespace InteractiveReport.Core.Execution;

/// <summary>Builds protocol column metadata from validated engine models.</summary>
internal static class ReportResultColumns
{
    public static List<ColumnInfo> From(IEnumerable<ColumnModel> columns)
        => columns
            .Select(column => new ColumnInfo(
                column.Name,
                column.Label,
                column.KindName,
                column.IsComputed))
            .ToList();

    public static List<ColumnInfo> ForGroupBy(ValidatedState state)
    {
        var columns = From(state.View.GroupBy);
        columns.Add(new ColumnInfo("__count", "Count", "number", false));
        for (var i = 0; i < state.View.Values.Count; i++)
            columns.Add(ForAggregate(state.View.Values[i], $"v{i}"));
        return columns;
    }

    public static ColumnInfo ForAggregate(ValidAggregate aggregate, string name)
        => new(name, AggregateLabel(aggregate), AggregateType(aggregate), false);

    public static string AggregateLabel(ValidAggregate aggregate)
        => $"{AggregateName(aggregate.Fn)}({aggregate.Column.Label})";

    public static string AggregateType(ValidAggregate aggregate)
        => aggregate.Fn is AggregateFn.Min or AggregateFn.Max
            ? aggregate.Column.KindName
            : "number";

    public static string AggregateName(AggregateFn function)
        => JsonNamingPolicy.CamelCase.ConvertName(function.ToString());
}
