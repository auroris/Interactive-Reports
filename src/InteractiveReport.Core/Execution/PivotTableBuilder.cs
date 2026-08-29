using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Validation;

namespace InteractiveReport.Core.Execution;

/// <summary>
/// Transforms provider-neutral grouped rows into the Pivot response matrix. Cell
/// columns carry stable value-derived names — {metricId}@{JSON array of column-key
/// strings} — so per-cell presentation state survives data drift and spec reordering.
/// Clients treat the names as opaque keys; the server is their only author.
/// </summary>
internal static class PivotTableBuilder
{
    private static readonly string[] TotalFunctionOrder =
        ["sum", "avg", "median", "min", "max", "count", "countDistinct", "total"];

    private static readonly JsonSerializerOptions KeyJson = new()
    {
        // Match what a JSON-literate reader expects: only structurally required
        // escaping. Clients never regenerate these names — they copy them.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>One Pivot cell family: a declared metric or the implicit row count.</summary>
    private sealed record CellDef(
        string Id,
        string Label,
        string Type,
        string? FormatSource,
        bool IsComputed,
        int ValueOrdinal,
        int TotalsOrdinal);

    public static PivotTable Build(
        IReadOnlyList<PivotGroup> groups,
        ValidatedState state,
        int maxColumns,
        IReadOnlyList<PivotGroup>? totalGroups = null)
    {
        var rowDimensions = state.View.PivotRows;
        var cells = CellDefinitions(state.View);

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
                    state.View.ShapeProperty("cols"),
                    $"pivot would produce {columnKeys.Count} column groups (max {maxColumns}) — filter further or choose a lower-cardinality column dimension")]);
        }

        var keyNames = columnKeys.ToDictionary(
            key => key,
            KeyName,
            KeyComparer.Instance);

        var columns = ReportResultColumns.From(rowDimensions);
        foreach (var key in columnKeys)
        {
            var keyLabel = string.Join(" · ", key.Select(FormatKeyPart));
            foreach (var cell in cells)
            {
                var label = cells.Count == 1 && !cell.IsComputed
                    ? keyLabel
                    : $"{keyLabel} · {cell.Label}";
                columns.Add(new ColumnInfo(CellName(cell, keyNames[key]), label, cell.Type, cell.IsComputed)
                {
                    FormatSource = cell.FormatSource,
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

            var keyName = keyNames[group.ColumnKey];
            foreach (var cell in cells)
            {
                currentRow![CellName(cell, keyName)] = cell.ValueOrdinal < 0
                    ? group.Count
                    : group.Values[cell.ValueOrdinal];
            }
        }

        var totals = new Dictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in totalGroups ?? [])
        {
            if (!keyNames.TryGetValue(group.ColumnKey, out var keyName)) continue;

            foreach (var cell in cells)
            {
                if (cell.TotalsOrdinal < -1) continue;   // computed excluded from totals

                var function = cell.IsComputed
                    ? "total"
                    : cell.ValueOrdinal < 0
                        ? "count"
                        : ReportResultColumns.AggregateName(state.View.Values[cell.ValueOrdinal].Fn);
                var value = cell.TotalsOrdinal < 0 ? group.Count : group.Values[cell.TotalsOrdinal];
                totals[CellName(cell, keyName)] =
                    new Dictionary<string, object?> { [function] = value };
            }
        }

        return new PivotTable(columns, rows, totals);
    }

    /// <summary>
    /// The pivot's cell families: declared metrics in order, or implicit __count when
    /// no metric is declared.
    /// </summary>
    private static List<CellDef> CellDefinitions(ValidView view)
    {
        var cells = new List<CellDef>();
        for (var i = 0; i < view.Values.Count; i++)
        {
            var metric = view.Values[i];
            var aggregate = metric.ToAggregate();
            cells.Add(new CellDef(
                metric.Id,
                ReportResultColumns.AggregateLabel(aggregate),
                ReportResultColumns.AggregateType(aggregate),
                ReportResultColumns.FormatSource(aggregate),
                IsComputed: false,
                ValueOrdinal: i,
                TotalsOrdinal: i));
        }

        if (cells.Count == 0)
        {
            cells.Add(new CellDef(
                "__count",
                "Count",
                "number",
                FormatSource: null,
                IsComputed: false,
                ValueOrdinal: -1,
                TotalsOrdinal: -1));
        }
        return cells;
    }

    private static string CellName(CellDef cell, string keyName) => $"{cell.Id}@{keyName}";

    /// <summary>
    /// The canonical column-key encoding: a compact JSON array of invariant value
    /// strings (null stays null). Deterministic and collision-free; the human-facing
    /// form lives in the column label, never in the name.
    /// </summary>
    private static string KeyName(object?[] key)
        => JsonSerializer.Serialize(
            key.Select(part => part is null ? null : Convert.ToString(part, CultureInfo.InvariantCulture)),
            KeyJson);

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
