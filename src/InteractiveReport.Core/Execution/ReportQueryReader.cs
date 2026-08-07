using System.Data.Common;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Validation;
using Microsoft.Extensions.Logging;
using SqlKata;
using SqlKata.Compilers;

namespace InteractiveReport.Core.Execution;

/// <summary>
/// Compiles and materializes report queries over one prepared connection. This keeps
/// provider command handling and ordinal-based result layouts out of the request
/// orchestrator.
/// </summary>
internal sealed class ReportQueryReader(
    DbConnection connection,
    Compiler compiler,
    IReadOnlyDictionary<string, object?> contextParams,
    ReportDefinition definition,
    ILogger? logger)
{
    public async Task<long> ReadCount(Query query, CancellationToken ct)
    {
        await using var command = Build(query);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
    }

    public async Task<QueryRows> ReadRows(Query query, int? maxRows, CancellationToken ct)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        await using var command = Build(query);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = ValueAt(reader, i);
            rows.Add(row);
        }

        return ApplyLimit(rows, maxRows);
    }

    /// <summary>
    /// Reads the chart's stable ordinal shape without assigning source or synthetic
    /// column names. Chart result shaping chooses collision-free protocol keys later,
    /// so a legitimate label named "v0" or "__count" cannot be overwritten.
    /// </summary>
    public async Task<List<ChartPoint>> ReadChartPoints(
        Query query,
        int metricOrdinal,
        CancellationToken ct)
    {
        var points = new List<ChartPoint>();
        await using var command = Build(query);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            points.Add(new ChartPoint(ValueAt(reader, 0), ValueAt(reader, metricOrdinal)));

        return points;
    }

    /// <summary>Reads the shared grouped layout: dimensions, __rows, then a0..aN.</summary>
    public async Task<QueryRows> ReadGroupedRows(
        Query query,
        IReadOnlyList<ColumnModel> dimensions,
        int valueCount,
        int? maxRows,
        CancellationToken ct)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        await using var command = Build(query);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < dimensions.Count; i++)
                row[dimensions[i].Name] = ValueAt(reader, i);
            row["__count"] = Convert.ToInt64(reader.GetValue(dimensions.Count));
            for (var i = 0; i < valueCount; i++)
                row[$"v{i}"] = ValueAt(reader, dimensions.Count + 1 + i);
            rows.Add(row);
        }

        return ApplyLimit(rows, maxRows);
    }

    /// <summary>Reads a single-row aggregate query whose aliases are a0..aN.</summary>
    public async Task<Dictionary<string, IReadOnlyDictionary<string, object?>>> ReadAggregates(
        Query query,
        IReadOnlyList<ValidAggregate> aggregates,
        CancellationToken ct)
    {
        await using var command = Build(query);
        await using var reader = await command.ExecuteReaderAsync(ct);

        var values = new object?[aggregates.Count];
        if (await reader.ReadAsync(ct))
        {
            for (var i = 0; i < values.Length; i++)
                values[i] = ValueAt(reader, i);
        }
        return NestAggregates(aggregates, i => values[i]);
    }

    /// <summary>Reads break columns, __rows, then a0..aN.</summary>
    public async Task<List<BreakTotal>> ReadBreakTotals(
        Query query,
        IReadOnlyList<ColumnModel> breaks,
        IReadOnlyList<ValidAggregate> aggregates,
        CancellationToken ct)
    {
        await using var command = Build(query);
        await using var reader = await command.ExecuteReaderAsync(ct);

        var result = new List<BreakTotal>();
        while (await reader.ReadAsync(ct))
        {
            var key = new Dictionary<string, object?>(breaks.Count, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < breaks.Count; i++)
                key[breaks[i].Name] = ValueAt(reader, i);

            var rowCount = Convert.ToInt64(reader.GetValue(breaks.Count));
            var offset = breaks.Count + 1;
            var aggregateValues = NestAggregates(aggregates, i => ValueAt(reader, offset + i));
            result.Add(new BreakTotal(key, rowCount, aggregateValues));
        }
        return result;
    }

    public async Task<List<PivotGroup>> ReadPivotGroups(
        Query query,
        int rowDimensionCount,
        int columnDimensionCount,
        int valueCount,
        CancellationToken ct)
    {
        var dimensionCount = rowDimensionCount + columnDimensionCount;
        var groups = new List<PivotGroup>();

        await using var command = Build(query);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var rowKey = ReadKey(reader, 0, rowDimensionCount);
            var columnKey = ReadKey(reader, rowDimensionCount, columnDimensionCount);
            var count = Convert.ToInt64(reader.GetValue(dimensionCount));
            var values = ReadKey(reader, dimensionCount + 1, valueCount);
            groups.Add(new PivotGroup(rowKey, columnKey, count, values));
        }

        return groups;
    }

    private DbCommand Build(Query query)
        => CommandBuilder.Build(connection, compiler.Compile(query), contextParams, definition, logger);

    private static object?[] ReadKey(DbDataReader reader, int offset, int count)
    {
        var values = new object?[count];
        for (var i = 0; i < count; i++)
            values[i] = ValueAt(reader, offset + i);
        return values;
    }

    private static object? ValueAt(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal);

    private static QueryRows ApplyLimit(
        List<IReadOnlyDictionary<string, object?>> rows,
        int? maxRows)
    {
        var truncated = maxRows.HasValue && rows.Count > maxRows.Value;
        if (truncated) rows.RemoveAt(rows.Count - 1);
        return new QueryRows(rows, truncated);
    }

    private static Dictionary<string, IReadOnlyDictionary<string, object?>> NestAggregates(
        IReadOnlyList<ValidAggregate> aggregates,
        Func<int, object?> valueAt)
    {
        var result = new Dictionary<string, Dictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < aggregates.Count; i++)
        {
            var aggregate = aggregates[i];
            if (!result.TryGetValue(aggregate.Column.Name, out var values))
            {
                values = new Dictionary<string, object?>();
                result[aggregate.Column.Name] = values;
            }
            values[ReportResultColumns.AggregateName(aggregate.Fn)] = valueAt(i);
        }
        return result.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyDictionary<string, object?>)pair.Value,
            StringComparer.OrdinalIgnoreCase);
    }
}

internal sealed record QueryRows(
    List<IReadOnlyDictionary<string, object?>> Rows,
    bool Truncated);

internal sealed record ChartPoint(object? Label, object? Value);

internal sealed record PivotGroup(
    object?[] RowKey,
    object?[] ColumnKey,
    long Count,
    object?[] Values);
