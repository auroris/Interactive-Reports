using System.Globalization;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Validation;

namespace InteractiveReport.Core.Execution;

/// <summary>Transforms provider-neutral grouped rows into the pivot response matrix.</summary>
internal static class PivotTableBuilder
{
    private static readonly string[] TotalFunctionOrder =
        ["sum", "avg", "median", "min", "max", "count", "countDistinct"];

    public static PivotTable Build(
        IReadOnlyList<PivotGroup> groups,
        ValidatedState state,
        int maxColumns,
        IReadOnlyList<PivotGroup>? totalGroups = null)
    {
        var rowDimensions = state.View.PivotRows;
        var values = state.View.Values;

        // Source rows are ordered by row dimensions first, so first-seen column-key
        // order is not global. Sort distinct keys explicitly.
        var columnKeys = groups
            .Select(group => group.ColumnKey)
            .Distinct(KeyComparer.Instance)
            .OrderBy(key => key, KeyOrdering.Instance)
            .ToList();

        if (columnKeys.Count > maxColumns)
        {
            throw new ReportValidationException(
                [new ValidationError(
                    "view.cols",
                    $"pivot would produce {columnKeys.Count} column groups (max {maxColumns}) — filter further or choose a lower-cardinality column dimension")]);
        }

        var columnKeyIndexes = new Dictionary<object?[], int>(KeyComparer.Instance);
        for (var i = 0; i < columnKeys.Count; i++)
            columnKeyIndexes[columnKeys[i]] = i;

        // An empty value list means one implicit count cell per column key.
        var valueLabels = values.Count > 0
            ? values.Select(ReportResultColumns.AggregateLabel).ToList()
            : ["count"];
        var valuesPerKey = valueLabels.Count;

        var columns = ReportResultColumns.From(rowDimensions);
        for (var keyIndex = 0; keyIndex < columnKeys.Count; keyIndex++)
        {
            var keyLabel = string.Join(" · ", columnKeys[keyIndex].Select(FormatKeyPart));
            for (var valueIndex = 0; valueIndex < valuesPerKey; valueIndex++)
            {
                var label = valuesPerKey == 1
                    ? keyLabel
                    : $"{keyLabel} · {valueLabels[valueIndex]}";
                var type = values.Count == 0
                    ? "number"
                    : ReportResultColumns.AggregateType(values[valueIndex]);
                columns.Add(new ColumnInfo($"p{keyIndex}_{valueIndex}", label, type, false)
                {
                    FormatSource = values.Count == 0
                        ? null
                        : ReportResultColumns.FormatSource(values[valueIndex]),
                });
            }
        }

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        Dictionary<string, object?>? currentRow = null;
        object?[]? currentKey = null;
        foreach (var group in groups)
        {
            if (currentKey is null || !KeyComparer.Instance.Equals(currentKey, group.RowKey))
            {
                currentRow = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < rowDimensions.Count; i++)
                    currentRow[rowDimensions[i].Name] = group.RowKey[i];
                rows.Add(currentRow);
                currentKey = group.RowKey;
            }

            var columnIndex = columnKeyIndexes[group.ColumnKey];
            for (var valueIndex = 0; valueIndex < valuesPerKey; valueIndex++)
            {
                currentRow![$"p{columnIndex}_{valueIndex}"] = values.Count == 0
                    ? group.Count
                    : group.Values[valueIndex];
            }
        }

        var totals = new Dictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in totalGroups ?? [])
        {
            if (!columnKeyIndexes.TryGetValue(group.ColumnKey, out var columnIndex)) continue;

            for (var valueIndex = 0; valueIndex < valuesPerKey; valueIndex++)
            {
                var function = values.Count == 0
                    ? "count"
                    : ReportResultColumns.AggregateName(values[valueIndex].Fn);
                var value = values.Count == 0 ? group.Count : group.Values[valueIndex];
                totals[$"p{columnIndex}_{valueIndex}"] =
                    new Dictionary<string, object?> { [function] = value };
            }
        }

        return new PivotTable(columns, rows, totals);
    }

    /// <summary>
    /// CSV has no separate footer channel, so materialize the same total rows the
    /// browser renders from ReportResult.Aggregates. The first row dimension carries
    /// the label; remaining dimension cells stay empty.
    /// </summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> RowsForExport(
        IReadOnlyList<ColumnInfo> columns,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> totals,
        IReadOnlyList<ColumnModel> rowDimensions)
    {
        if (totals.Count == 0) return rows;

        var result = rows.ToList();
        var functions = TotalFunctionOrder
            .Where(function => totals.Values.Any(byFunction => byFunction.ContainsKey(function)));
        foreach (var function in functions)
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var dimensionIndex = 0; dimensionIndex < rowDimensions.Count; dimensionIndex++)
                row[rowDimensions[dimensionIndex].Name] = dimensionIndex == 0 ? $"{TotalLabel(function)}:" : null;

            foreach (var column in columns.Skip(rowDimensions.Count))
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

    private static string FormatKeyPart(object? value)
        => value is null ? "(blank)" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";

    private sealed class KeyComparer : IEqualityComparer<object?[]>
    {
        public static readonly KeyComparer Instance = new();

        public bool Equals(object?[]? x, object?[]? y)
        {
            if (x is null || y is null || x.Length != y.Length) return false;
            for (var i = 0; i < x.Length; i++)
            {
                if (!EqualityComparer<object?>.Default.Equals(x[i], y[i])) return false;
            }
            return true;
        }

        public int GetHashCode(object?[] key)
        {
            var hash = new HashCode();
            foreach (var part in key) hash.Add(part);
            return hash.ToHashCode();
        }
    }

    private sealed class KeyOrdering : IComparer<object?[]>
    {
        public static readonly KeyOrdering Instance = new();

        public int Compare(object?[]? x, object?[]? y)
        {
            if (x is null || y is null) return (x is null).CompareTo(y is null);
            for (var i = 0; i < Math.Min(x.Length, y.Length); i++)
            {
                var comparison = ComparePart(x[i], y[i]);
                if (comparison != 0) return comparison;
            }
            return x.Length.CompareTo(y.Length);
        }

        private static int ComparePart(object? left, object? right)
        {
            if (left is null || right is null)
                return (left is not null).CompareTo(right is not null); // Nulls first.
            if (left.GetType() == right.GetType() && left is IComparable comparable)
                return comparable.CompareTo(right);
            return string.CompareOrdinal(
                Convert.ToString(left, CultureInfo.InvariantCulture),
                Convert.ToString(right, CultureInfo.InvariantCulture));
        }
    }
}

internal sealed record PivotTable(
    IReadOnlyList<ColumnInfo> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> Totals);
