using System.Text.Json;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Schema;
using InteractiveReport.Core.Validation;

namespace InteractiveReport.Core.Execution;

/// <summary>Builds protocol column metadata from validated engine models.</summary>
internal static class ReportResultColumns
{
    /// <summary>
    /// Projects available metadata into canonical selected-column order.
    /// </summary>
    /// <param name="available">The available column contract from which to select output columns.</param>
    /// <param name="selected">The selected engine columns in desired result order.</param>
    /// <returns>The matching public column metadata in selected order; unknown selections are omitted.</returns>
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

    /// <summary>
    /// Builds the two public chart columns: the category label followed by the metric.
    /// </summary>
    /// <param name="chart">The validated chart binding.</param>
    /// <returns>The label and metric metadata, with a disambiguated metric name when both names collide.</returns>
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
    /// Builds complete metadata for a statically known bound relation. Shape columns retain
    /// format-source metadata and computed output columns come from the relation's schema contract.
    /// Presentation labels are deliberately absent: query/cache metadata is structural, while export applies
    /// labels in its shared renderer.
    /// </summary>
    /// <param name="schema">The final relation schema in public order.</param>
    /// <param name="shapeColumns">Columns produced by the terminal shape operation.</param>
    /// <returns>One <see cref="ColumnInfo"/> per schema column, reusing richer shape metadata when available.</returns>
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

    /// <summary>
    /// Creates result-column metadata for an aggregate projection.
    /// </summary>
    /// <param name="aggregate">The validated aggregate binding.</param>
    /// <param name="name">The logical output name assigned by the shape.</param>
    /// <returns>Protocol metadata with the aggregate's label, result type, and format lineage.</returns>
    public static ColumnInfo ForAggregate(ValidAggregate aggregate, string name)
        => new(name, AggregateLabel(aggregate), AggregateType(aggregate), false)
        {
            // A count is a dimensionless row quantity, not a value expressed in the source
            // column's currency/percentage/date format.
            FormatSource = FormatSource(aggregate),
        };

    /// <summary>
    /// Selects the source column from which an aggregate inherits formatting.
    /// </summary>
    /// <param name="aggregate">The validated aggregate binding.</param>
    /// <returns>The source column name from which formatting is inherited, or <see langword="null"/> for dimensionless aggregates.</returns>
    public static string? FormatSource(ValidAggregate aggregate)
        => aggregate.Fn is AggregateFn.Count or AggregateFn.CountDistinct
            ? null
            : aggregate.Column.Name;

    /// <summary>
    /// Builds the display label for a validated aggregate.
    /// </summary>
    /// <param name="aggregate">The validated aggregate binding.</param>
    /// <returns>The display label for the aggregate result.</returns>
    public static string AggregateLabel(ValidAggregate aggregate)
        => $"{AggregateName(aggregate.Fn)}({aggregate.Column.Label})";

    /// <summary>
    /// Returns the protocol column kind produced by a validated aggregate.
    /// </summary>
    /// <param name="aggregate">The validated aggregate binding.</param>
    /// <returns>The protocol column kind produced by the aggregate.</returns>
    public static string AggregateType(ValidAggregate aggregate)
        => aggregate.Fn is AggregateFn.Min or AggregateFn.Max
            ? aggregate.Column.KindName
            : "number";

    /// <summary>
    /// Returns the canonical function name for a validated aggregate.
    /// </summary>
    /// <param name="function">The aggregate function to serialize.</param>
    /// <returns>The canonical aggregate-function name.</returns>
    public static string AggregateName(AggregateFn function)
        => JsonNamingPolicy.CamelCase.ConvertName(function.ToString());
}
