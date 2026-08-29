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
    /// Runs the active named table and every null schema-cache target through the same
    /// recursive validation used by query and export. Structural validation covers the
    /// entire document; dormant alternatives with a non-null advisory cache remain
    /// deferred until selected or explicitly invalidated. Pivot targets alone require
    /// runtime column discovery.
    /// </summary>
    public async Task ValidateDocument(
        ReportDefinition definition,
        ReportState state,
        IReadOnlyDictionary<string, object?> contextParams,
        CancellationToken ct = default)
        => _ = await RefreshSchemaCaches(definition, state, contextParams, ct);

    /// <summary>
    /// Replaces every null per-table schema cache and always validates the active table.
    /// Grid, Group, and Chart schemas are derived without executing rows; only Pivot
    /// requires live discovery. Non-null dormant caches are preserved as advisory
    /// snapshots and never participate in expression binding.
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
            ct,
            executeActive: false)).Document;
    }

    /// <summary>
    /// Compatibility path for requests without named tables. Named-table requests use
    /// <see cref="ComposableTableCompiler"/> because their recursive and dynamic schemas
    /// cannot be represented by the legacy synchronous ValidatedState plan.
    /// </summary>
    private async Task<ValidatedState> ValidateLegacyState(
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
            ct,
            executeActive: true);
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
        if (RequiresComposablePipeline(state, state.ActiveTable))
            return await QueryComposableTable(
                definition,
                state,
                contextParams,
                evaluationUtcNow,
                stopwatch,
                ct);

        var validated = await ValidateLegacyState(
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
        CancellationToken ct,
        bool executeActive)
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

        if (string.IsNullOrWhiteSpace(document.ActiveTable))
            throw new ReportValidationException(
                [new ValidationError("activeTable", "activeTable is required when tables are present")]);
        var activeTable = document.Tables.Keys.FirstOrDefault(tableId => string.Equals(
            tableId,
            document.ActiveTable.Trim(),
            StringComparison.OrdinalIgnoreCase));
        if (activeTable is null)
            throw new ReportValidationException(
                [new ValidationError(
                    "activeTable",
                    $"unknown table '{document.ActiveTable.Trim()}'")]);
        // Accept harmless casing/outer whitespace at the boundary, but return and
        // persist the exact document-owned table identifier.
        document.ActiveTable = activeTable;

        var refreshTargets = document.Tables
            .Where(pair => pair.Value.Schema is null)
            .Select(pair => pair.Key)
            .ToList();
        // One compiler and one read scope serve every null cache. Parent plans and
        // dynamic Pivot discoveries are memoized, so shared ancestry is compiled once.
        var definitionSchema = await GetSchema(definition, contextParams, ct);
        var sqlCompiler = DialectSupport.GetCompiler(definition.GetEffectiveDialect());
        await using var connection = await _connections.Open(definition, ct);
        await using var scope = await _connections.BeginReadScope(connection, definition, ct);
        var reader = CreateReader(connection, sqlCompiler, definition, contextParams, scope.Transaction);
        var tableCompiler = new ComposableTableCompiler(
            definition,
            document,
            definitionSchema,
            evaluationUtcNow,
            (query, rowDimensions, columnDimensions, values, token) =>
                reader.ReadPivotGroups(query, rowDimensions, columnDimensions, values, token));
        foreach (var tableId in refreshTargets)
            _ = tableCompiler.CompleteForTarget(await tableCompiler.Compile(tableId, ct));
        if (executeActive)
        {
            var activePlan = tableCompiler.CompleteForTarget(
                await tableCompiler.Compile(activeTable, ct));
            results[activeTable] = await ExecuteComposablePlan(
                definition,
                document,
                activePlan,
                reader,
                evaluationUtcNow,
                Stopwatch.StartNew(),
                ct);
        }
        else
        {
            // Advisory cache presence never suppresses semantic validation.
            _ = tableCompiler.CompleteForTarget(
                await tableCompiler.Compile(activeTable, ct));
        }

        // Every named table reached while compiling a refresh target or the active
        // table already has a live relation and schema in the memo. Replace its
        // advisory cache even when the submitted value was non-null, so a server-
        // returned cache never contradicts work this request has just completed.
        // Dormant, uncompiled alternatives retain their cache without causing extra
        // database work.
        foreach (var (tableId, plan) in tableCompiler.Completed)
            document.Tables[tableId].Schema = CompiledColumns(plan)
                .Select(column => column with { })
                .ToList();
        await scope.CompleteAsync(ct);
        return new SchemaRefresh(document, results);
    }

    private static List<ColumnInfo> CompiledColumns(CompiledComposableTable plan)
        => plan.Relation.Schema.Columns.Select(column =>
        {
            plan.FormatSources.TryGetValue(column.Name, out var formatSource);
            return new ColumnInfo(column.Name, column.Label, column.KindName, column.IsComputed)
            {
                FormatSource = formatSource,
            };
        }).ToList();

    /// <summary>
    /// Executes arbitrary shape chains as one recursively compiled SQL relation. The
    /// terminal materializer is shared with Pivot/Chart presentation, but every shape
    /// and every relational compositor before it remains in SQL.
    /// </summary>
    private async Task<ReportResult> QueryComposableTable(
        ReportDefinition definition,
        ReportState state,
        IReadOnlyDictionary<string, object?> contextParams,
        DateTime evaluationUtcNow,
        Stopwatch stopwatch,
        CancellationToken ct)
    {
        var schema = await GetSchema(definition, contextParams, ct);
        var compiler = DialectSupport.GetCompiler(definition.GetEffectiveDialect());
        await using var connection = await _connections.Open(definition, ct);
        await using var scope = await _connections.BeginReadScope(connection, definition, ct);
        var reader = CreateReader(connection, compiler, definition, contextParams, scope.Transaction);
        var tableCompiler = new ComposableTableCompiler(
            definition,
            state,
            schema,
            evaluationUtcNow,
            (query, rowDimensions, columnDimensions, values, token) =>
                reader.ReadPivotGroups(query, rowDimensions, columnDimensions, values, token));
        var tableId = state.ActiveTable
            ?? throw new ReportValidationException(
                [new ValidationError("activeTable", "activeTable is required when tables are present")]);
        var plan = tableCompiler.CompleteForTarget(await tableCompiler.Compile(tableId, ct));
        var result = await ExecuteComposablePlan(
            definition,
            state,
            plan,
            reader,
            evaluationUtcNow,
            stopwatch,
            ct);
        await scope.CompleteAsync(ct);
        return result;
    }

    private static async Task<ReportResult> ExecuteComposablePlan(
        ReportDefinition definition,
        ReportState state,
        CompiledComposableTable plan,
        ReportQueryReader reader,
        DateTime evaluationUtcNow,
        Stopwatch stopwatch,
        CancellationToken ct)
    {
        var executionState = TerminalState(definition, state, plan, evaluationUtcNow);
        var chartTerminal = IsChartTerminal(plan);
        var composed = ComposableTerminalQueryComposer.Compose(
            definition,
            plan.Relation,
            plan.Terminal,
            evaluationUtcNow,
            executionState.PageIndex,
            executionState.PageSize,
            executionState.PageAll,
            plan.LastShape,
            chartTerminal);
        var queryRows = await reader.ReadTableQueries(
            composed,
            plan.Terminal.Breaks,
            plan.Terminal.Aggregates,
            ct);
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>>? shapeTotals = null;
        if (plan.LastShape is
            {
                Kind: ShapeKind.Pivot,
                PivotTotals: true,
                PivotTotalsRelation: { } totalsRelation,
                PivotColumns: { } pivotColumns,
                Metrics: { } pivotMetrics,
                PivotKeys: { } pivotKeys,
            })
        {
            var totalGroups = await reader.ReadPivotGroups(
                totalsRelation.Query,
                rowDimensionCount: 0,
                columnDimensionCount: pivotColumns.Count,
                valueCount: pivotMetrics.Count,
                ct);
            shapeTotals = BuildPivotTotals(totalGroups, pivotMetrics, pivotKeys);
        }
        if (chartTerminal && plan.LastShape is { Kind: ShapeKind.Chart, Chart: { } chart })
        {
            if (queryRows.TotalRows > definition.MaxChartPoints)
                throw new ReportValidationException(
                    [new ValidationError(
                        plan.LastShape.Path,
                        $"chart would draw more than {definition.MaxChartPoints} points — filter further or aggregate to fewer categories")]);
            if (chart.Type == ChartType.Pie)
            {
                var metric = plan.Relation.Schema.Columns[1].Name;
                if (queryRows.Rows.Any(row => row.TryGetValue(metric, out var value) && IsNegative(value)))
                    throw new ReportValidationException(
                        [new ValidationError($"{plan.LastShape.Path}.value", "pie charts require non-negative values")]);
            }
        }

        var executionRows = queryRows.Rows;
        var breakContinues = false;
        if (!chartTerminal
            && plan.Terminal.Breaks.Count > 0
            && !executionState.PageAll
            && executionRows.Count > executionState.PageSize)
        {
            var boundary = executionRows[executionState.PageSize];
            executionRows.RemoveRange(
                executionState.PageSize,
                executionRows.Count - executionState.PageSize);
            breakContinues = executionRows.Count > 0
                && SameBreakKey(executionRows[^1], boundary, plan.Terminal.Breaks);
        }
        var highlights = plan.Terminal.Decorations.Count == 0
            ? []
            : HighlightEvaluator.Evaluate(plan.Terminal.Decorations, executionRows);
        var rows = ReportRowProjector.Columns(executionRows, plan.Terminal.ProjectionColumns);
        var shapeColumns = plan.Relation.Schema.Columns.Select(column =>
        {
            plan.FormatSources.TryGetValue(column.Name, out var formatSource);
            return new ColumnInfo(column.Name, column.Label, column.KindName, column.IsComputed)
            {
                FormatSource = formatSource,
            };
        }).ToList();
        var available = ReportResultColumns.ForMaterializedTable(plan.Relation.Schema, shapeColumns);
        var visible = ReportResultColumns.Select(available, plan.Terminal.SelectColumns);
        var totals = MergeTotals(shapeTotals, queryRows.Aggregates);

        stopwatch.Stop();
        return new ReportResult
        {
            AvailableColumns = available,
            Columns = visible,
            Rows = rows,
            Page = chartTerminal
                ? new PageRequest { Index = 1, Size = Math.Max(1, rows.Count) }
                : new PageRequest
                {
                    Index = executionState.PageIndex,
                    Size = executionState.PageAll ? 0 : executionState.PageSize,
                },
            TotalRows = queryRows.TotalRows,
            Aggregates = totals,
            BreakTotals = queryRows.BreakTotals,
            BreakContinues = breakContinues,
            Highlights = highlights,
            Ignored = plan.Ignored,
            ElapsedMs = stopwatch.ElapsedMilliseconds,
        };
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> MergeTotals(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>>? shape,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> ordinary)
    {
        var merged = (shape ?? new Dictionary<string, IReadOnlyDictionary<string, object?>>())
            .ToDictionary(
                pair => pair.Key,
                pair => new Dictionary<string, object?>(pair.Value, StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
        foreach (var (column, values) in ordinary)
        {
            if (!merged.TryGetValue(column, out var target))
                merged[column] = target = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (function, value) in values) target[function] = value;
        }
        return merged.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyDictionary<string, object?>)pair.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    internal static IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> BuildPivotTotals(
        IReadOnlyList<PivotGroup> groups,
        IReadOnlyList<ValidMetric> metrics,
        IReadOnlyList<PivotColumnKey> keys)
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            var key = keys.FirstOrDefault(candidate =>
                ComposableTableCompiler.PivotKeysEqual(candidate.Values, group.ColumnKey));
            if (key is null)
                continue;
            foreach (var cell in key.Cells)
            {
                if (metrics.Count == 0)
                {
                    result[cell.Column.Name] = new Dictionary<string, object?> { ["count"] = group.Count };
                    continue;
                }
                var metricIndex = -1;
                for (var index = 0; index < metrics.Count; index++)
                    if (string.Equals(metrics[index].Id, cell.SourceName, StringComparison.OrdinalIgnoreCase))
                    {
                        metricIndex = index;
                        break;
                    }
                if (metricIndex < 0) continue;
                result[cell.Column.Name] = new Dictionary<string, object?>
                {
                    [ReportResultColumns.AggregateName(metrics[metricIndex].Fn)] = group.Values[metricIndex],
                };
            }
        }
        return result;
    }

    private static ValidatedState TerminalState(
        ReportDefinition definition,
        ReportState state,
        CompiledComposableTable plan,
        DateTime evaluationUtcNow)
    {
        var requestedSize = state.Page?.Size ?? definition.DefaultPageSize;
        var pageAll = requestedSize == 0;
        var pageSize = pageAll ? 0 : Math.Clamp(requestedSize, 1, definition.MaxPageSize);
        var pageIndex = pageAll ? 1 : Math.Max(1, state.Page?.Index ?? 1);
        return new ValidatedState
        {
            Policy = ColumnPolicy.From(definition),
            EvaluationUtcNow = evaluationUtcNow,
            Schema = plan.Relation.Schema,
            Operations = [],
            Rules = new ExpressionRulePlan([], [], plan.Terminal.Decorations),
            Search = null,
            Sorts = plan.Terminal.Sorts,
            SelectColumns = plan.Terminal.SelectColumns,
            ProjectionColumns = plan.Terminal.ProjectionColumns,
            Formats = plan.Formats,
            Aggregates = plan.Terminal.Aggregates,
            Breaks = plan.Terminal.Breaks,
            View = ValidView.Grid,
            PageIndex = pageIndex,
            PageSize = pageSize,
            PageAll = pageAll,
            Ignored = plan.Ignored,
            Labels = plan.Labels,
        };
    }

    /// <summary>Every named table uses the recursive completed-relation pipeline.</summary>
    private static bool RequiresComposablePipeline(ReportState state, string? activeTable)
        => state.Tables is { Count: > 0 } && !string.IsNullOrWhiteSpace(activeTable);

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
        var structural = StateStructureValidator.Collect(state);
        if (structural.Count > 0) throw new ReportValidationException(structural);
        if (definition.DefaultState is not null
            && StateStructureValidator.Collect(definition.DefaultState) is { Count: > 0 } defaultErrors)
            throw new InvalidOperationException(
                $"Report '{definition.Name}': the default state document is structurally invalid — "
                + $"{defaultErrors[0].Path}: {defaultErrors[0].Message}.");
        var document = ReportStateResolver.Resolve(definition.DefaultState, state);
        if (document.Tables is { Count: > 0 })
        {
            return await ExportComposableTable(
                definition,
                document,
                contextParams,
                evaluationUtcNow,
                ct);
        }

        var validated = (await ValidateLegacyState(
            definition,
            document,
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

    private async Task<ExportResult> ExportComposableTable(
        ReportDefinition definition,
        ReportState document,
        IReadOnlyDictionary<string, object?> contextParams,
        DateTime evaluationUtcNow,
        CancellationToken ct)
    {
        var schema = await GetSchema(definition, contextParams, ct);
        var sqlCompiler = DialectSupport.GetCompiler(definition.GetEffectiveDialect());
        await using var connection = await _connections.Open(definition, ct);
        await using var scope = await _connections.BeginReadScope(connection, definition, ct);
        var reader = CreateReader(connection, sqlCompiler, definition, contextParams, scope.Transaction);
        var compiler = new ComposableTableCompiler(
            definition,
            document,
            schema,
            evaluationUtcNow,
            (query, rowDimensions, columnDimensions, values, token) =>
                reader.ReadPivotGroups(query, rowDimensions, columnDimensions, values, token));
        var tableId = document.ActiveTable
            ?? throw new ReportValidationException(
                [new ValidationError("activeTable", "activeTable is required when tables are present")]);
        var plan = compiler.CompleteForTarget(await compiler.Compile(tableId, ct));
        var chartTerminal = IsChartTerminal(plan);
        var limit = chartTerminal ? definition.MaxChartPoints : definition.MaxRows;
        var mapped = ComposableTerminalQueryComposer.ComposeExport(
            definition,
            plan.Relation,
            plan.Terminal,
            plan.LastShape,
            limit);
        var read = await reader.ReadRows(
            mapped.Query,
            mapped.PublicNames,
            limit > 0 ? limit : null,
            ct);

        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> pivotTotals =
            new Dictionary<string, IReadOnlyDictionary<string, object?>>();
        if (plan.LastShape is
            {
                Kind: ShapeKind.Pivot,
                PivotTotals: true,
                PivotTotalsRelation: { } totalsRelation,
                PivotColumns: { } pivotColumns,
                Metrics: { } pivotMetrics,
                PivotKeys: { } pivotKeys,
            })
        {
            var groups = await reader.ReadPivotGroups(
                totalsRelation.Query,
                0,
                pivotColumns.Count,
                pivotMetrics.Count,
                ct);
            pivotTotals = BuildPivotTotals(groups, pivotMetrics, pivotKeys);
        }
        await scope.CompleteAsync(ct);

        if (chartTerminal && read.Truncated)
            throw new ReportValidationException(
                [new ValidationError(
                    plan.LastShape!.Path,
                    $"chart would draw more than {definition.MaxChartPoints} points — filter further or aggregate to fewer categories")]);
        if (chartTerminal && plan.LastShape is { Kind: ShapeKind.Chart, Chart.Type: ChartType.Pie })
        {
            var metric = plan.Relation.Schema.Columns[1].Name;
            if (read.Rows.Any(row => row.TryGetValue(metric, out var value) && IsNegative(value)))
                throw new ReportValidationException(
                    [new ValidationError($"{plan.LastShape.Path}.value", "pie charts require non-negative values")]);
        }

        var available = CompiledColumns(plan);
        var visible = ReportResultColumns.Select(available, plan.Terminal.SelectColumns);
        IReadOnlyList<IReadOnlyDictionary<string, object?>> exportRows = read.Rows;
        if (pivotTotals.Count > 0 && plan.LastShape?.Dimensions is { } rowDimensions)
            exportRows = PivotTableBuilder.RowsForExport(
                visible,
                exportRows,
                pivotTotals,
                rowDimensions);
        var rendered = TableExportRenderer.Render(
            available,
            visible,
            exportRows,
            plan.Relation.Schema,
            plan.Formats,
            ExportLabels(plan),
            plan.Formats);
        return new ExportResult(rendered.Columns, rendered.Rows, !chartTerminal && read.Truncated);
    }

    private static IReadOnlyDictionary<string, string> ExportLabels(CompiledComposableTable plan)
    {
        var labels = new Dictionary<string, string>(plan.Labels, StringComparer.OrdinalIgnoreCase);
        foreach (var column in plan.Relation.Schema.Columns)
        {
            if (labels.ContainsKey(column.Name)
                || !plan.FormatSources.TryGetValue(column.Name, out var source)
                || source is null
                || !labels.TryGetValue(source, out var sourceLabel))
                continue;
            var open = column.Label.LastIndexOf('(');
            var close = open < 0 ? -1 : column.Label.IndexOf(')', open + 1);
            labels[column.Name] = close > open
                ? $"{column.Label[..(open + 1)]}{sourceLabel}{column.Label[close..]}"
                : column.Label;
        }
        return labels;
    }

    private static bool IsChartTerminal(CompiledComposableTable plan)
        => plan.LastShape is { Kind: ShapeKind.Chart }
            && plan.Relation.Schema.Columns.Take(2).All(shapeColumn =>
                plan.Terminal.SelectColumns.Any(selected => string.Equals(
                    selected.Name,
                    shapeColumn.Name,
                    StringComparison.OrdinalIgnoreCase)));

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
