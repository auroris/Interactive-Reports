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

        sw.Stop();

        return new ReportResult
        {
            Columns = validated.SelectColumns
                .Select(c => new ColumnInfo(c.Name, c.Label, c.KindName, c.IsComputed))
                .ToList(),
            Rows = rows,
            Page = new PageRequest { Index = validated.PageIndex, Size = validated.PageSize },
            TotalRows = totalRows,
            Ignored = validated.Ignored,
            ElapsedMs = sw.ElapsedMilliseconds,
        };
    }
}
