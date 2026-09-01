// Report execution entrypoint: coordinates schema discovery, document validation, canonical
// table compilation, bounded database reads, and response shaping. A request uses one prepared
// connection and read scope so its count, totals, and page data describe one logical result.

using System.Data.Common;
using System.Diagnostics;
using InteractiveReport.Core.Composition;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Planning;
using InteractiveReport.Core.Schema;
using InteractiveReport.Core.Validation;
using Microsoft.Extensions.Logging;
using SqlKata.Compilers;

namespace InteractiveReport.Core.Execution;

/// <summary>
/// ReportExecutor orchestrates one report request by discovering its schema, validating state, composing,
/// executing, and shaping the result. Each request runs sequentially on one prepared
/// connection. Provider access and stage transformations live in focused collaborators.
/// </summary>
public sealed class ReportExecutor
{
    /// <summary>Caps every list-of-values response, independently of report row limits.</summary>
    public const int MaxLovItems = 50;

    /// <summary>Caps pivot discovery groups independently of per-definition dynamic-column limits.</summary>
    public const int MaxPivotGroups = 10_000;

    /// <summary>Sets the highest value permitted for a definition's <c>MaxChartPoints</c> setting.</summary>
    public const int MaxChartPointsCeiling = 10_000;

    private readonly ReportConnectionManager _connections;
    private readonly SchemaCache _schemaCache;
    private readonly ILogger? _logger;

    /// <summary>
    /// Initializes the executor with the connection, schema-cache, and logging services used by the request
    /// pipeline.
    /// </summary>
    /// <param name="connections">Creates unopened report database connections.</param>
    /// <param name="schemaCache">The cache used to reuse discovered schemas across requests.</param>
    /// <param name="logger">The host-provided logger that receives diagnostic events; <see langword="null"/> disables logging; defaults to <c>null</c>.</param>
    public ReportExecutor(
        IReportConnectionFactory connections,
        SchemaCache schemaCache,
        ILogger<ReportExecutor>? logger = null)
    {
        _connections = new ReportConnectionManager(connections, logger);
        _schemaCache = schemaCache;
        _logger = logger;
    }

    /// <summary>
    /// Returns the cached schema for a definition, discovering it on the first request.
    /// </summary>
    /// <param name="definition">The resolved definition whose base-query schema is required.</param>
    /// <param name="contextParams">Request-scoped parameter values referenced by the report definition.</param>
    /// <param name="ct">Cancels connection opening and discovery.</param>
    /// <returns>The cached schema or a newly discovered schema.</returns>
    /// <remarks>A cache miss opens and prepares one connection, executes schema discovery, and populates <see cref="SchemaCache"/>.</remarks>
    public async Task<ReportSchema> GetSchema(
        ReportDefinition definition,
        IReadOnlyDictionary<string, object?> contextParams,
        CancellationToken ct = default)
    {
        // One discovery task is shared by every concurrent caller of the same key, so it must not
        // be tied to the first caller's request: if that caller aborts, the others would fault with
        // a cancellation they never asked for and be reported as server errors. Discovery runs under
        // the command timeout instead, and each caller observes only its own token while waiting.
        var discovery = _schemaCache.GetOrDiscover(definition, async () =>
        {
            await using var connection = await _connections.Open(definition, CancellationToken.None);
            return await SchemaDiscovery.Discover(
                connection, definition, contextParams, _logger, CancellationToken.None);
        });
        return await discovery.WaitAsync(ct);
    }

    /// <summary>
    /// Compiles the exported relation and schema of every uncached target, then fully validates the active
    /// table's owner-local result. Structural validation covers the entire document; dormant alternatives
    /// with a non-null advisory cache remain deferred until selected or explicitly invalidated. Pivot
    /// targets alone require runtime column discovery.
    /// </summary>
    /// <param name="definition">The resolved definition used for schema and execution policy.</param>
    /// <param name="state">The submitted partial report-state document.</param>
    /// <param name="contextParams">Request-scoped parameter values referenced by the report definition.</param>
    /// <param name="ct">Cancels schema discovery, pivot discovery, and validation.</param>
    /// <returns>A task that completes after the effective document and active terminal validate.</returns>
    /// <remarks>May open a database read scope and execute pivot discovery; the refreshed detached document is discarded.</remarks>
    public async Task ValidateDocument(
        ReportDefinition definition,
        ReportState state,
        IReadOnlyDictionary<string, object?> contextParams,
        CancellationToken ct = default)
        => _ = await RefreshSchemaCaches(definition, state, contextParams, ct);

    /// <summary>
    /// Replaces every null per-table schema cache and fully validates only the active table's owner-local
    /// result. Grid, Group, and Chart schemas are derived without executing rows; only Pivot requires live
    /// discovery. Non-null dormant caches are preserved as advisory snapshots and never participate in
    /// expression binding.
    /// </summary>
    /// <param name="definition">The resolved definition used for schema and execution policy.</param>
    /// <param name="state">The submitted partial report-state document.</param>
    /// <param name="contextParams">Request-scoped parameter values referenced by the report definition.</param>
    /// <param name="ct">Cancels schema discovery, pivot discovery, and validation.</param>
    /// <returns>The detached effective document with every compiled table cache refreshed.</returns>
    /// <remarks>May open a database read scope and execute pivot discovery; it does not execute active terminal rows.</remarks>
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
    /// Resolves, validates, compiles, and executes the active table as one coherent report request.
    /// </summary>
    /// <param name="definition">The resolved definition used for schema, SQL, and execution limits.</param>
    /// <param name="state">The submitted partial report-state document.</param>
    /// <param name="contextParams">Request-scoped parameter values referenced by the report definition.</param>
    /// <param name="ct">Cancels schema discovery, validation, database execution, and result reading.</param>
    /// <returns>The active result plus the detached effective document adopted by the client.</returns>
    /// <remarks>Opens one prepared connection/read scope, may execute pivot discovery and multiple terminal queries, and emits a completion log.</remarks>
    /// <example>
    /// <code><![CDATA[
    /// var definition = await definitions.Find("orders", ct)
    ///     ?? throw new KeyNotFoundException("Report 'orders' was not found.");
    /// var result = await executor.Query(definition, state, contextParameters, ct);
    /// ]]></code>
    /// </example>
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

    /// <summary>
    /// Returns a bounded distinct list for one column of the active table represented by a complete,
    /// possibly unsaved report document.
    /// </summary>
    /// <param name="definition">The resolved definition used for schema, SQL, and execution policy.</param>
    /// <param name="request">
    /// The complete document, its active table, one column, and optional case-insensitive
    /// substring search text.
    /// </param>
    /// <param name="contextParams">Request-scoped parameter values referenced by the report definition.</param>
    /// <param name="ct">Cancels schema discovery, validation, compilation, and database execution.</param>
    /// <returns>At most 50 distinct matching values from the completed active relation.</returns>
    /// <remarks>
    /// The request document is compiled in memory and need not have been persisted. This operation does
    /// not apply a separate column authorization policy; callers use the same report-query authorization
    /// boundary as the active table query. <see cref="ReportLovRequest.Search"/> narrows the
    /// returned choices and is partial-match by default. It does not define the filter or
    /// highlight condition ultimately authored by a client.
    /// </remarks>
    /// <example>
    /// <code><![CDATA[
    /// var values = await executor.Lov(definition, new ReportLovRequest
    /// {
    ///     Document = currentDocument,
    ///     Table = currentDocument.ActiveTable,
    ///     Column = "STATUS",
    ///     Search = "op"
    /// }, contextParameters, ct);
    /// ]]></code>
    /// </example>
    public async Task<ReportLovResult> Lov(
        ReportDefinition definition,
        ReportLovRequest request,
        IReadOnlyDictionary<string, object?> contextParams,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(contextParams);

        if (request.Document is null)
            throw new ReportValidationException(
                [new ValidationError("document", "the current report document is required")]);

        var structural = StateStructureValidator.Collect(request.Document);
        if (structural.Count > 0) throw new ReportValidationException(structural);
        if (definition.DefaultState is not null
            && StateStructureValidator.Collect(definition.DefaultState) is { Count: > 0 } defaultErrors)
            throw new InvalidOperationException(
                $"Report '{definition.Name}': the default state document is structurally invalid — "
                + $"{defaultErrors[0].Path}: {defaultErrors[0].Message}.");

        var document = ReportStateResolver.Resolve(definition.DefaultState, request.Document);
        ValidateSyntheticColumnIdentities(document);
        var activeTable = document.Tables is { Count: > 0 }
            ? ResolveActiveTable(document)
            : "definition";
        var requestedTable = request.Table?.Trim();
        if (string.IsNullOrEmpty(requestedTable))
            throw new ReportValidationException(
                [new ValidationError("table", "the current active table is required")]);
        if (!string.Equals(requestedTable, activeTable, StringComparison.OrdinalIgnoreCase))
            throw new ReportValidationException(
                [new ValidationError("table", "table must identify the submitted document's active table")]);

        var requestedColumn = request.Column?.Trim();
        if (string.IsNullOrEmpty(requestedColumn))
            throw new ReportValidationException(
                [new ValidationError("column", "one current-table column is required")]);
        if (request.Search is { Length: > 200 })
            throw new ReportValidationException(
                [new ValidationError("search", "search cannot exceed 200 characters")]);

        var schema = await GetSchema(definition, contextParams, ct);
        var sqlCompiler = DialectSupport.GetCompiler(definition.GetEffectiveDialect());
        await using var connection = await _connections.Open(definition, ct);
        await using var scope = await _connections.BeginReadScope(connection, definition, ct);
        var reader = CreateReader(connection, sqlCompiler, definition, contextParams, scope.Transaction);
        var compiler = new ComposableTableCompiler(
            definition,
            document,
            schema,
            DateTime.UtcNow,
            (query, rowDimensions, columnDimensions, values, token) =>
                reader.ReadPivotGroups(query, rowDimensions, columnDimensions, values, token));
        var plan = compiler.CompleteForTarget(await compiler.Compile(activeTable, ct));
        if (!plan.Relation.Schema.TryGetValue(requestedColumn, out var column))
            throw new ReportValidationException(
                [new ValidationError("column", $"unknown active-table column '{requestedColumn}'")]);

        var mapped = ComposableTerminalQueryComposer.ComposeLov(
            definition,
            plan.Relation,
            column,
            request.Search,
            MaxLovItems);
        var rows = await reader.ReadRows(
            mapped.Query,
            mapped.PublicNames,
            MaxLovItems,
            ct);
        await scope.CompleteAsync(ct);
        return new ReportLovResult(
            activeTable,
            column.Name,
            column.KindName,
            rows.Rows.Select(row => row[column.Name]).ToList(),
            rows.Truncated);
    }

    /// <summary>
    /// Implements the shared document-resolution, compilation, optional execution, and cache-refresh pipeline.
    /// </summary>
    /// <param name="definition">The resolved definition used by every compilation stage.</param>
    /// <param name="state">The submitted partial state.</param>
    /// <param name="contextParams">Request-scoped parameter values referenced by the report definition.</param>
    /// <param name="evaluationUtcNow">The fixed UTC timestamp used to evaluate time-sensitive expressions consistently throughout the request.</param>
    /// <param name="ct">Cancels schema discovery, read-scope setup, pivot discovery, compilation, and execution.</param>
    /// <param name="executeActive">Whether to materialize the active terminal after compilation.</param>
    /// <returns>The detached effective document and any active result keyed by table id.</returns>
    /// <remarks>Opens one connection/read scope, refreshes advisory caches on compiled tables, and may execute database queries.</remarks>
    /// <exception cref="ReportValidationException">Thrown when the report state violates the report contract.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the report definition contains a structurally invalid default-state document.</exception>
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

        // Return the effective document, not merely the partial request. The
        // client adopts this value, so inherited default tables and their refreshed caches must
        // be present in the response.
        var document = ReportStateResolver.Resolve(definition.DefaultState, state);
        ValidateSyntheticColumnIdentities(document);
        var results = new Dictionary<string, ReportResult>(StringComparer.OrdinalIgnoreCase);
        var hasNamedTables = document.Tables is { Count: > 0 };
        var activeTable = hasNamedTables ? ResolveActiveTable(document) : "definition";

        var refreshTargets = (document.Tables ?? [])
            .Where(pair => pair.Value.Schema is null)
            .Select(pair => pair.Key)
            .ToList();
        // One compiler and one read scope serve every null cache. Non-active
        // targets stop at their exported relation/schema: owner-local terminal validation and
        // request search belong only to the active target completed below. Parent plans and
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

        // Every named table reached while compiling a refresh target or the
        // active table already has a live relation and schema in the memo. Replace its advisory
        // cache even when the submitted value was non-null, so a server-returned cache never
        // contradicts work this request has just completed. Dormant, uncompiled alternatives
        // retain their cache without causing extra database work.
        if (hasNamedTables)
            foreach (var (tableId, plan) in tableCompiler.Completed)
                document.Tables![tableId].Schema = CompiledColumns(plan)
                    .Select(column => column with { })
                    .ToList();
        await scope.CompleteAsync(ct);
        return new SchemaRefresh(document, results);
    }

    /// <summary>
    /// Projects compiled output contracts into protocol column metadata.
    /// </summary>
    /// <param name="plan">The compiled table whose child-visible output becomes cached metadata.</param>
    /// <returns>Protocol columns in public output order, including inherited format source and pivot metric identity.</returns>
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
                PivotMetricId = PivotMetricId(column),
            };
        }).ToList();

    /// <summary>
    /// Executes all terminal datasets for a completed plan and shapes the public result.
    /// </summary>
    /// <param name="definition">Supplies chart limits.</param>
    /// <param name="plan">The completed active-table plan and terminal bundle.</param>
    /// <param name="reader">Executes the plan on the prepared connection/read scope.</param>
    /// <param name="stopwatch">The request timer used to report execution duration.</param>
    /// <param name="ct">Cancels database execution and reading.</param>
    /// <returns>Rows, columns, paging, totals, highlights, ignored rules, and elapsed time.</returns>
    /// <exception cref="ReportValidationException">Thrown when chart point count or pie values violate chart constraints.</exception>
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
        var output = plan.Export.Bound.Relation.Output;
        var shapeColumns = plan.Relation.Schema.Columns.Select(column =>
        {
            plan.FormatSources.TryGetValue(column.Name, out var formatSource);
            output.TryGetValue(column.Name, out var contract);
            return new ColumnInfo(column.Name, column.Label, column.KindName, column.IsComputed)
            {
                FormatSource = formatSource,
                PivotMetricId = contract is null ? null : PivotMetricId(contract),
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
            ConfiguredLabels = definition.GetEffectiveColumnLabels()
                ?? new Dictionary<string, string>(),
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

    /// <summary>
    /// Builds the stable logical identifier for a generated pivot metric.
    /// </summary>
    /// <param name="column">The bound output column whose lineage should be inspected.</param>
    /// <returns>The stable identifier for the pivot metric.</returns>
    private static string? PivotMetricId(BoundColumnContract column)
        => column.Lineage is BoundPivotCellColumnLineage pivot
            ? pivot.MetricId
            : null;

    /// <summary>
    /// Merges pivot-total rows into the terminal result without changing column order.
    /// </summary>
    /// <param name="shape">Pivot-cell totals, or <see langword="null"/> when no pivot totals were requested.</param>
    /// <param name="ordinary">Footer aggregates keyed by column and function.</param>
    /// <returns>A detached case-insensitive union of both total channels.</returns>
    /// <exception cref="InvalidOperationException">Thrown when both channels produce the same column/function identity.</exception>
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
            foreach (var (function, value) in values)
                if (!target.TryAdd(function, value))
                    throw new InvalidOperationException(
                        $"Pivot and footer totals both produced aggregate '{function}' "
                        + $"for column '{column}'. The compiler must reject this ambiguous plan.");
        }
        return merged.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyDictionary<string, object?>)pair.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Combines pivot total result sets into protocol total rows.
    /// </summary>
    /// <param name="groups">Grouped total rows returned by pivot discovery SQL.</param>
    /// <param name="metrics">Validated metrics matching group value ordinals.</param>
    /// <param name="keys">Registered dynamic keys and cell output columns.</param>
    /// <returns>One aggregate map per matched public pivot cell; unknown groups or metrics are ignored.</returns>
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

    /// <summary>
    /// Resolves the requested active table case-insensitively and restores its exact document-owned spelling.
    /// </summary>
    /// <param name="document">The effective document containing named tables.</param>
    /// <returns>The canonical active table identifier.</returns>
    /// <remarks>Mutates <paramref name="document"/> when casing or surrounding whitespace differs.</remarks>
    /// <exception cref="ReportValidationException">Thrown when activeTable is missing or unknown.</exception>
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

        // Accept harmless casing/outer whitespace at the boundary, but return and persist the
        // exact document-owned table identifier.
        document.ActiveTable = activeTable;
        return activeTable;
    }

    /// <summary>
    /// Rejects duplicate, colliding, or reserved document-wide synthetic column identities.
    /// </summary>
    /// <param name="document">The effective document to inspect.</param>
    /// <exception cref="ReportValidationException">Thrown when the report state violates the report contract.</exception>
    private static void ValidateSyntheticColumnIdentities(ReportState document)
    {
        var errors = SyntheticColumnIdentityValidator.Collect(document);
        if (errors.Count > 0) throw new ReportValidationException(errors);
    }

    /// <summary>
    /// Determines whether the terminal relation is a chart.
    /// </summary>
    /// <param name="plan">The completed plan and its terminal selection.</param>
    /// <returns><see langword="true"/> when the relation terminates in a chart; otherwise, <see langword="false"/>.</returns>
    private static bool IsChartTerminal(CompiledComposableTable plan)
        => plan.LastShape is { Kind: ShapeKind.Chart }
            && plan.Relation.Schema.Columns.Take(2).All(shapeColumn =>
                plan.Terminal.SelectColumns.Any(selected => string.Equals(
                    selected.Name,
                    shapeColumn.Name,
                    StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Determines whether a non-null provider numeric value is negative.
    /// </summary>
    /// <param name="value">The numeric provider value to test.</param>
    /// <returns><see langword="true"/> when the numeric value is negative; otherwise, <see langword="false"/>.</returns>
    private static bool IsNegative(object? value)
    {
        // A metric column whose provider type is unknown (SQLite expression columns) may arrive as
        // text; a value that is not a number is simply not negative rather than a conversion error.
        switch (value)
        {
            case null:
                return false;
            case string text:
                return decimal.TryParse(
                        text,
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var parsed)
                    && parsed < 0;
            case IConvertible convertible:
                try
                {
                    return convertible.ToDouble(System.Globalization.CultureInfo.InvariantCulture) < 0;
                }
                catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
                {
                    return false;
                }
            default:
                return false;
        }
    }

    /// <summary>
    /// Determines whether two projected rows have equal values for every break column.
    /// </summary>
    /// <param name="left">The last included row on the page.</param>
    /// <param name="right">The first sentinel row after the page.</param>
    /// <param name="breaks">The ordered break columns whose values define a group boundary.</param>
    /// <returns><see langword="true"/> when the compared values have the same break key; otherwise, <see langword="false"/>.</returns>
    private static bool SameBreakKey(
        IReadOnlyDictionary<string, object?> left,
        IReadOnlyDictionary<string, object?> right,
        IReadOnlyList<ColumnModel> breaks)
        => breaks.All(column => Equals(left[column.Name], right[column.Name]));

    /// <summary>
    /// Creates a query reader bound to the active connection, transaction, and request context.
    /// </summary>
    /// <param name="connection">The open prepared connection.</param>
    /// <param name="compiler">The SQL compiler for the configured database dialect.</param>
    /// <param name="definition">The resolved definition supplying timeout, dialect, and consistency.</param>
    /// <param name="contextParams">Request-scoped parameter values referenced by the report definition.</param>
    /// <param name="transaction">The transaction that keeps related database reads consistent; defaults to <c>null</c>.</param>
    /// <returns>A reader configured with this executor's optional logger.</returns>
    private ReportQueryReader CreateReader(
        DbConnection connection,
        Compiler compiler,
        ReportDefinition definition,
        IReadOnlyDictionary<string, object?> contextParams,
        DbTransaction? transaction = null)
        => new(connection, compiler, contextParams, definition, _logger, transaction);

}

/// <summary>Contains a refreshed effective document and any active result produced in the same read scope.</summary>
internal sealed record SchemaRefresh(
    ReportState Document,
    IReadOnlyDictionary<string, ReportResult> Results);
