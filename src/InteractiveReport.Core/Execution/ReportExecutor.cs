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
/// connection. Provider access and stage transformations live in focused collaborators.
/// </summary>
public sealed class ReportExecutor
{
    /// <summary>Hard ceiling on Pivot source groups regardless of definition settings.</summary>
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

        var result = validated.View.Mode switch
        {
            ViewMode.GroupBy => await QueryGroupStage(definition, validated, contextParams, stopwatch, ct),
            ViewMode.Pivot => await QueryPivot(
                definition,
                validated,
                contextParams,
                stopwatch,
                ct,
                maxRows: definition.MaxRows),
            ViewMode.Chart => await QueryChart(definition, validated, contextParams, stopwatch, ct),
            _ => await QueryGrid(definition, validated, contextParams, stopwatch, ct),
        };
        _logger?.LogInformation(
            "Report {Report} query completed in {ElapsedMs} ms with {RowCount} rows ({TotalRows} total)",
            definition.Name,
            result.ElapsedMs,
            result.Rows.Count,
            result.TotalRows);
        return result;
    }

    /// <summary>
    /// Uses the same validated state without paging, capped when MaxRows is positive.
    /// An export is the server rendering what the user sees, so the ingested document's display
    /// labels, stage-layer label overrides, and grid renderers apply here (headers,
    /// sum(…) labels, Pivot cells, link/image HTML) — the posted document is the
    /// source of truth, since the client's state may never have been saved.
    /// </summary>
    public async Task<ExportResult> Export(
        ReportDefinition definition,
        ReportState state,
        IReadOnlyDictionary<string, object?> contextParams,
        CancellationToken ct = default)
    {
        var result = await ExportCore(definition, state, contextParams, ct);
        _logger?.LogInformation(
            "Report {Report} export completed with {RowCount} rows (truncated: {Truncated})",
            definition.Name,
            result.Rows.Count,
            result.Truncated);
        return result;
    }

    private async Task<ExportResult> ExportCore(
        ReportDefinition definition,
        ReportState state,
        IReadOnlyDictionary<string, object?> contextParams,
        CancellationToken ct)
    {
        var validated = (await IngestDocument(definition, state, contextParams, ct)).WithDisplayLabels();

        if (validated.View.Mode == ViewMode.Pivot)
        {
            var pivot = await QueryPivot(
                definition,
                validated,
                contextParams,
                Stopwatch.StartNew(),
                ct,
                unpaged: true,
                maxRows: definition.MaxRows);
            return new ExportResult(
                pivot.Columns,
                PivotTableBuilder.RowsForExport(
                    pivot.Columns,
                    pivot.Rows,
                    pivot.Aggregates,
                    validated.View.PivotRows),
                definition.MaxRows > 0 && pivot.TotalRows > pivot.Rows.Count);
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

        var compiler = DialectSupport.GetCompiler(definition.GetEffectiveDialect());
        await using var connection = await _connections.Open(definition, ct);
        var reader = CreateReader(connection, compiler, definition, contextParams);

        if (validated.View.Mode == ViewMode.GroupBy)
        {
            var layer = validated.View.GroupLayer!;
            var query = QueryComposer.ComposeGroupStageExport(definition, validated, definition.MaxRows);
            var result = await reader.ReadRows(
                query, definition.MaxRows > 0 ? definition.MaxRows : null, ct);
            return new ExportResult(
                ApplyStageLabels(ReportResultColumns.ForGroupStage(validated), layer.Labels),
                ReportRowProjector.Columns(result.Rows, layer.SelectColumns),
                result.Truncated);
        }

        var grid = QueryComposer.ComposeGridExport(definition, validated, definition.MaxRows);
        var gridResult = await reader.ReadRows(
            grid, definition.MaxRows > 0 ? definition.MaxRows : null, ct);
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
        var compiler = DialectSupport.GetCompiler(definition.GetEffectiveDialect());

        // The configured scope is exact: none leaves these statements independent;
        // snapshot makes them one provider-specific versioned view or fails loudly.
        await using var connection = await _connections.Open(definition, ct);
        await using var scope = await _connections.BeginReadScope(connection, definition, ct);
        var reader = CreateReader(connection, compiler, definition, contextParams, scope.Transaction);

        var queryRows = await reader.ReadGridQueries(composed, state, ct);
        var executionRows = queryRows.Rows;
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
        await scope.CompleteAsync(ct);
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
            TotalRows = queryRows.TotalRows,
            Aggregates = queryRows.Aggregates,
            BreakTotals = queryRows.BreakTotals,
            BreakContinues = breakContinues,
            Highlights = highlights,
            Ignored = state.Ignored,
            ElapsedMs = stopwatch.ElapsedMilliseconds,
        };
    }

    /// <summary>
    /// The terminal group stage: paginated groups read by name through the same
    /// highlight-marker and projection pipeline the grid uses — the stage table's
    /// stable aliases (dims, __count, metric ids, computed ids) are the row keys.
    /// </summary>
    private async Task<ReportResult> QueryGroupStage(
        ReportDefinition definition,
        ValidatedState state,
        IReadOnlyDictionary<string, object?> contextParams,
        Stopwatch stopwatch,
        CancellationToken ct)
    {
        var composed = QueryComposer.ComposeGroupStageQueries(definition, state);
        var compiler = DialectSupport.GetCompiler(definition.GetEffectiveDialect());
        var layer = state.View.GroupLayer!;

        await using var connection = await _connections.Open(definition, ct);
        await using var scope = await _connections.BeginReadScope(connection, definition, ct);
        var reader = CreateReader(connection, compiler, definition, contextParams, scope.Transaction);
        var queryRows = await reader.ReadTableQueries(
            composed,
            layer.Breaks,
            layer.Aggregates,
            ct);
        var executionRows = queryRows.Rows;
        var breakContinues = false;
        if (layer.Breaks.Count > 0
            && !state.PageAll
            && executionRows.Count > state.PageSize)
        {
            var boundary = executionRows[state.PageSize];
            executionRows.RemoveRange(state.PageSize, executionRows.Count - state.PageSize);
            breakContinues = executionRows.Count > 0
                && SameBreakKey(executionRows[^1], boundary, layer.Breaks);
        }
        await scope.CompleteAsync(ct);
        var highlights = layer.Decorations.Count > 0
            ? HighlightEvaluator.Evaluate(layer.Decorations, executionRows)
            : [];
        var rows = ReportRowProjector.Columns(executionRows, layer.SelectColumns);

        stopwatch.Stop();
        return new ReportResult
        {
            AvailableColumns = ReportResultColumns.From(state.Schema),
            Columns = ReportResultColumns.ForGroupStage(state),
            Rows = rows,
            Page = Page(state),
            TotalRows = queryRows.TotalRows,
            Aggregates = queryRows.Aggregates,
            BreakTotals = queryRows.BreakTotals,
            BreakContinues = breakContinues,
            Highlights = highlights,
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
        var compiler = DialectSupport.GetCompiler(definition.GetEffectiveDialect());

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
                    "pipeline[1].shape",
                    $"chart would draw more than {maxPoints} points — filter further or aggregate to fewer categories")]);
        }

        if (chart.Type == ChartType.Pie && rows.Any(point => IsNegative(point.Value)))
        {
            throw new ReportValidationException(
                [new ValidationError(
                    "pipeline[1].shape.value",
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
        CancellationToken ct,
        bool unpaged = false,
        int maxRows = 0)
    {
        var view = state.View;
        var source = QueryComposer.ComposePivotSource(definition, state, MaxPivotGroups);
        var compiler = DialectSupport.GetCompiler(definition.GetEffectiveDialect());
        var valueCount = view.Values.Count;

        List<PivotGroup> groups;
        List<PivotGroup> totalGroups = [];
        await using (var connection = await _connections.Open(definition, ct))
        {
            if (!view.Totals)
            {
                var reader = CreateReader(connection, compiler, definition, contextParams);
                groups = await ReadPivotSource(reader);
                EnsurePivotGroupLimit(groups);
            }
            else
            {
                await using var scope = await _connections.BeginReadScope(connection, definition, ct);
                var reader = CreateReader(connection, compiler, definition, contextParams, scope.Transaction);
                groups = await ReadPivotSource(reader);
                EnsurePivotGroupLimit(groups);
                totalGroups = await reader.ReadPivotGroups(
                    QueryComposer.ComposePivotTotals(definition, state),
                    0,
                    view.PivotCols.Count,
                    view.Values.Count,
                    ct);
                await scope.CompleteAsync(ct);
            }
        }

        var pivot = PivotTableBuilder.Build(
            groups,
            state,
            definition.MaxPivotColumns,
            totalGroups);
        var processed = PivotLayerProcessor.Apply(
            pivot,
            state,
            definition.GetEffectiveDialect(),
            unpaged,
            maxRows);
        stopwatch.Stop();
        return new ReportResult
        {
            AvailableColumns = ReportResultColumns.From(state.Schema),
            Columns = processed.Columns,
            Rows = processed.Rows,
            Page = unpaged
                ? new PageRequest { Index = 1, Size = Math.Max(1, processed.Rows.Count) }
                : Page(state),
            TotalRows = processed.TotalRows,
            Aggregates = processed.Totals,
            Highlights = processed.Highlights,
            Ignored = processed.Ignored,
            ElapsedMs = stopwatch.ElapsedMilliseconds,
        };

        Task<List<PivotGroup>> ReadPivotSource(ReportQueryReader reader)
            => reader.ReadPivotGroups(
                source,
                view.PivotRows.Count,
                view.PivotCols.Count,
                valueCount,
                ct);
    }

    private static void EnsurePivotGroupLimit(IReadOnlyCollection<PivotGroup> groups)
    {
        if (groups.Count <= MaxPivotGroups) return;
        throw new ReportValidationException(
            [new ValidationError(
                "pipeline[1].shape",
                $"pivot source exceeds {MaxPivotGroups} groups — filter further or choose lower-cardinality dimensions")]);
    }

    /// <summary>Export-only: stage-layer label overrides win over generated metadata labels.</summary>
    private static IReadOnlyList<ColumnInfo> ApplyStageLabels(
        IReadOnlyList<ColumnInfo> columns,
        IReadOnlyDictionary<string, string>? overrides)
    {
        if (overrides is not { Count: > 0 }) return columns;
        return columns
            .Select(column => overrides.TryGetValue(column.Name, out var label)
                ? column with { Label = label }
                : column)
            .ToList();
    }

    private ReportQueryReader CreateReader(
        DbConnection connection,
        Compiler compiler,
        ReportDefinition definition,
        IReadOnlyDictionary<string, object?> contextParams,
        DbTransaction? transaction = null)
        => new(connection, compiler, contextParams, definition, _logger, transaction);

    private static PageRequest Page(ValidatedState state)
        => new() { Index = state.PageIndex, Size = state.PageAll ? 0 : state.PageSize };
}

/// <summary>Unpaged export payload; Truncated means a positive MaxRows was hit.</summary>
public sealed record ExportResult(
    IReadOnlyList<ColumnInfo> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    bool Truncated);
