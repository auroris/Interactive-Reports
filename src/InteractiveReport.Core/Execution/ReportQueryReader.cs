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
/// <param name="connection">The open connection used for every command.</param>
/// <param name="compiler">The dialect-specific SQLKata compiler.</param>
/// <param name="contextParams">Server-resolved base-query parameters.</param>
/// <param name="definition">Supplies dialect, timeout, and consistency settings.</param>
/// <param name="logger">Receives SQL diagnostics; <see langword="null"/> disables logging.</param>
/// <param name="transaction">The optional read-scope transaction assigned to every command.</param>
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
    /// aggregates, optional break subtotals, and rows. Oracle snapshot mode sends one anonymous PL/SQL block
    /// and receives ordered REF CURSORs; every other mode uses the same logical contract over ordinary
    /// commands.
    /// </summary>
    /// <param name="bundle">The compiled main, count, footer, and break queries plus public layout contracts.</param>
    /// <param name="ct">Cancels command execution and result reading.</param>
    /// <returns>Materialized total count, aggregate maps, break totals, and page rows.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="bundle"/> is <see langword="null"/>.</exception>
    /// <remarks>Executes one command per logical result set, except Oracle snapshot mode, which uses one multi-cursor batch.</remarks>
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

    /// <summary>
    /// Executes the terminal query bundle and materializes its related result sets.
    /// </summary>
    /// <param name="queries">The logical main, count, and optional aggregate/break queries.</param>
    /// <param name="breaks">Break-key columns in result-set ordinal order.</param>
    /// <param name="aggregates">Aggregate descriptors in alias ordinal order.</param>
    /// <param name="pagePublicNames">Optional public names used to replace physical reader column names by ordinal.</param>
    /// <param name="ct">Cancels command execution and result reading.</param>
    /// <returns>Materialized result sets combined into one table result.</returns>
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

    /// <summary>
    /// Executes the optional count query and returns the total row count.
    /// </summary>
    /// <param name="query">The count query expected to return one scalar.</param>
    /// <param name="ct">Cancels command execution.</param>
    /// <returns>The scalar converted to a 64-bit row count.</returns>
    /// <remarks>Creates, executes, and disposes one command.</remarks>
    private async Task<long> ReadCount(Query query, CancellationToken ct)
    {
        await using var command = Build(query);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
    }

    /// <summary>
    /// Executes the main query and materializes protocol rows.
    /// </summary>
    /// <param name="query">The SQLKata row query to compile and execute.</param>
    /// <param name="maxRows">Optional public row cap; the query must fetch at most one sentinel row beyond it.</param>
    /// <param name="ct">Cancels command execution and result reading.</param>
    /// <returns>Rows keyed by provider column names plus a truncation flag.</returns>
    /// <remarks>Creates, executes, and disposes one command and reader.</remarks>
    public async Task<QueryRows> ReadRows(Query query, int? maxRows, CancellationToken ct)
    {
        await using var command = Build(query);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await MaterializeRows(reader, maxRows, ct);
    }

    /// <summary>
    /// Reads a server-authored physical projection into public protocol names by ordinal. This is
    /// required for Pivot cells: their public, data-derived names are dictionary keys only and are never
    /// emitted as SQL identifiers.
    /// </summary>
    /// <param name="query">The SQLKata row query to compile and execute.</param>
    /// <param name="publicNames">The public output names already allocated in the result contract.</param>
    /// <param name="maxRows">Optional public row cap; the query must fetch at most one sentinel row beyond it.</param>
    /// <param name="ct">Cancels command execution and result reading.</param>
    /// <returns>Rows keyed by <paramref name="publicNames"/> plus a truncation flag.</returns>
    /// <remarks>Creates, executes, and disposes one command and reader.</remarks>
    /// <exception cref="InvalidOperationException">Thrown when reader field count differs from the public contract.</exception>
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

    /// <summary>
    /// Reads a single-row aggregate query whose aliases are <c>a0</c> through <c>aN</c>.
    /// </summary>
    /// <param name="query">The aggregate query to compile and execute.</param>
    /// <param name="aggregates">Descriptors matching aggregate result ordinals.</param>
    /// <param name="ct">Cancels command execution and result reading.</param>
    /// <returns>Values nested by public column name and aggregate function name.</returns>
    private async Task<Dictionary<string, IReadOnlyDictionary<string, object?>>> ReadAggregates(
        Query query,
        IReadOnlyList<ValidAggregate> aggregates,
        CancellationToken ct)
    {
        await using var command = Build(query);
        await using var reader = await command.ExecuteReaderAsync(ct);

        return await MaterializeAggregates(reader, aggregates, ct);
    }

    /// <summary>
    /// Reads break columns, <c>__count</c>, then <c>a0</c> through <c>aN</c>.
    /// </summary>
    /// <param name="query">The break-total query to compile and execute.</param>
    /// <param name="breaks">The ordered break columns whose values define a group boundary.</param>
    /// <param name="aggregates">Descriptors matching aggregate result ordinals after the count.</param>
    /// <param name="ct">Cancels command execution and result reading.</param>
    /// <returns>One subtotal record per break-key tuple in query order.</returns>
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

    /// <summary>
    /// Executes pivot-key discovery and returns distinct typed keys.
    /// </summary>
    /// <param name="query">The grouped discovery query to compile and execute.</param>
    /// <param name="rowDimensionCount">Leading row-key column count.</param>
    /// <param name="columnDimensionCount">Following dynamic column-key count.</param>
    /// <param name="valueCount">Metric value count following the implicit row count.</param>
    /// <param name="ct">Cancels command execution and result reading.</param>
    /// <returns>Provider values split into row keys, dynamic column keys, counts, and metric arrays.</returns>
    /// <remarks>Creates, executes, and disposes one command and reader.</remarks>
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

    /// <summary>
    /// Compiles a SQLKata query and creates a command bound to the reader's connection and transaction.
    /// </summary>
    /// <param name="query">The SQLKata query to compile and bind.</param>
    /// <returns>An unexecuted command assigned to the configured transaction; the caller must dispose it.</returns>
    private DbCommand Build(Query query)
    {
        var command = CommandBuilder.Build(connection, compiler.Compile(query), contextParams, definition, logger);
        command.Transaction = transaction;
        return command;
    }

    /// <summary>
    /// Builds an Oracle batch command that returns multiple ref-cursor result sets.
    /// </summary>
    /// <param name="queries">Ordered logical result-set queries.</param>
    /// <returns>An unexecuted Oracle batch assigned to the configured transaction; the caller must dispose it.</returns>
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

    /// <summary>Gets whether snapshot consistency on Oracle requires one REF CURSOR batch.</summary>
    private bool UseOracleCursorBatch
        => definition.GetEffectiveDialect() == ReportDialect.Oracle
            && definition.Consistency == ReportConsistency.Snapshot;

    /// <summary>
    /// Reads the first value from a count result set.
    /// </summary>
    /// <param name="reader">The reader positioned before the count result.</param>
    /// <param name="ct">Cancels asynchronous reading.</param>
    /// <returns>The first column converted to a 64-bit count.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the result set has no row.</exception>
    private static async Task<long> MaterializeCount(DbDataReader reader, CancellationToken ct)
    {
        if (!await reader.ReadAsync(ct))
            throw new InvalidOperationException("The report count result set returned no row.");
        return Convert.ToInt64(reader.GetValue(0));
    }

    /// <summary>
    /// Materializes every current result-set row using provider column names.
    /// </summary>
    /// <param name="reader">The reader positioned before the first data row in the current result set.</param>
    /// <param name="maxRows">Optional public cap used to strip one sentinel row.</param>
    /// <param name="ct">Cancels asynchronous reading.</param>
    /// <returns>Materialized rows and whether a sentinel proved truncation.</returns>
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

    /// <summary>
    /// Materializes every current result-set row using public names by ordinal.
    /// </summary>
    /// <param name="reader">The reader positioned before the first data row in the current result set.</param>
    /// <param name="publicNames">The public output names already allocated in the result contract.</param>
    /// <param name="maxRows">Optional public cap used to strip one sentinel row.</param>
    /// <param name="ct">Cancels asynchronous reading.</param>
    /// <returns>Materialized public rows and whether a sentinel proved truncation.</returns>
    /// <exception cref="InvalidOperationException">Thrown when reader field count differs from <paramref name="publicNames"/>.</exception>
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

    /// <summary>
    /// Materializes one aggregate row and nests its ordinal values by column and function.
    /// </summary>
    /// <param name="reader">The reader positioned before the optional aggregate row.</param>
    /// <param name="aggregates">Descriptors matching result ordinals.</param>
    /// <param name="ct">Cancels asynchronous reading.</param>
    /// <returns>Values nested by public column and aggregate name; an empty result row yields null values.</returns>
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

    /// <summary>
    /// Materializes all break subtotal rows from the current result set.
    /// </summary>
    /// <param name="reader">The reader positioned before the first break-total row.</param>
    /// <param name="breaks">The ordered break columns whose values define a group boundary.</param>
    /// <param name="aggregates">Descriptors matching result ordinals after break keys and count.</param>
    /// <param name="ct">Cancels asynchronous reading.</param>
    /// <returns>Break keys, row counts, and nested aggregates in reader order.</returns>
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

    /// <summary>
    /// Advances an Oracle batch reader and requires another logical result set.
    /// </summary>
    /// <param name="reader">The Oracle batch reader positioned at the current logical result set.</param>
    /// <param name="ct">Cancels the asynchronous result-set advance.</param>
    /// <returns>A task that completes after the reader advances.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the batch returned too few result sets.</exception>
    private static async Task RequireNextResult(DbDataReader reader, CancellationToken ct)
    {
        if (!await reader.NextResultAsync(ct))
            throw new InvalidOperationException("The Oracle report batch returned fewer result sets than requested.");
    }

    /// <summary>
    /// Requires that an Oracle batch reader has no unrequested result set remaining.
    /// </summary>
    /// <param name="reader">The Oracle batch reader positioned at the final requested result set.</param>
    /// <param name="ct">Cancels the asynchronous probe for an extra result set.</param>
    /// <returns>A task that completes after probing the reader.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the batch returned an extra result set.</exception>
    private static async Task RequireEnd(DbDataReader reader, CancellationToken ct)
    {
        if (await reader.NextResultAsync(ct))
            throw new InvalidOperationException("The Oracle report batch returned an unexpected result set.");
    }

    /// <summary>
    /// Copies one contiguous range of provider values from the current row.
    /// </summary>
    /// <param name="reader">The reader positioned on the row containing the requested key range.</param>
    /// <param name="offset">The first zero-based reader ordinal.</param>
    /// <param name="count">The number of consecutive values to copy.</param>
    /// <returns>Provider values in ordinal order, with database nulls converted to <see langword="null"/>.</returns>
    private static object?[] ReadKey(DbDataReader reader, int offset, int count)
    {
        var values = new object?[count];
        for (var i = 0; i < count; i++)
            values[i] = ValueAt(reader, offset + i);
        return values;
    }

    /// <summary>
    /// Reads one provider value by ordinal and converts database null to <see langword="null"/>.
    /// </summary>
    /// <param name="reader">The reader positioned on the row containing the requested field.</param>
    /// <param name="ordinal">The zero-based reader field ordinal.</param>
    /// <returns>The provider value, or <see langword="null"/> for database null.</returns>
    private static object? ValueAt(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal);

    /// <summary>
    /// Applies a provider-neutral row limit to a query.
    /// </summary>
    /// <param name="rows">Rows already materialized, including at most one sentinel beyond the public cap.</param>
    /// <param name="maxRows">The optional public row cap.</param>
    /// <returns>The same list, with its final sentinel removed when truncated, plus the truncation flag.</returns>
    private static QueryRows ApplyLimit(
        List<IReadOnlyDictionary<string, object?>> rows,
        int? maxRows)
    {
        var truncated = maxRows.HasValue && rows.Count > maxRows.Value;
        if (truncated) rows.RemoveAt(rows.Count - 1);
        return new QueryRows(rows, truncated);
    }

    /// <summary>
    /// Nests flat aggregate ordinal values by public column and function name.
    /// </summary>
    /// <param name="aggregates">Descriptors in the same order as the flat result values.</param>
    /// <param name="valueAt">Returns the materialized value at an aggregate ordinal.</param>
    /// <returns>A case-insensitive outer map keyed by column, with inner keys from the public aggregate-name contract.</returns>
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

/// <summary>Contains materialized protocol rows and whether a sentinel row proved truncation.</summary>
internal sealed record QueryRows(
    List<IReadOnlyDictionary<string, object?>> Rows,
    bool Truncated);

/// <summary>Contains every logical result set needed to render a terminal table response.</summary>
internal sealed record TableQueryRows(
    long TotalRows,
    Dictionary<string, IReadOnlyDictionary<string, object?>> Aggregates,
    List<BreakTotal> BreakTotals,
    List<IReadOnlyDictionary<string, object?>> Rows);

/// <summary>Contains one grouped pivot-discovery row split into row key, column key, count, and metric values.</summary>
internal sealed record PivotGroup(
    object?[] RowKey,
    object?[] ColumnKey,
    long Count,
    object?[] Values);
