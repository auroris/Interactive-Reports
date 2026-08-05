using System.Data.Common;
using System.Diagnostics;
using InteractiveReport.Core.Composition;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Schema;
using InteractiveReport.Core.Validation;

namespace InteractiveReport.Core.Execution;

/// <summary>
/// Orchestrates one report request: discover schema (cached) → validate state → compose →
/// compile → execute count + page → shape the result. Count and page derive from the same
/// filtered core, and both run inside a single connection per request (sequential —
/// SQLite-friendly; per-dialect parallelism is a later optimization).
/// </summary>
public sealed class ReportExecutor
{
    private readonly IReportConnectionFactory _connections;
    private readonly SchemaCache _schemaCache;

    public ReportExecutor(IReportConnectionFactory connections, SchemaCache schemaCache)
    {
        _connections = connections;
        _schemaCache = schemaCache;
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
        var composed = QueryComposer.Compose(def, validated);
        var compiler = DialectSupport.GetCompiler(def.Dialect);

        await using var conn = _connections.CreateConnection(def.Connection);
        await conn.OpenAsync(ct);

        long totalRows;
        await using (var countCmd = CommandBuilder.Build(conn, compiler.Compile(composed.Count), contextParams, def))
        {
            var scalar = await countCmd.ExecuteScalarAsync(ct);
            totalRows = Convert.ToInt64(scalar);
        }

        var aggregates = composed.Aggregates is null
            ? new Dictionary<string, IReadOnlyDictionary<string, object?>>()
            : await ReadAggregates(conn, compiler, composed.Aggregates, validated, contextParams, def, ct);

        var breakTotals = composed.BreakTotals is null
            ? (IReadOnlyList<BreakTotal>)[]
            : await ReadBreakTotals(conn, compiler, composed.BreakTotals, validated, contextParams, def, ct);

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        await using (var pageCmd = CommandBuilder.Build(conn, compiler.Compile(composed.Page), contextParams, def))
        await using (var reader = await pageCmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < reader.FieldCount; i++)
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                rows.Add(row);
            }
        }

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

    /// <summary>Single-row aggregate query: aliases a0..aN in validated-aggregate order.</summary>
    private static async Task<Dictionary<string, IReadOnlyDictionary<string, object?>>> ReadAggregates(
        DbConnection conn,
        SqlKata.Compilers.Compiler compiler,
        SqlKata.Query query,
        ValidatedState validated,
        IReadOnlyDictionary<string, object?> contextParams,
        ReportDefinition def,
        CancellationToken ct)
    {
        await using var cmd = CommandBuilder.Build(conn, compiler.Compile(query), contextParams, def);
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
    private static async Task<List<BreakTotal>> ReadBreakTotals(
        DbConnection conn,
        SqlKata.Compilers.Compiler compiler,
        SqlKata.Query query,
        ValidatedState validated,
        IReadOnlyDictionary<string, object?> contextParams,
        ReportDefinition def,
        CancellationToken ct)
    {
        await using var cmd = CommandBuilder.Build(conn, compiler.Compile(query), contextParams, def);
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
