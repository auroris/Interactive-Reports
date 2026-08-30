using System.Data.Common;
using InteractiveReport.Core.Composition;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Validation;
using Microsoft.Extensions.Logging;
using SqlKata;
using SqlKata.Compilers;

namespace InteractiveReport.Core.Execution;

/// <summary>
/// Compiles and materializes report queries over one prepared connection. This keeps
/// provider command handling and ordinal-based result layouts out of the request
/// orchestrator. When the provider represents a configured read scope as an ADO.NET
/// transaction, every command joins it explicitly. Oracle also uses its multi-cursor
/// batch to transport the logical result sets in one round trip.
/// </summary>
internal sealed class ReportQueryReader(
    DbConnection connection,
    Compiler compiler,
    IReadOnlyDictionary<string, object?> contextParams,
    ReportDefinition definition,
    ILogger? logger,
    DbTransaction? transaction = null)
{
    /// <summary>
    /// Reads the common SQL-backed terminal-table datasets: a count, optional footer
    /// aggregates, optional break subtotals, and rows. Oracle snapshot mode sends one
    /// anonymous PL/SQL block and receives ordered REF CURSORs; every other mode uses
    /// the same logical contract over ordinary commands.
    /// </summary>
    public async Task<TableQueryRows> ReadTableQueries(
        TerminalExecutionBundle bundle,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        return await ReadTableQueries(
            new TerminalQueries(
                bundle.MainRows.Query,
                bundle.Count,
                bundle.FooterAggregates?.Query,
                bundle.BreakTotals?.Query),
            bundle.BreakTotals?.BreakColumns ?? [],
            bundle.FooterAggregates?.Aggregates
                ?? bundle.BreakTotals?.Aggregates
                ?? [],
            bundle.MainRows.PublicNames,
            ct);
    }

    private async Task<TableQueryRows> ReadTableQueries(
        TerminalQueries queries,
        IReadOnlyList<ColumnModel> breaks,
        IReadOnlyList<ValidAggregate> aggregates,
        IReadOnlyList<string>? pagePublicNames,
        CancellationToken ct)
    {
        if (!UseOracleCursorBatch)
        {
            var totalRows = await ReadCount(queries.Count, ct);
            var footerValues = queries.FooterAggregates is null
                ? new Dictionary<string, IReadOnlyDictionary<string, object?>>()
                : await ReadAggregates(queries.FooterAggregates, aggregates, ct);
            var breakTotals = queries.BreakTotals is null
                ? []
                : await ReadBreakTotals(queries.BreakTotals, breaks, aggregates, ct);
            var rows = (pagePublicNames is null
                ? await ReadRows(queries.MainRows, maxRows: null, ct)
                : await ReadRows(queries.MainRows, pagePublicNames, maxRows: null, ct)).Rows;
            return new TableQueryRows(totalRows, footerValues, breakTotals, rows);
        }

        var resultSets = new List<Query> { queries.Count };
        if (queries.FooterAggregates is not null) resultSets.Add(queries.FooterAggregates);
        if (queries.BreakTotals is not null) resultSets.Add(queries.BreakTotals);
        resultSets.Add(queries.MainRows);

        await using var command = BuildOracleBatch(resultSets);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var total = await MaterializeCount(reader, ct);

        Dictionary<string, IReadOnlyDictionary<string, object?>> aggregateValues = [];
        if (queries.FooterAggregates is not null)
        {
            await RequireNextResult(reader, ct);
            aggregateValues = await MaterializeAggregates(reader, aggregates, ct);
        }

        List<BreakTotal> breakValues = [];
        if (queries.BreakTotals is not null)
        {
            await RequireNextResult(reader, ct);
            breakValues = await MaterializeBreakTotals(reader, breaks, aggregates, ct);
        }

        await RequireNextResult(reader, ct);
        var pageRows = (pagePublicNames is null
            ? await MaterializeRows(reader, maxRows: null, ct)
            : await MaterializeRows(reader, pagePublicNames, maxRows: null, ct)).Rows;
        await RequireEnd(reader, ct);
        return new TableQueryRows(total, aggregateValues, breakValues, pageRows);
    }

    private async Task<long> ReadCount(Query query, CancellationToken ct)
    {
        await using var command = Build(query);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
    }

    public async Task<QueryRows> ReadRows(Query query, int? maxRows, CancellationToken ct)
    {
        await using var command = Build(query);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await MaterializeRows(reader, maxRows, ct);
    }

    /// <summary>
    /// Reads a server-authored physical projection into public protocol names by
    /// ordinal. This is required for Pivot cells: their public, data-derived names
    /// are dictionary keys only and are never emitted as SQL identifiers.
    /// </summary>
    public async Task<QueryRows> ReadRows(
        Query query,
        IReadOnlyList<string> publicNames,
        int? maxRows,
        CancellationToken ct)
    {
        await using var command = Build(query);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (reader.FieldCount != publicNames.Count)
            throw new InvalidOperationException(
                $"The composed table returned {reader.FieldCount} columns for a {publicNames.Count}-column projection.");

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>(publicNames.Count, StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < publicNames.Count; index++)
                row[publicNames[index]] = ValueAt(reader, index);
            rows.Add(row);
        }
        return ApplyLimit(rows, maxRows);
    }

    /// <summary>Reads a single-row aggregate query whose aliases are a0..aN.</summary>
    private async Task<Dictionary<string, IReadOnlyDictionary<string, object?>>> ReadAggregates(
        Query query,
        IReadOnlyList<ValidAggregate> aggregates,
        CancellationToken ct)
    {
        await using var command = Build(query);
        await using var reader = await command.ExecuteReaderAsync(ct);

        return await MaterializeAggregates(reader, aggregates, ct);
    }

    /// <summary>Reads break columns, __count, then a0..aN.</summary>
    private async Task<List<BreakTotal>> ReadBreakTotals(
        Query query,
        IReadOnlyList<ColumnModel> breaks,
        IReadOnlyList<ValidAggregate> aggregates,
        CancellationToken ct)
    {
        await using var command = Build(query);
        await using var reader = await command.ExecuteReaderAsync(ct);

        return await MaterializeBreakTotals(reader, breaks, aggregates, ct);
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
    {
        var command = CommandBuilder.Build(connection, compiler.Compile(query), contextParams, definition, logger);
        command.Transaction = transaction;
        return command;
    }

    private DbCommand BuildOracleBatch(IReadOnlyList<Query> queries)
    {
        var command = CommandBuilder.BuildOracleCursorBatch(
            connection,
            queries.Select(compiler.Compile).ToList(),
            contextParams,
            definition,
            logger);
        command.Transaction = transaction;
        return command;
    }

    private bool UseOracleCursorBatch
        => definition.GetEffectiveDialect() == ReportDialect.Oracle
            && definition.Consistency == ReportConsistency.Snapshot;

    private static async Task<long> MaterializeCount(DbDataReader reader, CancellationToken ct)
    {
        if (!await reader.ReadAsync(ct))
            throw new InvalidOperationException("The report count result set returned no row.");
        return Convert.ToInt64(reader.GetValue(0));
    }

    private static async Task<QueryRows> MaterializeRows(
        DbDataReader reader,
        int? maxRows,
        CancellationToken ct)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = ValueAt(reader, i);
            rows.Add(row);
        }
        return ApplyLimit(rows, maxRows);
    }

    private static async Task<QueryRows> MaterializeRows(
        DbDataReader reader,
        IReadOnlyList<string> publicNames,
        int? maxRows,
        CancellationToken ct)
    {
        if (reader.FieldCount != publicNames.Count)
            throw new InvalidOperationException(
                $"The composed table returned {reader.FieldCount} columns for a {publicNames.Count}-column projection.");
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>(publicNames.Count, StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < publicNames.Count; index++)
                row[publicNames[index]] = ValueAt(reader, index);
            rows.Add(row);
        }
        return ApplyLimit(rows, maxRows);
    }

    private static async Task<Dictionary<string, IReadOnlyDictionary<string, object?>>> MaterializeAggregates(
        DbDataReader reader,
        IReadOnlyList<ValidAggregate> aggregates,
        CancellationToken ct)
    {
        var values = new object?[aggregates.Count];
        if (await reader.ReadAsync(ct))
        {
            for (var i = 0; i < values.Length; i++)
                values[i] = ValueAt(reader, i);
        }
        return NestAggregates(aggregates, i => values[i]);
    }

    private static async Task<List<BreakTotal>> MaterializeBreakTotals(
        DbDataReader reader,
        IReadOnlyList<ColumnModel> breaks,
        IReadOnlyList<ValidAggregate> aggregates,
        CancellationToken ct)
    {
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

    private static async Task RequireNextResult(DbDataReader reader, CancellationToken ct)
    {
        if (!await reader.NextResultAsync(ct))
            throw new InvalidOperationException("The Oracle report batch returned fewer result sets than requested.");
    }

    private static async Task RequireEnd(DbDataReader reader, CancellationToken ct)
    {
        if (await reader.NextResultAsync(ct))
            throw new InvalidOperationException("The Oracle report batch returned an unexpected result set.");
    }

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

internal sealed record TableQueryRows(
    long TotalRows,
    Dictionary<string, IReadOnlyDictionary<string, object?>> Aggregates,
    List<BreakTotal> BreakTotals,
    List<IReadOnlyDictionary<string, object?>> Rows);

internal sealed record PivotGroup(
    object?[] RowKey,
    object?[] ColumnKey,
    long Count,
    object?[] Values);
