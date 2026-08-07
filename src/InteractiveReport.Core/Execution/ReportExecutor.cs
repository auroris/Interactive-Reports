using System.Data.Common;
using System.Diagnostics;
using InteractiveReport.Core.Composition;
using InteractiveReport.Core.Export;
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

    /// <summary>Ceiling a definition's MaxChartPoints may be configured up to.</summary>
    public const int MaxChartPointsCeiling = 10_000;

    private readonly ReportConnectionManager _connections;
    private readonly SchemaCache _schemaCache;
    private readonly ILogger? _logger;

    public ReportExecutor(
        IReportConnectionFactory connections,
        SchemaCache schemaCache,
        ILogger<ReportExecutor>? logger = null)
    {
        _connections = new ReportConnectionManager(connections, logger);
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
            return await SchemaDiscovery.Discover(connection, definition, contextParams, _logger, ct);
        });
    }

    /// <summary>
    /// Runs a report document through the same default resolution, schema discovery,
    /// and validation pipeline used by query and export, without executing its data
    /// query. Administrative imports use this before persisting an uploaded document.
    /// </summary>
    public async Task ValidateDocument(
        ReportDefinition definition,
        ReportState state,
        IReadOnlyDictionary<string, object?> contextParams,
        CancellationToken ct = default)
        => _ = await IngestDocument(definition, state, contextParams, ct);

    /// <summary>
    /// The unified document-ingestion pipeline: every request that carries a report
    /// state document — query or export, saved server-side or never saved at all —
    /// enters here. Discover the (cached) schema, then resolve the document over the
    /// definition's defaults and validate it into the one typed form processors accept.
    /// </summary>
    private async Task<ValidatedState> IngestDocument(
        ReportDefinition definition,
        ReportState state,
        IReadOnlyDictionary<string, object?> contextParams,
        CancellationToken ct)
    {
        var schema = await GetSchema(definition, contextParams, ct);
        return StateValidator.Validate(definition, state, schema);
    }

    public async Task<ReportResult> Query(
        ReportDefinition definition,
        ReportState state,
        IReadOnlyDictionary<string, object?> contextParams,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var validated = await IngestDocument(definition, state, contextParams, ct);

        return validated.View.Mode switch
        {
            ViewMode.GroupBy => await QueryGroupBy(definition, validated, contextParams, stopwatch, ct),
            ViewMode.Pivot => await QueryPivot(definition, validated, contextParams, stopwatch, ct),
            ViewMode.Chart => await QueryChart(definition, validated, contextParams, stopwatch, ct),
            _ => await QueryGrid(definition, validated, contextParams, stopwatch, ct),
        };
    }

    /// <summary>
    /// Uses the same validated state without paging, capped at MaxRows. An export is
    /// the server rendering what the user sees, so the ingested document's display
    /// labels and grid renderers apply here (headers, sum(…) labels, pivot cells,
    /// link/image HTML) — the posted document is the source of truth, since the
    /// client's state may never have been saved.
    /// </summary>
    public async Task<ExportResult> Export(
        ReportDefinition definition,
        ReportState state,
        IReadOnlyDictionary<string, object?> contextParams,
        CancellationToken ct = default)
    {
        var validated = (await IngestDocument(definition, state, contextParams, ct)).WithDisplayLabels();

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

        if (validated.View.Mode == ViewMode.Chart)
        {
            // The charted dataset is the export: same points, same cap, same precise
            // overflow error — a chart export never truncates silently either.
            var chart = await QueryChart(
                definition,
                validated,
                contextParams,
                Stopwatch.StartNew(),
                ct);
            return new ExportResult(chart.Columns, chart.Rows, Truncated: false);
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
            GridExportRenderer.Render(validated, gridResult.Rows),
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
        var breakContinues = false;
        if (state.Breaks.Count > 0
            && !state.PageAll
            && executionRows.Count > state.PageSize)
        {
            var boundary = executionRows[state.PageSize];
            executionRows.RemoveRange(state.PageSize, executionRows.Count - state.PageSize);
            breakContinues = executionRows.Count > 0
                && SameBreakKey(executionRows[^1], boundary, state.Breaks);
        }
        var highlights = state.Rules.Decorations.Count > 0
            ? HighlightEvaluator.Evaluate(state.Rules.Decorations, executionRows)
            : [];
        var rows = ReportRowProjector.Columns(executionRows, state.ProjectionColumns);

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
            BreakContinues = breakContinues,
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

    /// <summary>
    /// Chart data over the complete filtered rowset — computed columns, filters, and
    /// search all apply; the visible grid page never feeds a chart. The response keeps
    /// the generic shape: two columns (label, metric) and a row collection.
    /// </summary>
    private async Task<ReportResult> QueryChart(
        ReportDefinition definition,
        ValidatedState state,
        IReadOnlyDictionary<string, object?> contextParams,
        Stopwatch stopwatch,
        CancellationToken ct)
    {
        var chart = state.View.Chart!;
        var maxPoints = definition.MaxChartPoints;
        var query = QueryComposer.ComposeChartView(definition, state, maxPoints);
        var compiler = DialectSupport.GetCompiler(definition.Dialect);

        List<ChartPoint> rows;
        await using (var connection = await _connections.Open(definition, ct))
        {
            var reader = CreateReader(connection, compiler, definition, contextParams);
            var metricOrdinal = chart.Fn is null || chart.Value is null ? 1 : 2;
            rows = await reader.ReadChartPoints(query, metricOrdinal, ct);
        }

        if (rows.Count > maxPoints)
        {
            throw new ReportValidationException(
                [new ValidationError(
                    "view",
                    $"chart would draw more than {maxPoints} points — filter further or aggregate to fewer categories")]);
        }

        if (chart.Type == ChartType.Pie && rows.Any(point => IsNegative(point.Value)))
        {
            throw new ReportValidationException(
                [new ValidationError(
                    "view.value",
                    "pie charts require non-negative values")]);
        }

        var columns = ReportResultColumns.ForChart(chart);
        var points = new List<IReadOnlyDictionary<string, object?>>(rows.Count);
        foreach (var row in rows)
        {
            var point = new Dictionary<string, object?>(columns.Count, StringComparer.OrdinalIgnoreCase);
            point[columns[0].Name] = row.Label;
            point[columns[1].Name] = row.Value;
            points.Add(point);
        }

        stopwatch.Stop();
        return new ReportResult
        {
            AvailableColumns = ReportResultColumns.From(state.Schema),
            Columns = columns,
            Rows = points,
            Page = new PageRequest { Index = 1, Size = Math.Max(1, points.Count) },
            TotalRows = points.Count,
            Ignored = state.Ignored,
            ElapsedMs = stopwatch.ElapsedMilliseconds,
        };
    }

    private static bool IsNegative(object? value)
        => value is not null && Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture) < 0;

    private static bool SameBreakKey(
        IReadOnlyDictionary<string, object?> left,
        IReadOnlyDictionary<string, object?> right,
        IReadOnlyList<ColumnModel> breaks)
        => breaks.All(column => Equals(left[column.Name], right[column.Name]));

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
        => new() { Index = state.PageIndex, Size = state.PageAll ? 0 : state.PageSize };
}

/// <summary>Unpaged export payload; Truncated means MaxRows was hit and rows were cut there.</summary>
public sealed record ExportResult(
    IReadOnlyList<ColumnInfo> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    bool Truncated);
