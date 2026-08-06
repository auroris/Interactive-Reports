using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Execution;

/// <summary>
/// Shapes execution rows into protocol rows. Query-private projections such as
/// highlight markers never cross this boundary.
/// </summary>
internal static class ReportRowProjector
{
    public static List<IReadOnlyDictionary<string, object?>> VisibleColumns(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        IReadOnlyList<ColumnModel> columns)
    {
        var result = new List<IReadOnlyDictionary<string, object?>>(rows.Count);
        foreach (var row in rows)
        {
            var visible = new Dictionary<string, object?>(columns.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var column in columns)
                visible[column.Name] = row.TryGetValue(column.Name, out var value) ? value : null;
            result.Add(visible);
        }
        return result;
    }
}
