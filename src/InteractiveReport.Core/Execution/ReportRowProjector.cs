using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Execution;

/// <summary>
/// Shapes execution rows into protocol rows. Query-private projections such as
/// highlight markers never cross this boundary.
/// </summary>
internal static class ReportRowProjector
{
    /// <summary>
    /// Projects each execution row onto the ordered public columns, filling missing values with <see langword="null"/>.
    /// </summary>
    /// <param name="rows">The execution rows, which may contain private helper projections.</param>
    /// <param name="columns">The public columns to retain, in result order.</param>
    /// <returns>New case-insensitive row dictionaries containing only the requested public columns.</returns>
    public static List<IReadOnlyDictionary<string, object?>> Columns(
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
