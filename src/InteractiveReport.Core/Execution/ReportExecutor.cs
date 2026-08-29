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
    /// and validation pipeline used by query and export. Static table schemas require
    /// no row query; Pivot alone performs runtime column discovery. Administrative
    /// imports use this before persisting an uploaded document.
    /// </summary>
    public async Task ValidateDocument(
        ReportDefinition definition,
        ReportState state,
        IReadOnlyDictionary<string, object?> contextParams,
        CancellationToken ct = default)
        => _ = await RefreshSchemaCaches(definition, state, contextParams, ct);

    /// <summary>
    /// Replaces every null per-table schema cache. Grid, Group, and Chart schemas are
    /// derived from their validated plans without executing rows; only Pivot requires
    /// live discovery because its generated columns depend on data values. Non-null
    /// caches are preserved as snapshots and never participate in validation or binding.
    /// </summary>
    public async Task<ReportState> RefreshSchemaCaches(
        ReportDefinition definition,
        ReportState state,
        IReadOnlyDictionary<string, object?> contextParams,
        CancellationToken ct = default)
    {
        var evaluationUtcNow = DateTime.UtcNow;
        return (await RefreshSchemaCachesCore(
            definition,
            state,
            contextParams,
            evaluationUtcNow,
            ct)).Document;
    }

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
        DateTime evaluationUtcNow,
        CancellationToken ct)
    {
        var schema = await GetSchema(definition, contextParams, ct);
        return StateValidator.Validate(definition, state, schema, evaluationUtcNow);
    }

    public async Task<ReportResult> Query(
        ReportDefinition definition,
        ReportState state,
        IReadOnlyDictionary<string, object?> contextParams,
        CancellationToken ct = default)
    {
        var evaluationUtcNow = DateTime.UtcNow;
        var refreshed = await RefreshSchemaCachesCore(
            definition,
            state,
            contextParams,
            evaluationUtcNow,
            ct);
        var active = refreshed.Document.ActiveTable;
        var result = active is not null && refreshed.Results.TryGetValue(active, out var cached)
            ? cached
            : await QueryCore(
                definition,
                refreshed.Document,
                contextParams,
                evaluationUtcNow,
                ct);
        result.Document = refreshed.Document;

        _logger?.LogInformation(
            "Report {Report} query completed in {ElapsedMs} ms with {RowCount} rows ({TotalRows} total)",
            definition.Name,
            result.ElapsedMs,
            result.Rows.Count,
            result.TotalRows);
        return result;
    }

    private async Task<ReportResult> QueryCore(
        ReportDefinition definition,
        ReportState state,
        IReadOnlyDictionary<string, object?> contextParams,
        DateTime evaluationUtcNow,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var validated = await IngestDocument(
            definition,
            state,
            contextParams,
            evaluationUtcNow,
            ct);

        return validated.View.Mode switch
        {
            ViewMode.GroupBy => await QueryGroupStage(definition, validated, contextParams, stopwatch, ct),
            ViewMode.Pivot => (await QueryPivot(
                definition,
                validated,
                contextParams,
                stopwatch,
                ct,
                maxRows: definition.MaxRows)).Result,
            ViewMode.Chart => await QueryChart(definition, validated, contextParams, stopwatch, ct),
            _ => await QueryGrid(definition, validated, contextParams, stopwatch, ct),
        };
    }

    private async Task<SchemaRefresh> RefreshSchemaCachesCore(
        ReportDefinition definition,
        ReportState state,
        IReadOnlyDictionary<string, object?> contextParams,
        DateTime evaluationUtcNow,
        CancellationToken ct)
    {
        var structural = StateStructureValidator.Collect(state);
        if (structural.Count > 0) throw new ReportValidationException(structural);
        if (definition.DefaultState is not null
            && StateStructureValidator.Collect(definition.DefaultState) is { Count: > 0 } defaultErrors)
            throw new InvalidOperationException(
                $"Report '{definition.Name}': the default state document is structurally invalid — "
                + $"{defaultErrors[0].Path}: {defaultErrors[0].Message}.");

        // Return the effective document, not merely the partial request. The client
        // adopts this value, so inherited default tables and their refreshed caches
        // must be present in the response.
        var document = ReportStateResolver.Resolve(definition.DefaultState, state);
        var results = new Dictionary<string, ReportResult>(StringComparer.OrdinalIgnoreCase);
        if (document.Tables is not { Count: > 0 })
            return new SchemaRefresh(document, results);

        foreach (var (tableId, table) in document.Tables)
        {
            if (table.Schema is not null) continue;
            var target = ReportStateResolver.Resolve(null, document);
            target.ActiveTable = tableId;
            var validated = await IngestDocument(
                definition,
                target,
                contextParams,
                evaluationUtcNow,
                ct);
            var staticSchema = StaticTableSchema(validated);
            if (staticSchema is not null)
            {
                table.Schema = staticSchema.Select(column => column with { }).ToList();
                continue;
            }

            // Pivot is the only shape whose output column names are values read from
            // the database. Execute it once and reuse the result if it is active.
            var result = await QueryCore(
                definition,
                target,
                contextParams,
                evaluationUtcNow,
                ct);
            table.Schema = result.AvailableColumns.Select(column => column with { }).ToList();
            results[tableId] = result;
        }
        return new SchemaRefresh(document, results);
    }

    private static List<ColumnInfo>? StaticTableSchema(ValidatedState state)
        => state.View.Mode switch
        {
            ViewMode.Grid => ReportResultColumns.From(state.Schema),
            ViewMode.GroupBy => ReportResultColumns.ForGroupTable(state),
            ViewMode.Chart => ReportResultColumns.ForMaterializedTable(
                state.View.Output!.Schema,
                ReportResultColumns.ForChart(state.View.Chart!)),
            ViewMode.Pivot => null,
            _ => null,
        };

    /// <summary>
    /// Uses the same validated state without paging, capped when MaxRows is positive.
    /// An export is the server rendering what the user sees, so the ingested document's display
    /// labels, output-label overrides, and cell renderers apply here (headers,
    /// sum(…) labels, Pivot cells, link/image HTML) — the posted document is the
    /// source of truth, since the client's state may never have been saved.
    /// </summary>
    public async Task<ExportResult> Export(
        ReportDefinition definition,
        ReportState state,
        IReadOnlyDictionary<string, object?> contextParams,
        CancellationToken ct = default)
    {
        var result = await ExportCore(
            definition,
            state,
            contextParams,
            DateTime.UtcNow,
            ct);
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
        DateTime evaluationUtcNow,
        CancellationToken ct)
    {
        var validated = (await IngestDocument(
            definition,
            state,
            contextParams,
            evaluationUtcNow,
            ct)).WithDisplayLabels();

        if (validated.View.Mode == ViewMode.Pivot)
        {
            var executed = await QueryPivot(
                definition,
                validated,
                contextParams,
                Stopwatch.StartNew(),
                ct,
                unpaged: true,
                maxRows: definition.MaxRows);
            var pivot = executed.Result;
            return RenderExport(
                validated,
                pivot.AvailableColumns,
                pivot.Columns,
                PivotTableBuilder.RowsForExport(
                    pivot.Columns,
                    pivot.Rows,
                    pivot.Aggregates,
                    validated.View.PivotRows),
                definition.MaxRows > 0 && pivot.TotalRows > pivot.Rows.Count,
                executed.Layer);
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
            return RenderExport(
                validated,
                chart.AvailableColumns,
                chart.Columns,
                chart.Rows,
                truncated: false);
        }

        var compiler = DialectSupport.GetCompiler(definition.GetEffectiveDialect());
        await using var connection = await _connections.Open(definition, ct);
        var reader = CreateReader(connection, compiler, definition, contextParams);

        if (validated.View.Mode == ViewMode.GroupBy)
        {
            var layer = validated.View.Output!;
            var query = QueryComposer.ComposeGroupStageExport(definition, validated, definition.MaxRows);
            var result = await reader.ReadRows(
                query, definition.MaxRows > 0 ? definition.MaxRows : null, ct);
            var available = ReportResultColumns.ForGroupTable(validated);
            return RenderExport(
                validated,
                available,
                ReportResultColumns.Select(available, layer.SelectColumns),
                result.Rows,
                result.Truncated);
        }

        var grid = QueryComposer.ComposeGridExport(definition, validated, definition.MaxRows);
        var gridResult = await reader.ReadRows(
            grid, definition.MaxRows > 0 ? definition.MaxRows : null, ct);
        return RenderExport(
            validated,
            ReportResultColumns.From(validated.Schema),
            ReportResultColumns.From(validated.SelectColumns),
            gridResult.Rows,
            gridResult.Truncated);
    }

    private static ExportResult RenderExport(
        ValidatedState state,
        IReadOnlyList<ColumnInfo> availableColumns,
        IReadOnlyList<ColumnInfo> columns,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        bool truncated,
        ValidTableLayer? runtimeLayer = null)
    {
        var layer = runtimeLayer ?? state.View.Output;
        var rendered = layer is null
            ? TableExportRenderer.Render(
                availableColumns,
                columns,
                rows,
                state.Schema,
                state.Formats,
                state.Labels)
            : TableExportRenderer.Render(
                availableColumns,
                columns,
                rows,
                layer.Schema,
                layer.Formats,
                layer.Labels,
                state.Formats);
        return new ExportResult(rendered.Columns, rendered.Rows, truncated);
    }

    private async Task<ReportResult> QueryGrid(
        ReportDefinition definition,
        ValidatedState state,
        IReadOnlyDictionary<string, object?> contextParams,
        Stopwatch stopwatch,
        CancellationToken ct)
    {
        var terminal = new SqlTerminalPlan(
            state.Breaks,
            state.Aggregates,
            state.Rules.Decorations,
            state.ProjectionColumns,
            ReportResultColumns.From(state.Schema),
            ReportResultColumns.From(state.SelectColumns));
        return await QuerySqlTable(
            definition,
            state,
            QueryComposer.Compose(definition, state),
            terminal,
            contextParams,
            stopwatch,
            ct);
    }

    /// <summary>
    /// A terminal grouped table: paginated groups read by name through the same
    /// highlight-marker and projection path as an unshaped table. The grouped relation's
    /// stable aliases (dims, __count, metric ids, computed ids) are the row keys.
    /// </summary>
    private async Task<ReportResult> QueryGroupStage(
        ReportDefinition definition,
        ValidatedState state,
        IReadOnlyDictionary<string, object?> contextParams,
        Stopwatch stopwatch,
        CancellationToken ct)
    {
        var layer = state.View.Output!;
        var availableColumns = ReportResultColumns.ForGroupTable(state);
        var terminal = new SqlTerminalPlan(
            layer.Breaks,
            layer.Aggregates,
            layer.Decorations,
            layer.ProjectionColumns,
            availableColumns,
            ReportResultColumns.Select(availableColumns, layer.SelectColumns));
        return await QuerySqlTable(
            definition,
            state,
            QueryComposer.ComposeGroupStageQueries(definition, state),
            terminal,
            contextParams,
            stopwatch,
            ct);
    }

    /// <summary>
    /// Executes and assembles any SQL-backed terminal table. The shape, when present,
    /// has already selected its relation by producing <paramref name="composed"/>;
    /// break paging, highlights, projection, selection, and response metadata are one
    /// ordinary-table path from here onward.
    /// </summary>
    private async Task<ReportResult> QuerySqlTable(
        ReportDefinition definition,
        ValidatedState state,
        ComposedQueries composed,
        SqlTerminalPlan terminal,
        IReadOnlyDictionary<string, object?> contextParams,
        Stopwatch stopwatch,
        CancellationToken ct)
    {
        var compiler = DialectSupport.GetCompiler(definition.GetEffectiveDialect());

        // The configured scope is exact: none leaves these statements independent;
        // snapshot makes them one provider-specific versioned view or fails loudly.
        await using var connection = await _connections.Open(definition, ct);
        await using var scope = await _connections.BeginReadScope(connection, definition, ct);
        var reader = CreateReader(connection, compiler, definition, contextParams, scope.Transaction);
        var queryRows = await reader.ReadTableQueries(
            composed,
            terminal.Breaks,
            terminal.Aggregates,
            ct);
        var executionRows = queryRows.Rows;
        var breakContinues = false;
        if (terminal.Breaks.Count > 0
            && !state.PageAll
            && executionRows.Count > state.PageSize)
        {
            var boundary = executionRows[state.PageSize];
            executionRows.RemoveRange(state.PageSize, executionRows.Count - state.PageSize);
            breakContinues = executionRows.Count > 0
                && SameBreakKey(executionRows[^1], boundary, terminal.Breaks);
        }
        await scope.CompleteAsync(ct);

        var highlights = terminal.Decorations.Count > 0
            ? HighlightEvaluator.Evaluate(terminal.Decorations, executionRows)
            : [];
        var rows = ReportRowProjector.Columns(executionRows, terminal.ProjectionColumns);

        stopwatch.Stop();
        return new ReportResult
        {
            AvailableColumns = terminal.AvailableColumns,
            Columns = terminal.Columns,
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
        var layer = state.View.Output!;
        var maxPoints = definition.MaxChartPoints;
        var query = QueryComposer.ComposeChartView(definition, state, maxPoints);
        var compiler = DialectSupport.GetCompiler(definition.GetEffectiveDialect());
        var shapeColumns = ReportResultColumns.ForChart(chart);

        List<IReadOnlyDictionary<string, object?>> rows;
        await using (var connection = await _connections.Open(definition, ct))
        {
            var reader = CreateReader(connection, compiler, definition, contextParams);
            var metricOrdinal = chart.Fn is null || chart.Value is null ? 1 : 2;
            rows = await reader.ReadChartRows(
                query,
                metricOrdinal,
                shapeColumns,
                layer,
                state.EvaluationUtcNow,
                maxPoints,
                ct);
        }

        if (rows.Count > maxPoints)
        {
            throw new ReportValidationException(
                [new ValidationError(
                    state.View.ShapePath ?? "tables",
                    $"chart would draw more than {maxPoints} points — filter further or aggregate to fewer categories")]);
        }

        if (chart.Type == ChartType.Pie
            && rows.Any(row => row.TryGetValue(shapeColumns[1].Name, out var value)
                && IsNegative(value)))
        {
            throw new ReportValidationException(
                [new ValidationError(
                    state.View.ShapeProperty("value"),
                    "pie charts require non-negative values")]);
        }

        // Compute/filter already ran while reading so the point cap and pie invariant
        // saw the terminal relational table. Finish sort/aggregate/break/highlight and
        // projection without evaluating those operations a second time.
        var processed = MaterializedTableProcessor.Apply(
            shapeColumns,
            rows,
            layer with { Operations = [] },
            state,
            definition.GetEffectiveDialect(),
            unpaged: true);

        stopwatch.Stop();
        return new ReportResult
        {
            AvailableColumns = processed.AvailableColumns,
            Columns = processed.Columns,
            Rows = processed.Rows,
            Page = new PageRequest { Index = 1, Size = Math.Max(1, processed.Rows.Count) },
            TotalRows = processed.TotalRows,
            Aggregates = processed.Totals,
            BreakTotals = processed.BreakTotals,
            BreakContinues = processed.BreakContinues,
            Highlights = processed.Highlights,
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

    private async Task<(ReportResult Result, ValidTableLayer Layer)> QueryPivot(
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
                EnsurePivotGroupLimit(groups, view.ShapePath);
            }
            else
            {
                await using var scope = await _connections.BeginReadScope(connection, definition, ct);
                var reader = CreateReader(connection, compiler, definition, contextParams, scope.Transaction);
                groups = await ReadPivotSource(reader);
                EnsurePivotGroupLimit(groups, view.ShapePath);
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
        return (
            new ReportResult
            {
                AvailableColumns = processed.AvailableColumns,
                Columns = processed.Columns,
                Rows = processed.Rows,
                Page = unpaged
                    ? new PageRequest { Index = 1, Size = Math.Max(1, processed.Rows.Count) }
                    : Page(state),
                TotalRows = processed.TotalRows,
                Aggregates = processed.Totals,
                BreakTotals = processed.BreakTotals,
                BreakContinues = processed.BreakContinues,
                Highlights = processed.Highlights,
                Ignored = processed.Ignored,
                ElapsedMs = stopwatch.ElapsedMilliseconds,
            },
            processed.Layer);

        Task<List<PivotGroup>> ReadPivotSource(ReportQueryReader reader)
            => reader.ReadPivotGroups(
                source,
                view.PivotRows.Count,
                view.PivotCols.Count,
                valueCount,
                ct);
    }

    private static void EnsurePivotGroupLimit(
        IReadOnlyCollection<PivotGroup> groups,
        string? shapePath)
    {
        if (groups.Count <= MaxPivotGroups) return;
        throw new ReportValidationException(
            [new ValidationError(
                shapePath ?? "tables",
                $"pivot source exceeds {MaxPivotGroups} groups — filter further or choose lower-cardinality dimensions")]);
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

internal sealed record SchemaRefresh(
    ReportState Document,
    IReadOnlyDictionary<string, ReportResult> Results);

internal sealed record SqlTerminalPlan(
    IReadOnlyList<ColumnModel> Breaks,
    IReadOnlyList<ValidAggregate> Aggregates,
    IReadOnlyList<CompiledRule<HighlightEffect>> Decorations,
    IReadOnlyList<ColumnModel> ProjectionColumns,
    IReadOnlyList<ColumnInfo> AvailableColumns,
    IReadOnlyList<ColumnInfo> Columns);
