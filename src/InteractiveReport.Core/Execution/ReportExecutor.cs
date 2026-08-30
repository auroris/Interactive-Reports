using System.Data.Common;
using System.Diagnostics;
using InteractiveReport.Core.Composition;
using InteractiveReport.Core.Export;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Planning;
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
    /// Compiles the exported relation/schema of every null schema-cache target and fully
    /// validates the active table's owner-local result. Structural validation covers the
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
    /// Replaces every null per-table schema cache and fully validates only the active
    /// table's owner-local result. Grid, Group, and Chart schemas are derived without
    /// executing rows; only Pivot requires live discovery. Non-null dormant caches are
    /// preserved as advisory snapshots and never participate in expression binding.
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
        var target = refreshed.Document.Tables is { Count: > 0 }
            ? refreshed.Document.ActiveTable!
            : "definition";
        var result = refreshed.Results[target];
        result.Document = refreshed.Document;

        _logger?.LogInformation(
            "Report {Report} query completed in {ElapsedMs} ms with {RowCount} rows ({TotalRows} total)",
            definition.Name,
            result.ElapsedMs,
            result.Rows.Count,
            result.TotalRows);
        return result;
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
        ValidateSyntheticColumnIdentities(document);
        var results = new Dictionary<string, ReportResult>(StringComparer.OrdinalIgnoreCase);
        var hasNamedTables = document.Tables is { Count: > 0 };
        var activeTable = hasNamedTables ? ResolveActiveTable(document) : "definition";

        var refreshTargets = (document.Tables ?? [])
            .Where(pair => pair.Value.Schema is null)
            .Select(pair => pair.Key)
            .ToList();
        // One compiler and one read scope serve every null cache. Non-active targets
        // stop at their exported relation/schema: owner-local terminal validation and
        // request search belong only to the active target completed below. Parent plans
        // and dynamic Pivot discoveries are memoized, so shared ancestry is compiled once.
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
            _ = await tableCompiler.Compile(tableId, ct);
        if (executeActive)
        {
            var activePlan = tableCompiler.CompleteForTarget(
                await tableCompiler.Compile(activeTable, ct));
            results[activeTable] = await ExecuteComposablePlan(
                definition,
                activePlan,
                reader,
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
        if (hasNamedTables)
            foreach (var (tableId, plan) in tableCompiler.Completed)
                document.Tables![tableId].Schema = CompiledColumns(plan)
                    .Select(column => column with { })
                    .ToList();
        await scope.CompleteAsync(ct);
        return new SchemaRefresh(document, results);
    }

    private static List<ColumnInfo> CompiledColumns(CompiledComposableTable plan)
        => plan.Export.Bound.Relation.Output.Columns.Select(column =>
        {
            plan.FormatSources.TryGetValue(column.LogicalId, out var formatSource);
            return new ColumnInfo(
                column.LogicalId,
                column.DefaultLabel,
                column.Kind switch
                {
                    ColumnKind.Text => "text",
                    ColumnKind.Number => "number",
                    ColumnKind.Date => "date",
                    ColumnKind.Bool => "bool",
                    _ => "other",
                },
                column.IsComputed)
            {
                FormatSource = column.FormatSourceLogicalId ?? formatSource,
            };
        }).ToList();

    private static async Task<ReportResult> ExecuteComposablePlan(
        ReportDefinition definition,
        CompiledComposableTable plan,
        ReportQueryReader reader,
        Stopwatch stopwatch,
        CancellationToken ct)
    {
        var request = plan.RequestOverlay;
        var chartTerminal = IsChartTerminal(plan);
        var queryRows = await reader.ReadTableQueries(
            plan.ExecutionBundle,
            ct);
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>>? shapeTotals = null;
        if (plan.ExecutionBundle.PivotTotals is { } pivotTotals)
        {
            var totalGroups = await reader.ReadPivotGroups(
                pivotTotals.Query.Query,
                pivotTotals.Query.RowDimensionCount,
                pivotTotals.Query.ColumnDimensionCount,
                pivotTotals.Query.ValueCount,
                ct);
            shapeTotals = BuildPivotTotals(
                totalGroups,
                pivotTotals.Metrics,
                pivotTotals.Keys);
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
            && plan.Terminal.Breaks.Length > 0
            && !request.PageAll
            && executionRows.Count > request.PageSize)
        {
            var boundary = executionRows[request.PageSize];
            executionRows.RemoveRange(
                request.PageSize,
                executionRows.Count - request.PageSize);
            breakContinues = executionRows.Count > 0
                && SameBreakKey(executionRows[^1], boundary, plan.Terminal.Breaks);
        }
        var highlights = plan.Terminal.Decorations.Length == 0
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
        var available = ReportResultColumns.ForBoundRelation(plan.Relation.Schema, shapeColumns);
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
                    Index = request.PageIndex,
                    Size = request.PageAll ? 0 : request.PageSize,
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

    private static string ResolveActiveTable(ReportState document)
    {
        if (string.IsNullOrWhiteSpace(document.ActiveTable))
            throw new ReportValidationException(
                [new ValidationError("activeTable", "activeTable is required when tables are present")]);

        var requested = document.ActiveTable.Trim();
        var activeTable = document.Tables!.Keys.FirstOrDefault(tableId => string.Equals(
            tableId,
            requested,
            StringComparison.OrdinalIgnoreCase));
        if (activeTable is null)
            throw new ReportValidationException(
                [new ValidationError("activeTable", $"unknown table '{requested}'")]);

        // Accept harmless casing/outer whitespace at the boundary, but return and
        // persist the exact document-owned table identifier.
        document.ActiveTable = activeTable;
        return activeTable;
    }

    private static void ValidateSyntheticColumnIdentities(ReportState document)
    {
        var errors = SyntheticColumnIdentityValidator.Collect(document);
        if (errors.Count > 0) throw new ReportValidationException(errors);
    }

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
        ValidateSyntheticColumnIdentities(document);
        if (document.Tables is { Count: > 0 })
            ResolveActiveTable(document);
        return await ExportComposableTable(
            definition,
            document,
            contextParams,
            evaluationUtcNow,
            ct);
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
        var tableId = document.Tables is { Count: > 0 }
            ? document.ActiveTable!
            : "definition";
        var plan = compiler.CompleteForTarget(await compiler.Compile(tableId, ct));
        var chartTerminal = IsChartTerminal(plan);
        var limit = chartTerminal ? definition.MaxChartPoints : definition.MaxRows;
        var mapped = plan.ExecutionBundle.Export;
        var read = await reader.ReadRows(
            mapped.Query,
            mapped.PublicNames,
            limit > 0 ? limit : null,
            ct);

        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> pivotTotals =
            new Dictionary<string, IReadOnlyDictionary<string, object?>>();
        if (plan.ExecutionBundle.PivotTotals is { } pivotTotalsQuery)
        {
            var groups = await reader.ReadPivotGroups(
                pivotTotalsQuery.Query.Query,
                pivotTotalsQuery.Query.RowDimensionCount,
                pivotTotalsQuery.Query.ColumnDimensionCount,
                pivotTotalsQuery.Query.ValueCount,
                ct);
            pivotTotals = BuildPivotTotals(
                groups,
                pivotTotalsQuery.Metrics,
                pivotTotalsQuery.Keys);
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

    private static bool IsNegative(object? value)
        => value is not null && Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture) < 0;

    private static bool SameBreakKey(
        IReadOnlyDictionary<string, object?> left,
        IReadOnlyDictionary<string, object?> right,
        IReadOnlyList<ColumnModel> breaks)
        => breaks.All(column => Equals(left[column.Name], right[column.Name]));

    private ReportQueryReader CreateReader(
        DbConnection connection,
        Compiler compiler,
        ReportDefinition definition,
        IReadOnlyDictionary<string, object?> contextParams,
        DbTransaction? transaction = null)
        => new(connection, compiler, contextParams, definition, _logger, transaction);

}

/// <summary>Unpaged export payload; Truncated means a positive MaxRows was hit.</summary>
public sealed record ExportResult(
    IReadOnlyList<ColumnInfo> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    bool Truncated);

internal sealed record SchemaRefresh(
    ReportState Document,
    IReadOnlyDictionary<string, ReportResult> Results);
