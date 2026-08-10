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

    /// <summary>
    /// The terminal group stage's visible columns, in the layer's selection order:
    /// pass-through dims, __count, metrics by stable id, and layer computed columns.
    /// Metric labels rebuild from the (possibly relabeled) view metadata so export
    /// display labels reach the synthetic sum(…) captions.
    /// </summary>
    public static List<ColumnInfo> ForGroupStage(ValidatedState state)
    {
        var view = state.View;
        var layer = view.GroupLayer!;
        var metrics = view.Values.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);

        var result = new List<ColumnInfo>(layer.SelectColumns.Count);
        foreach (var column in layer.SelectColumns)
        {
            if (metrics.TryGetValue(column.Name, out var metric))
                result.Add(ForMetric(metric));
            else if (string.Equals(column.Name, "__count", StringComparison.OrdinalIgnoreCase))
                result.Add(new ColumnInfo("__count", "Count", "number", false));
            else
                result.Add(new ColumnInfo(column.Name, column.Label, column.KindName, column.IsComputed));
        }
        return result;
    }

    public static ColumnInfo ForMetric(ValidMetric metric)
        => ForAggregate(metric.ToAggregate(), metric.Id);

    /// <summary>Two columns always: the label as itself, then the metric.</summary>
    public static List<ColumnInfo> ForChart(ValidChart chart)
    {
        var label = new ColumnInfo(chart.Label.Name, chart.Label.Label, chart.Label.KindName, chart.Label.IsComputed);
        var metric = chart switch
        {
            { Fn: null } => new ColumnInfo(chart.Value!.Name, chart.Value.Label, chart.Value.KindName, chart.Value.IsComputed)
                { FormatSource = chart.Value.Name },
            { Value: null } => new ColumnInfo("__count", "Count", "number", false),
            { Fn: { } fn, Value: { } value } => ForAggregate(new ValidAggregate(value, fn), "v0"),
        };
        if (string.Equals(label.Name, metric.Name, StringComparison.OrdinalIgnoreCase))
            metric = metric with { Name = $"{metric.Name}_metric" };
        return [label, metric];
    }

    public static ColumnInfo ForAggregate(ValidAggregate aggregate, string name)
        => new(name, AggregateLabel(aggregate), AggregateType(aggregate), false)
        {
            // A count is a dimensionless row quantity, not a value expressed in
            // the source column's currency/percentage/date format.
            FormatSource = FormatSource(aggregate),
        };

    public static string? FormatSource(ValidAggregate aggregate)
        => aggregate.Fn is AggregateFn.Count or AggregateFn.CountDistinct
            ? null
            : aggregate.Column.Name;

    public static string AggregateLabel(ValidAggregate aggregate)
        => $"{AggregateName(aggregate.Fn)}({aggregate.Column.Label})";

    public static string AggregateType(ValidAggregate aggregate)
        => aggregate.Fn is AggregateFn.Min or AggregateFn.Max
            ? aggregate.Column.KindName
            : "number";

    public static string AggregateName(AggregateFn function)
        => JsonNamingPolicy.CamelCase.ConvertName(function.ToString());
}
