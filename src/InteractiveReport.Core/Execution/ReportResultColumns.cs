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

    /// <summary>Two columns always: the label as itself, then the metric.</summary>
    public static List<ColumnInfo> ForChart(ValidChart chart)
    {
        var label = new ColumnInfo(chart.Label.Name, chart.Label.Label, chart.Label.KindName, chart.Label.IsComputed);
        var metric = chart switch
        {
            { Fn: null } => new ColumnInfo(chart.Value!.Name, chart.Value.Label, chart.Value.KindName, chart.Value.IsComputed),
            { Value: null } => new ColumnInfo("__count", "Count", "number", false),
            { Fn: { } fn, Value: { } value } => ForAggregate(new ValidAggregate(value, fn), "v0"),
        };
        return [label, metric];
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
