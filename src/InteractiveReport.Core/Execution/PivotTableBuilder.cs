using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Execution;

/// <summary>Materializes Pivot footer rows for flat exports.</summary>
internal static class PivotTableBuilder
{
    private static readonly string[] TotalFunctionOrder =
        ["sum", "avg", "median", "min", "max", "count", "countDistinct", "total"];

    /// <summary>
    /// CSV has no separate footer channel, so materialize the same total rows the
    /// browser renders from <see cref="ReportResult.Aggregates"/>. The first visible
    /// row dimension carries the label; remaining dimension cells stay empty.
    /// </summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> RowsForExport(
        IReadOnlyList<ColumnInfo> columns,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> totals,
        IReadOnlyList<ColumnModel> rowDimensions)
    {
        if (totals.Count == 0) return rows;

        var result = rows.ToList();
        var visible = new HashSet<string>(
            columns.Select(column => column.Name),
            StringComparer.OrdinalIgnoreCase);
        var dimensionNames = new HashSet<string>(
            rowDimensions.Select(dimension => dimension.Name),
            StringComparer.OrdinalIgnoreCase);
        var firstVisibleDimension = rowDimensions.FirstOrDefault(dimension =>
            visible.Contains(dimension.Name));
        var functions = TotalFunctionOrder
            .Where(function => totals.Values.Any(byFunction => byFunction.ContainsKey(function)));
        foreach (var function in functions)
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var dimension in rowDimensions.Where(dimension => visible.Contains(dimension.Name)))
                row[dimension.Name] = ReferenceEquals(dimension, firstVisibleDimension)
                    ? $"{TotalLabel(function)}:"
                    : null;

            foreach (var column in columns.Where(column => !dimensionNames.Contains(column.Name)))
            {
                if (totals.TryGetValue(column.Name, out var byFunction)
                    && byFunction.TryGetValue(function, out var value))
                    row[column.Name] = value;
            }
            result.Add(row);
        }
        return result;
    }

    private static string TotalLabel(string function) => function switch
    {
        "avg" => "Average",
        "countDistinct" => "Count Distinct",
        _ => char.ToUpperInvariant(function[0]) + function[1..],
    };
}
