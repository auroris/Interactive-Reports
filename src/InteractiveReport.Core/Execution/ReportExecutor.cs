using System.Data.Common;
using System.Diagnostics;
using InteractiveReport.Core.Composition;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Schema;
using InteractiveReport.Core.Validation;
using Microsoft.Extensions.Logging;
using SqlKata.Compilers;

namespace InteractiveReport.Core.Execution;

/// <summary>
/// Orchestrates one report request: discover schema (cached), validate state, compose,
/// execute, and shape the result. Each request runs sequentially on one prepared
/// connection. Provider access and view transformations live in focused collaborators.
/// </summary>
public sealed class ReportExecutor
{
    /// <summary>Hard ceiling on pivot source groups regardless of definition settings.</summary>
    public const int MaxPivotGroups = 10_000;

    private readonly ReportConnectionManager _connections;
    private readonly SchemaCache _schemaCache;
    private readonly ILogger? _logger;

    public ReportExecutor(
        IReportConnectionFactory connections,
        SchemaCache schemaCache,
        ILogger<ReportExecutor>? logger = null)
    {
        _connections = new ReportConnectionManager(connections);
        _schemaCache = schemaCache;
        _logger = logger;
    }

    public async Task<ReportSchema> GetSchema(
        ReportDefinition definition,
        IReadOnlyDictionary<string, object?> contextParams,
        CancellationToken ct = default)
    {
        return await _schemaCache.GetOrDiscover(definition, async () =>
        {
            await using var connection = await _connections.Open(definition, ct);
            return await SchemaDiscovery.Discover(connection, definition, contextParams, ct);
        });
    }

    public async Task<ReportResult> Query(
        ReportDefinition definition,
        ReportState state,
        IReadOnlyDictionary<string, object?> contextParams,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var schema = await GetSchema(definition, contextParams, ct);
        var validated = StateValidator.Validate(definition, state, schema);

        return validated.View.Mode switch
        {
            ViewMode.GroupBy => await QueryGroupBy(definition, validated, contextParams, stopwatch, ct),
            ViewMode.Pivot => await QueryPivot(definition, validated, contextParams, stopwatch, ct),
            _ => await QueryGrid(definition, validated, contextParams, stopwatch, ct),
        };
    }

    /// <summary>Uses the same validated state without paging, capped at MaxRows.</summary>
    public async Task<ExportResult> Export(
        ReportDefinition definition,
        ReportState state,
        IReadOnlyDictionary<string, object?> contextParams,
        CancellationToken ct = default)
    {
        var schema = await GetSchema(definition, contextParams, ct);
        var validated = StateValidator.Validate(definition, state, schema);

        if (validated.View.Mode == ViewMode.Pivot)
        {
            var pivot = await QueryPivot(
                definition,
                validated,
                contextParams,
                Stopwatch.StartNew(),
                ct);
            return new ExportResult(pivot.Columns, pivot.Rows, Truncated: false);
        }

        var compiler = DialectSupport.GetCompiler(definition.Dialect);
        await using var connection = await _connections.Open(definition, ct);
        var reader = CreateReader(connection, compiler, definition, contextParams);

        if (validated.View.Mode == ViewMode.GroupBy)
        {
            var query = QueryComposer.ComposeGroupByExport(definition, validated, definition.MaxRows);
            var result = await reader.ReadGroupedRows(
                query,
                validated.View.GroupBy,
                validated.View.Values.Count,
                definition.MaxRows,
                ct);
            return new ExportResult(ReportResultColumns.ForGroupBy(validated), result.Rows, result.Truncated);
        }

        var grid = QueryComposer.ComposeGridExport(definition, validated, definition.MaxRows);
        var gridResult = await reader.ReadRows(grid, definition.MaxRows, ct);
        return new ExportResult(
            ReportResultColumns.From(validated.SelectColumns),
            gridResult.Rows,
            gridResult.Truncated);
    }

    private async Task<ReportResult> QueryGrid(
        ReportDefinition definition,
        ValidatedState state,
        IReadOnlyDictionary<string, object?> contextParams,
        Stopwatch stopwatch,
        CancellationToken ct)
    {
        var composed = QueryComposer.Compose(definition, state);
        var compiler = DialectSupport.GetCompiler(definition.Dialect);

        await using var connection = await _connections.Open(definition, ct);
        var reader = CreateReader(connection, compiler, definition, contextParams);

        var totalRows = await reader.ReadCount(composed.Count, ct);
        var aggregates = composed.Aggregates is null
            ? new Dictionary<string, IReadOnlyDictionary<string, object?>>()
            : await reader.ReadAggregates(composed.Aggregates, state.Aggregates, ct);
        var breakTotals = composed.BreakTotals is null
            ? []
            : await reader.ReadBreakTotals(composed.BreakTotals, state.Breaks, state.Aggregates, ct);
        var executionRows = (await reader.ReadRows(composed.Page, maxRows: null, ct)).Rows;
        var highlights = state.Rules.Decorations.Count > 0
            ? HighlightEvaluator.Evaluate(state.Rules.Decorations, executionRows)
            : [];
        var rows = ReportRowProjector.VisibleColumns(executionRows, state.SelectColumns);

        stopwatch.Stop();
        return new ReportResult
        {
            AvailableColumns = ReportResultColumns.From(state.Schema),
            Columns = ReportResultColumns.From(state.SelectColumns),
            Rows = rows,
            Page = Page(state),
            TotalRows = totalRows,
            Aggregates = aggregates,
            BreakTotals = breakTotals,
            Highlights = highlights,
            Ignored = state.Ignored,
            ElapsedMs = stopwatch.ElapsedMilliseconds,
        };
    }

    private async Task<ReportResult> QueryGroupBy(
        ReportDefinition definition,
        ValidatedState state,
        IReadOnlyDictionary<string, object?> contextParams,
        Stopwatch stopwatch,
        CancellationToken ct)
    {
        var (page, count) = QueryComposer.ComposeGroupByView(definition, state);
        var compiler = DialectSupport.GetCompiler(definition.Dialect);

        await using var connection = await _connections.Open(definition, ct);
        var reader = CreateReader(connection, compiler, definition, contextParams);
        var totalGroups = await reader.ReadCount(count, ct);
        var rows = (await reader.ReadGroupedRows(
            page,
            state.View.GroupBy,
            state.View.Values.Count,
            maxRows: null,
            ct)).Rows;

        stopwatch.Stop();
        return new ReportResult
        {
            AvailableColumns = ReportResultColumns.From(state.Schema),
            Columns = ReportResultColumns.ForGroupBy(state),
            Rows = rows,
            Page = Page(state),
            TotalRows = totalGroups,
            Ignored = state.Ignored,
            ElapsedMs = stopwatch.ElapsedMilliseconds,
        };
    }

    private async Task<ReportResult> QueryPivot(
        ReportDefinition definition,
        ValidatedState state,
        IReadOnlyDictionary<string, object?> contextParams,
        Stopwatch stopwatch,
        CancellationToken ct)
    {
        var source = QueryComposer.ComposePivotSource(definition, state, MaxPivotGroups);
        var compiler = DialectSupport.GetCompiler(definition.Dialect);

        List<PivotGroup> groups;
        await using (var connection = await _connections.Open(definition, ct))
        {
            var reader = CreateReader(connection, compiler, definition, contextParams);
            groups = await reader.ReadPivotGroups(
                source,
                state.View.PivotRows.Count,
                state.View.PivotCols.Count,
                state.View.Values.Count,
                ct);
        }

        if (groups.Count > MaxPivotGroups)
        {
            throw new ReportValidationException(
                [new ValidationError(
                    "view",
                    $"pivot source exceeds {MaxPivotGroups} groups — filter further or choose lower-cardinality dimensions")]);
        }

        var pivot = PivotTableBuilder.Build(groups, state, definition.MaxPivotColumns);
        stopwatch.Stop();
        return new ReportResult
        {
            AvailableColumns = ReportResultColumns.From(state.Schema),
            Columns = pivot.Columns,
            Rows = pivot.Rows,
            Page = new PageRequest { Index = 1, Size = Math.Max(1, pivot.Rows.Count) },
            TotalRows = pivot.Rows.Count,
            Ignored = state.Ignored,
            ElapsedMs = stopwatch.ElapsedMilliseconds,
        };
    }

    private ReportQueryReader CreateReader(
        DbConnection connection,
        Compiler compiler,
        ReportDefinition definition,
        IReadOnlyDictionary<string, object?> contextParams)
        => new(connection, compiler, contextParams, definition, _logger);

    private static PageRequest Page(ValidatedState state)
        => new() { Index = state.PageIndex, Size = state.PageSize };
}

/// <summary>Unpaged export payload; Truncated means MaxRows was hit and rows were cut there.</summary>
public sealed record ExportResult(
    IReadOnlyList<ColumnInfo> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    bool Truncated);
