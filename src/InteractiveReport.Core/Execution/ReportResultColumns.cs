using System.Text.Json;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Schema;
using InteractiveReport.Core.Validation;

namespace InteractiveReport.Core.Execution;

/// <summary>Builds protocol column metadata from validated engine models.</summary>
internal static class ReportResultColumns
{
    /// <summary>Projects metadata by canonical selected-column order.</summary>
    public static List<ColumnInfo> Select(
        IReadOnlyList<ColumnInfo> available,
        IReadOnlyList<ColumnModel> selected)
    {
        var lookup = available.ToDictionary(column => column.Name, StringComparer.OrdinalIgnoreCase);
        return selected
            .Where(column => lookup.ContainsKey(column.Name))
            .Select(column => lookup[column.Name])
            .ToList();
    }

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

    /// <summary>
    /// Complete metadata for a statically-known bound relation. Shape columns retain
    /// format-source metadata and computed output columns come from the relation's
    /// schema contract. Presentation labels are deliberately absent: query/cache
    /// metadata is structural, while export applies labels in its shared renderer.
    /// </summary>
    public static List<ColumnInfo> ForBoundRelation(
        ReportSchema schema,
        IReadOnlyList<ColumnInfo> shapeColumns)
    {
        var shape = shapeColumns.ToDictionary(column => column.Name, StringComparer.OrdinalIgnoreCase);
        return schema.Columns.Select(column =>
        {
            return shape.TryGetValue(column.Name, out var known)
                ? known
                : new ColumnInfo(column.Name, column.Label, column.KindName, column.IsComputed);
        }).ToList();
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
