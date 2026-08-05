using System.Data.Common;
using System.Diagnostics;
using InteractiveReport.Core.Composition;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Schema;
using InteractiveReport.Core.Validation;
using SqlKata.Compilers;

namespace InteractiveReport.Core.Execution;

/// <summary>
/// Orchestrates one report request: discover schema (cached) → validate state → compose →
/// compile → execute → shape the result. All derived queries clone one filtered core and
/// run sequentially on a single connection per request (SQLite-friendly; per-dialect
/// parallelism is a later optimization).
///
/// Views: grid is the full pipeline; groupBy executes the grouped query with group-count
/// pagination; pivot fetches the grouped rows+cols matrix source (capped) and transforms
/// in memory.
/// </summary>
public sealed class ReportExecutor
{
    /// <summary>Hard ceiling on pivot source groups regardless of definition settings.</summary>
    public const int MaxPivotGroups = 10_000;

    private readonly IReportConnectionFactory _connections;
    private readonly SchemaCache _schemaCache;
    private readonly Microsoft.Extensions.Logging.ILogger? _logger;

    public ReportExecutor(
        IReportConnectionFactory connections,
        SchemaCache schemaCache,
        Microsoft.Extensions.Logging.ILogger<ReportExecutor>? logger = null)
    {
        _connections = connections;
        _schemaCache = schemaCache;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ColumnModel>> GetSchema(
        ReportDefinition def,
        IReadOnlyDictionary<string, object?> contextParams,
        CancellationToken ct = default)
    {
        return await _schemaCache.GetOrDiscover(def.Name, async () =>
        {
            await using var conn = _connections.CreateConnection(def.Connection);
            await conn.OpenAsync(ct);
            return await SchemaDiscovery.Discover(conn, def, contextParams, ct);
        });
    }

    public async Task<ReportResult> Query(
        ReportDefinition def,
        ReportState state,
        IReadOnlyDictionary<string, object?> contextParams,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var schema = await GetSchema(def, contextParams, ct);
        var validated = StateValidator.Validate(def, state, schema);

        return validated.View.Mode switch
        {
            ViewMode.GroupBy => await QueryGroupBy(def, validated, contextParams, sw, ct),
            ViewMode.Pivot => await QueryPivot(def, validated, contextParams, sw, ct),
            _ => await QueryGrid(def, validated, contextParams, sw, ct),
        };
    }

    /// <summary>Same validated state, no paging, capped at MaxRows with truncation signaling.</summary>
    public async Task<ExportResult> Export(
        ReportDefinition def,
        ReportState state,
        IReadOnlyDictionary<string, object?> contextParams,
        CancellationToken ct = default)
    {
        var schema = await GetSchema(def, contextParams, ct);
        var validated = StateValidator.Validate(def, state, schema);
        var compiler = DialectSupport.GetCompiler(def.Dialect);

        if (validated.View.Mode == ViewMode.Pivot)
        {
            var sw = Stopwatch.StartNew();
            var pivot = await QueryPivot(def, validated, contextParams, sw, ct);
            return new ExportResult(pivot.Columns, pivot.Rows, Truncated: false);
        }

        await using var conn = _connections.CreateConnection(def.Connection);
        await conn.OpenAsync(ct);

        if (validated.View.Mode == ViewMode.GroupBy)
        {
            var query = QueryComposer.ComposeGroupByExport(def, validated, def.MaxRows);
            var (rows, truncated) = await ReadGroupedShaped(conn, compiler, query, validated, contextParams, def, def.MaxRows, ct);
            return new ExportResult(GroupByColumns(validated), rows, truncated);
        }

        var grid = QueryComposer.ComposeGridExport(def, validated, def.MaxRows);
        var gridRows = await ReadRows(conn, compiler, grid, contextParams, def, ct);
        var gridTruncated = gridRows.Count > def.MaxRows;
        if (gridTruncated) gridRows.RemoveAt(gridRows.Count - 1);

        return new ExportResult(
            validated.SelectColumns.Select(c => new ColumnInfo(c.Name, c.Label, c.KindName, c.IsComputed)).ToList(),
            gridRows,
            gridTruncated);
    }

    // --- grid ----------------------------------------------------------------

    private async Task<ReportResult> QueryGrid(
        ReportDefinition def,
        ValidatedState validated,
        IReadOnlyDictionary<string, object?> contextParams,
        Stopwatch sw,
        CancellationToken ct)
    {
        var composed = QueryComposer.Compose(def, validated);
        var compiler = DialectSupport.GetCompiler(def.Dialect);

        await using var conn = _connections.CreateConnection(def.Connection);
        await conn.OpenAsync(ct);

        long totalRows;
        await using (var countCmd = CommandBuilder.Build(conn, compiler.Compile(composed.Count), contextParams, def, _logger))
        {
            totalRows = Convert.ToInt64(await countCmd.ExecuteScalarAsync(ct));
        }

        var aggregates = composed.Aggregates is null
            ? new Dictionary<string, IReadOnlyDictionary<string, object?>>()
            : await ReadAggregates(conn, compiler, composed.Aggregates, validated, contextParams, def, ct);

        var breakTotals = composed.BreakTotals is null
            ? (IReadOnlyList<BreakTotal>)[]
            : await ReadBreakTotals(conn, compiler, composed.BreakTotals, validated, contextParams, def, ct);

        var rows = await ReadRows(conn, compiler, composed.Page, contextParams, def, ct);

        var highlights = validated.Highlights.Count > 0
            ? HighlightEvaluator.Evaluate(validated.Highlights, rows)
            : [];

        sw.Stop();

        return new ReportResult
        {
            Columns = validated.SelectColumns
                .Select(c => new ColumnInfo(c.Name, c.Label, c.KindName, c.IsComputed))
                .ToList(),
            Rows = rows,
            Page = new PageRequest { Index = validated.PageIndex, Size = validated.PageSize },
            TotalRows = totalRows,
            Aggregates = aggregates,
            BreakTotals = breakTotals,
            Highlights = highlights,
            Ignored = validated.Ignored,
            ElapsedMs = sw.ElapsedMilliseconds,
        };
    }

    // --- groupBy view --------------------------------------------------------

    private async Task<ReportResult> QueryGroupBy(
        ReportDefinition def,
        ValidatedState validated,
        IReadOnlyDictionary<string, object?> contextParams,
        Stopwatch sw,
        CancellationToken ct)
    {
        var (page, count) = QueryComposer.ComposeGroupByView(def, validated);
        var compiler = DialectSupport.GetCompiler(def.Dialect);

        await using var conn = _connections.CreateConnection(def.Connection);
        await conn.OpenAsync(ct);

        long totalGroups;
        await using (var countCmd = CommandBuilder.Build(conn, compiler.Compile(count), contextParams, def, _logger))
        {
            totalGroups = Convert.ToInt64(await countCmd.ExecuteScalarAsync(ct));
        }

        var (rows, _) = await ReadGroupedShaped(conn, compiler, page, validated, contextParams, def, maxRows: null, ct);

        sw.Stop();

        return new ReportResult
        {
            Columns = GroupByColumns(validated),
            Rows = rows,
            Page = new PageRequest { Index = validated.PageIndex, Size = validated.PageSize },
            TotalRows = totalGroups,
            Ignored = validated.Ignored,
            ElapsedMs = sw.ElapsedMilliseconds,
        };
    }

    private static List<ColumnInfo> GroupByColumns(ValidatedState validated)
    {
        var columns = validated.View.GroupBy
            .Select(d => new ColumnInfo(d.Name, d.Label, d.KindName, d.IsComputed))
            .ToList();
        columns.Add(new ColumnInfo("__count", "Count", "number", false));
        for (var i = 0; i < validated.View.Values.Count; i++)
            columns.Add(ValueColumn(validated.View.Values[i], $"v{i}"));
        return columns;
    }

    /// <summary>Reads the shared grouped layout (dims, __rows, a0..aN) into flat dicts.</summary>
    private async Task<(List<IReadOnlyDictionary<string, object?>> Rows, bool Truncated)> ReadGroupedShaped(
        DbConnection conn,
        Compiler compiler,
        SqlKata.Query query,
        ValidatedState validated,
        IReadOnlyDictionary<string, object?> contextParams,
        ReportDefinition def,
        int? maxRows,
        CancellationToken ct)
    {
        var dims = validated.View.GroupBy;
        var values = validated.View.Values;

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        await using var cmd = CommandBuilder.Build(conn, compiler.Compile(query), contextParams, def, _logger);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < dims.Count; i++)
                row[dims[i].Name] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            row["__count"] = Convert.ToInt64(reader.GetValue(dims.Count));
            for (var i = 0; i < values.Count; i++)
            {
                var ordinal = dims.Count + 1 + i;
                row[$"v{i}"] = reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal);
            }
            rows.Add(row);
        }

        var truncated = maxRows.HasValue && rows.Count > maxRows.Value;
        if (truncated) rows.RemoveAt(rows.Count - 1);
        return (rows, truncated);
    }

    // --- pivot view ----------------------------------------------------------

    private async Task<ReportResult> QueryPivot(
        ReportDefinition def,
        ValidatedState validated,
        IReadOnlyDictionary<string, object?> contextParams,
        Stopwatch sw,
        CancellationToken ct)
    {
        var source = QueryComposer.ComposePivotSource(def, validated, MaxPivotGroups);
        var compiler = DialectSupport.GetCompiler(def.Dialect);

        var rowDims = validated.View.PivotRows;
        var colDims = validated.View.PivotCols;
        var values = validated.View.Values;
        var dimCount = rowDims.Count + colDims.Count;

        var groups = new List<(object?[] RowKey, object?[] ColKey, long Count, object?[] Values)>();
        await using (var conn = _connections.CreateConnection(def.Connection))
        {
            await conn.OpenAsync(ct);
            await using var cmd = CommandBuilder.Build(conn, compiler.Compile(source), contextParams, def, _logger);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var rowKey = new object?[rowDims.Count];
                for (var i = 0; i < rowDims.Count; i++)
                    rowKey[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);

                var colKey = new object?[colDims.Count];
                for (var i = 0; i < colDims.Count; i++)
                    colKey[i] = reader.IsDBNull(rowDims.Count + i) ? null : reader.GetValue(rowDims.Count + i);

                var groupCount = Convert.ToInt64(reader.GetValue(dimCount));

                var vals = new object?[values.Count];
                for (var i = 0; i < values.Count; i++)
                    vals[i] = reader.IsDBNull(dimCount + 1 + i) ? null : reader.GetValue(dimCount + 1 + i);

                groups.Add((rowKey, colKey, groupCount, vals));
            }
        }

        if (groups.Count > MaxPivotGroups)
            throw new ReportValidationException(
                [new ValidationError("view", $"pivot source exceeds {MaxPivotGroups} groups — filter further or choose lower-cardinality dimensions")]);

        // Distinct column keys, ordered; source arrives sorted by row dims first, so
        // first-seen order is not global — sort explicitly.
        var colKeys = groups.Select(g => g.ColKey)
            .Distinct(KeyComparer.Instance)
            .OrderBy(k => k, KeyOrdering.Instance)
            .ToList();

        if (colKeys.Count > def.MaxPivotColumns)
            throw new ReportValidationException(
                [new ValidationError("view.cols",
                    $"pivot would produce {colKeys.Count} column groups (max {def.MaxPivotColumns}) — filter further or choose a lower-cardinality column dimension")]);

        var colKeyIndex = new Dictionary<object?[], int>(KeyComparer.Instance);
        for (var i = 0; i < colKeys.Count; i++) colKeyIndex[colKeys[i]] = i;

        // Implicit count when no values requested.
        var valueLabels = values.Count > 0
            ? values.Select(v => ValueColumn(v, "").Label).ToList()
            : ["count"];
        var valuesPerKey = valueLabels.Count;

        var columns = rowDims.Select(d => new ColumnInfo(d.Name, d.Label, d.KindName, d.IsComputed)).ToList();
        for (var k = 0; k < colKeys.Count; k++)
        {
            var keyLabel = string.Join(" · ", colKeys[k].Select(v => v?.ToString() ?? "(blank)"));
            for (var v = 0; v < valuesPerKey; v++)
            {
                var label = valuesPerKey == 1 ? keyLabel : $"{keyLabel} · {valueLabels[v]}";
                var type = values.Count == 0 ? "number" : ValueColumn(values[v], "").Type;
                columns.Add(new ColumnInfo($"p{k}_{v}", label, type, false));
            }
        }

        var pivotRows = new List<IReadOnlyDictionary<string, object?>>();
        Dictionary<string, object?>? current = null;
        object?[]? currentKey = null;
        foreach (var g in groups)
        {
            if (currentKey is null || !KeyComparer.Instance.Equals(currentKey, g.RowKey))
            {
                current = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < rowDims.Count; i++)
                    current[rowDims[i].Name] = g.RowKey[i];
                pivotRows.Add(current);
                currentKey = g.RowKey;
            }

            var k = colKeyIndex[g.ColKey];
            for (var v = 0; v < valuesPerKey; v++)
                current![$"p{k}_{v}"] = values.Count == 0 ? g.Count : g.Values[v];
        }

        sw.Stop();

        return new ReportResult
        {
            Columns = columns,
            Rows = pivotRows,
            Page = new PageRequest { Index = 1, Size = Math.Max(1, pivotRows.Count) },
            TotalRows = pivotRows.Count,
            Ignored = validated.Ignored,
            ElapsedMs = sw.ElapsedMilliseconds,
        };
    }

    private static ColumnInfo ValueColumn(ValidAggregate v, string name)
    {
        var fn = System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(v.Fn.ToString());
        var type = v.Fn is AggregateFn.Min or AggregateFn.Max ? v.Column.KindName : "number";
        return new ColumnInfo(name, $"{fn}({v.Column.Label})", type, false);
    }

    private sealed class KeyComparer : IEqualityComparer<object?[]>
    {
        public static readonly KeyComparer Instance = new();

        public bool Equals(object?[]? x, object?[]? y)
        {
            if (x is null || y is null || x.Length != y.Length) return false;
            for (var i = 0; i < x.Length; i++)
                if (!System.Collections.Generic.EqualityComparer<object?>.Default.Equals(x[i], y[i])) return false;
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
                var cmp = ComparePart(x[i], y[i]);
                if (cmp != 0) return cmp;
            }
            return x.Length.CompareTo(y.Length);
        }

        private static int ComparePart(object? a, object? b)
        {
            if (a is null || b is null) return (a is not null).CompareTo(b is not null);   // nulls first
            if (a.GetType() == b.GetType() && a is IComparable comparable) return comparable.CompareTo(b);
            return string.CompareOrdinal(
                Convert.ToString(a, System.Globalization.CultureInfo.InvariantCulture),
                Convert.ToString(b, System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    // --- shared readers ------------------------------------------------------

    private async Task<List<IReadOnlyDictionary<string, object?>>> ReadRows(
        DbConnection conn,
        Compiler compiler,
        SqlKata.Query query,
        IReadOnlyDictionary<string, object?> contextParams,
        ReportDefinition def,
        CancellationToken ct)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        await using var cmd = CommandBuilder.Build(conn, compiler.Compile(query), contextParams, def, _logger);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>Single-row aggregate query: aliases a0..aN in validated-aggregate order.</summary>
    private async Task<Dictionary<string, IReadOnlyDictionary<string, object?>>> ReadAggregates(
        DbConnection conn,
        Compiler compiler,
        SqlKata.Query query,
        ValidatedState validated,
        IReadOnlyDictionary<string, object?> contextParams,
        ReportDefinition def,
        CancellationToken ct)
    {
        await using var cmd = CommandBuilder.Build(conn, compiler.Compile(query), contextParams, def, _logger);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var values = new object?[validated.Aggregates.Count];
        if (await reader.ReadAsync(ct))
        {
            for (var i = 0; i < values.Length; i++)
                values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        }
        return Nest(validated.Aggregates, i => values[i]);
    }

    /// <summary>Break totals: break columns, then [__rows], then a0..aN — read by ordinal.</summary>
    private async Task<List<BreakTotal>> ReadBreakTotals(
        DbConnection conn,
        Compiler compiler,
        SqlKata.Query query,
        ValidatedState validated,
        IReadOnlyDictionary<string, object?> contextParams,
        ReportDefinition def,
        CancellationToken ct)
    {
        await using var cmd = CommandBuilder.Build(conn, compiler.Compile(query), contextParams, def, _logger);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var breakCount = validated.Breaks.Count;
        var result = new List<BreakTotal>();
        while (await reader.ReadAsync(ct))
        {
            var key = new Dictionary<string, object?>(breakCount, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < breakCount; i++)
                key[validated.Breaks[i].Name] = reader.IsDBNull(i) ? null : reader.GetValue(i);

            var rowCount = Convert.ToInt64(reader.GetValue(breakCount));

            var offset = breakCount + 1;
            var aggregates = Nest(validated.Aggregates,
                i => reader.IsDBNull(offset + i) ? null : reader.GetValue(offset + i));

            result.Add(new BreakTotal(key, rowCount, aggregates));
        }
        return result;
    }

    /// <summary>Flat aggregate values → column → camelCase fn → value.</summary>
    private static Dictionary<string, IReadOnlyDictionary<string, object?>> Nest(
        IReadOnlyList<ValidAggregate> aggregates,
        Func<int, object?> valueAt)
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < aggregates.Count; i++)
        {
            var agg = aggregates[i];
            if (result.TryGetValue(agg.Column.Name, out var existing))
                ((Dictionary<string, object?>)existing)[FnName(agg.Fn)] = valueAt(i);
            else
                result[agg.Column.Name] = new Dictionary<string, object?> { [FnName(agg.Fn)] = valueAt(i) };
        }
        return result;
    }

    private static string FnName(AggregateFn fn)
        => System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(fn.ToString());
}

/// <summary>Unpaged export payload; Truncated means MaxRows was hit and rows were cut there.</summary>
public sealed record ExportResult(
    IReadOnlyList<ColumnInfo> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    bool Truncated);
