// Named-table compilation entrypoint: recursively binds each table against the completed export
// of its parent, memoizes shared ancestors, and produces provider-neutral execution contracts.
// Structural normalization, schema binding, relation lowering, and terminal request overlays stay
// separate so child tables inherit data semantics without inheriting a parent's presentation state.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using InteractiveReport.Core.Composition;
using InteractiveReport.Core.Expressions;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Planning;
using InteractiveReport.Core.Schema;
using InteractiveReport.Core.Validation;
using SqlKata;

namespace InteractiveReport.Core.Execution;

/// <summary>
/// Compiles a report's named-table graph from parent to child. Each <c>from</c> edge consumes only
/// the parent's bound export, while selection, sorting, highlighting, and other renderer state remain
/// local to the declaring table. Group, chart, and data-derived pivot shapes remain composable relations.
/// </summary>
internal sealed class ComposableTableCompiler
{
    internal const int MaxRelationStages = 256;
    // Reserve terminal page/export and grouped aggregate wrappers below SQL Server's
    // practical derived-table nesting ceiling.
    internal const int MaxSqlServerRelationStages = 22;
    // Below the narrowest supported provider limit (Oracle: 1000 selected columns),
    // leaving room for provider/terminal helper projections.
    internal const int MaxGeneratedColumns = 900;
    internal const int MaxPivotBindings = 1800;
    internal const int MaxShapeMetrics = 256;

    private readonly ReportDefinition _definition;
    private readonly ReportState _document;
    private readonly ReportSchema _definitionSchema;
    private readonly ColumnPolicy _policy;
    private readonly DateTime _evaluationUtcNow;
    private readonly Func<Query, int, int, int, CancellationToken, Task<List<PivotGroup>>> _readPivotGroups;
    private readonly SqlKataRelationLowerer _lowerer;
    private readonly DynamicPivotColumnIdentityRegistry _pivotIdentities;
    private readonly Dictionary<string, CompiledComposableTable> _memo =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _visiting = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Creates a request-scoped compiler with a fixed clock and a callback for pivot-key discovery.
    /// </summary>
    /// <param name="definition">The authoritative report definition, SQL dialect, policies, and execution limits.</param>
    /// <param name="document">The mutable report state containing the named-table graph and request overlay.</param>
    /// <param name="definitionSchema">The schema exposed by the definition's base query.</param>
    /// <param name="evaluationUtcNow">The UTC instant shared by every time-sensitive expression in this compilation.</param>
    /// <param name="readPivotGroups">Executes the bounded grouping query used to discover dynamic pivot column keys.</param>
    public ComposableTableCompiler(
        ReportDefinition definition,
        ReportState document,
        ReportSchema definitionSchema,
        DateTime evaluationUtcNow,
        Func<Query, int, int, int, CancellationToken, Task<List<PivotGroup>>> readPivotGroups)
    {
        _definition = definition;
        _document = document;
        _definitionSchema = definitionSchema;
        _policy = ColumnPolicy.From(definition);
        _evaluationUtcNow = evaluationUtcNow;
        _readPivotGroups = readPivotGroups;
        _lowerer = new SqlKataRelationLowerer(
            definition.GetEffectiveDialect(),
            evaluationUtcNow);
        _pivotIdentities = new DynamicPivotColumnIdentityRegistry(
            ReservedLogicalIds(document, definitionSchema));
    }

    /// <summary>
    /// Gets the plans compiled so far, keyed case-insensitively by canonical table identifier.
    /// The view grows as <see cref="Compile"/> completes additional tables.
    /// </summary>
    public IReadOnlyDictionary<string, CompiledComposableTable> Completed => _memo;

    /// <summary>
    /// Compiles the definition source or one named table, recursively compiling and memoizing its ancestors first.
    /// </summary>
    /// <param name="tableId">The requested table identifier, or the reserved name <c>definition</c>.</param>
    /// <param name="ct">Cancels recursive compilation and any pivot-discovery query.</param>
    /// <returns>The compiled structural export and table-local instructions. Call <see cref="CompleteForTarget"/> before execution.</returns>
    /// <remarks>Named-table compilation may normalize kind and <c>from</c> casing in the retained report document and invoke the pivot reader.</remarks>
    /// <exception cref="ReportValidationException">Thrown for unknown tables, cycles, excessive depth or relation complexity, and invalid composables.</exception>
    public async Task<CompiledComposableTable> Compile(string tableId, CancellationToken ct)
    {
        var requested = tableId.Trim();
        if (string.Equals(requested, "definition", StringComparison.OrdinalIgnoreCase))
            return Definition();
        return await CompileTable(requested, depth: 1, "activeTable", ct);
    }

    /// <summary>
    /// Completes a compiled table for the active request by applying search, binding table-local
    /// renderer instructions, resolving projection support columns, and building terminal queries.
    /// Request overlays are deliberately absent from memoized exports so descendants cannot inherit them.
    /// </summary>
    /// <param name="plan">The structurally compiled table to finish for execution.</param>
    /// <returns>A copy containing the active execution relation, terminal bindings, request overlay, and execution bundle.</returns>
    /// <exception cref="ReportValidationException">Thrown when request search, pivot totals, terminal widths, or local instructions are incompatible.</exception>
    public CompiledComposableTable CompleteForTarget(CompiledComposableTable plan)
    {
        ValidatePivotTotalsCompatibility(plan);
        var request = BoundRequestOverlay.From(_definition, _document);
        BoundRelationNode executionNode = plan.Export.Bound.Relation;
        var searchApplied = request.Search is not null;
        if (searchApplied)
            executionNode = new BoundSearchRelation(
                executionNode,
                request.Search!,
                executionNode.Output);
        var executionRelation = _lowerer.Lower(executionNode).AsComposable();
        plan = plan with
        {
            Local = plan.Local with
            {
                ExecutionNode = executionNode,
                ExecutionRelation = executionRelation,
            },
            SearchApplied = searchApplied,
        };
        EnsureRelationComplexity("tables", plan.Relation);

        var errors = new List<ValidationError>();
        var ignored = plan.Ignored.ToList();
        var terminal = CanonicalLocalResultBinder.Bind(
            plan.Local.Instructions,
            plan.Relation.Schema,
            _policy,
            errors,
            ignored);
        ValidateTerminalWidths(plan, terminal, errors);
        ValidatePivotFooterAggregateCompatibility(plan, terminal, errors);
        var projection = ColumnBindingRules.ResolveRendererColumns(
            plan.Formats,
            terminal.SelectColumns,
            plan.Relation.Schema,
            ignored);
        foreach (var breakColumn in terminal.Breaks)
            if (!projection.Any(column => string.Equals(
                    column.Name,
                    breakColumn.Name,
                    StringComparison.OrdinalIgnoreCase)))
                projection.Add(breakColumn);
        if (plan.ShapeCount == 0 && _definition.EditLink is not null)
            ColumnBindingRules.AddEditLinkColumns(
                _definition.EditLink,
                projection,
                plan.Relation.Schema,
                ignored);
        terminal = terminal with
        {
            Labels = plan.Labels.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase),
            Formats = plan.Formats.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase),
            ProjectionColumns = projection.ToImmutableArray(),
        };
        if (errors.Count > 0) throw new ReportValidationException(errors);
        var chartTerminal = plan.LastShape is { Kind: ShapeKind.Chart }
            && plan.Relation.Schema.Columns.Take(2).All(shapeColumn =>
                terminal.SelectColumns.Any(selected => string.Equals(
                    selected.Name,
                    shapeColumn.Name,
                    StringComparison.OrdinalIgnoreCase)));
        var bundle = TerminalExecutionBundleBuilder.Build(
            _definition,
            plan.Relation,
            terminal,
            _evaluationUtcNow,
            request,
            plan.LastShape,
            chartTerminal);
        return plan with
        {
            Local = plan.Local with
            {
                Terminal = terminal,
                ExecutionBundle = bundle,
                RequestOverlay = request,
            },
            Ignored = ignored.ToImmutableArray(),
        };
    }

    /// <summary>
    /// Creates the root export for the report definition's opaque SQL and authoritative schema.
    /// </summary>
    /// <returns>A fresh, uncompleted compiled table with no named-table shapes or local presentation instructions.</returns>
    private CompiledComposableTable Definition()
    {
        var labels = ColumnBindingRules.ResolveLabels(_definition.GetEffectiveColumnLabels())
            .ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);
        var output = BoundOutputContract.FromSchema(
            _definition.Name,
            _definitionSchema,
            labels);
        var node = new BoundOpaqueSqlSource(
            _definition.Name,
            _definition.Sql,
            _definition.GetEffectiveDialect(),
            output);
        var boundExport = BoundTableExport.Create(
            "definition",
            node,
            shapeCount: 0,
            computedRuleCount: 0,
            filterRuleCount: 0);
        return new(
            Export: new CompiledTableExport(
                Bound: boundExport,
                ShapeCount: 0,
                RelationStages: 0,
                ComputedRuleCount: 0,
                FilterRuleCount: 0),
            Local: new CompiledTableLocalResult(
                ExecutionNode: null,
                ExecutionRelation: null,
                ExecutionBundle: null,
                RequestOverlay: null,
                Shape: null,
                Terminal: EmptyLayer(_definitionSchema),
                Instructions: new CanonicalLocalResult(
                    null,
                    null,
                    [],
                    CanonicalRulePopulation.Empty,
                    null,
                    [])),
            SearchApplied: false,
            Ignored: []);
    }

    /// <summary>
    /// Resolves and compiles one named table, recursively importing only its parent's completed export.
    /// </summary>
    /// <param name="requestedId">The case-insensitive table identifier requested by the caller or child edge.</param>
    /// <param name="depth">The one-based ancestry depth used to enforce the table nesting limit.</param>
    /// <param name="incomingEdgePath">The source path to use when the identifier, depth, or edge is invalid.</param>
    /// <param name="ct">Cancels recursive compilation and pivot-key discovery.</param>
    /// <returns>The memoized compiled table containing a child-safe export and owner-local instructions.</returns>
    /// <remarks>Canonicalizes composable kind names and <c>from</c> identifiers in the report document, invokes pivot discovery when needed, and updates the memo table on success.</remarks>
    /// <exception cref="ReportValidationException">Thrown when the table graph, composables, schema bindings, or generated relation violate a report limit.</exception>
    private async Task<CompiledComposableTable> CompileTable(
        string requestedId,
        int depth,
        string incomingEdgePath,
        CancellationToken ct)
    {
        if (_memo.TryGetValue(requestedId, out var existing)) return existing;
        if (depth > StateStructureValidator.MaxTableDepth)
            throw Validation(
                incomingEdgePath,
                $"table ancestry may contain at most {StateStructureValidator.MaxTableDepth} tables");
        if (!_visiting.Add(requestedId))
            throw Validation(incomingEdgePath, $"table delegation contains a cycle at '{requestedId}'");

        try
        {
            var (tableId, table) = ResolveTable(requestedId, incomingEdgePath);
            if (string.IsNullOrWhiteSpace(table.From))
                throw Validation(
                    $"tables.{tableId}.from",
                    "from is required and must be 'definition' or another table identifier");

            var specification = CanonicalTableNormalizer.Normalize(
                table,
                $"tables.{tableId}");

            // Persistence normalization is separate from execution planning. The
            // canonical specification above was already built from deep snapshots.
            foreach (var syntax in table.Composables ?? [])
                if (syntax is not null
                    && ComposableSemanticsCatalog.TryResolve(syntax.Kind, out var semantics))
                    syntax.Kind = semantics.DocumentKind;

            CompiledComposableTable parent;
            if (string.Equals(table.From.Trim(), "definition", StringComparison.OrdinalIgnoreCase))
            {
                table.From = "definition";
                parent = Definition();
            }
            else
            {
                var (parentId, _) = ResolveTable(table.From.Trim(), $"tables.{tableId}.from");
                table.From = parentId;
                parent = await CompileTable(
                    parentId,
                    depth + 1,
                    $"tables.{tableId}.from",
                    ct);
            }
            // A child imports only this channel. The parent's Local result never
            // participates in binding or lowering the child.
            var inherited = parent.Export;
            BoundRelationNode relationNode = new BoundExportReference(
                inherited.Bound.TableId,
                inherited.Bound,
                $"tables.{tableId}.from",
                $"{_definition.Name}#{tableId}");
            var relation = _lowerer.Lower(relationNode).AsComposable();
            var searchApplied = false;
            var shapeCount = inherited.ShapeCount;
            var computedCount = inherited.ComputedRuleCount;
            var filterCount = inherited.FilterRuleCount;
            // A child consumes the parent's relation, schema, and metadata. Renderer
            // hints are terminal state owned by the named table that declared them.
            CompiledShape? lastShape = null;
            var ignored = parent.Ignored.ToList();
            var errors = new List<ValidationError>();

            if (specification.Shape is { } shape)
            {
                switch (shape)
                {
                    case CanonicalGroupShape group:
                    {
                        (relationNode, lastShape) = ApplyGroup(
                            relationNode,
                            group,
                            errors,
                            ignored);
                        break;
                    }
                    case CanonicalChartShape chartShape:
                    {
                        var chart = ShapeBindingRules.BindChart(
                            chartShape,
                            relationNode.Output.ToReportSchema().Lookup,
                            errors);
                        if (chart is not null)
                        {
                            relationNode = CreateChartNode(
                                relationNode,
                                chart,
                                $"{_definition.Name}#{tableId}#chart{shapeCount + 1}",
                                chartShape.Path);
                            lastShape = new CompiledShape(
                                ShapeKind.Chart,
                                chartShape.Path,
                                Chart: chart);
                        }
                        break;
                    }
                    case CanonicalPivotShape pivot:
                    {
                        ValidatePivotTotalsBeforeDiscovery(
                            specification,
                            pivot,
                            relationNode.Output,
                            tableId);
                        (relationNode, lastShape) = await ApplyPivot(
                            relationNode,
                            pivot,
                            tableId,
                            shapeCount + 1,
                            errors,
                            ignored,
                            ct);
                        break;
                    }
                    default:
                        throw new InvalidOperationException(
                            $"Unknown canonical shape '{shape.GetType().Name}'.");
                }

                shapeCount++;
                relation = _lowerer.Lower(relationNode).AsComposable();
                EnsureRelationComplexity(shape.SourcePath, relation);
            }

            var relationBinding = CanonicalRelationBinder.Bind(
                specification,
                $"{_definition.Name}#{tableId}",
                relationNode.Output,
                _policy,
                inherited.ComputedRuleCount,
                inherited.FilterRuleCount,
                errors,
                ignored);
            computedCount = relationBinding.ComputedRuleCount;
            filterCount = relationBinding.FilterRuleCount;
            relationNode = relationBinding.ApplyTo(relationNode);
            relation = _lowerer.Lower(relationNode).AsComposable();
            foreach (var mutation in relationBinding.Mutations)
                EnsureRelationComplexity(mutation.OperationPath, relation);

            var metadataOutput = relationNode.Output
                .ApplyMetadata(specification.Metadata)
                .Rename($"{_definition.Name}#{tableId}");
            relationNode = new BoundMetadataRelation(
                relationNode,
                metadataOutput,
                $"tables.{tableId}");
            relation = _lowerer.Lower(relationNode).AsComposable();

            if (lastShape is { Kind: ShapeKind.Pivot, PivotTotals: true })
            {
                lastShape = lastShape with
                {
                    HasPostShapeComputed = relationBinding.Mutations.Any(operation =>
                        operation.Kind == ComposableKind.Compute),
                    HasPostShapeFilters = relationBinding.Mutations.Any(operation =>
                        operation.Kind == ComposableKind.Filter),
                };
            }

            if (errors.Count > 0) throw new ReportValidationException(errors);
            var boundExport = BoundTableExport.Create(
                tableId,
                relationNode,
                shapeCount,
                computedCount,
                filterCount);
            var result = new CompiledComposableTable(
                Export: new CompiledTableExport(
                    Bound: boundExport,
                    ShapeCount: shapeCount,
                    RelationStages: relation.NestingDepth,
                    ComputedRuleCount: computedCount,
                    FilterRuleCount: filterCount),
                Local: new CompiledTableLocalResult(
                    ExecutionNode: null,
                    ExecutionRelation: null,
                    ExecutionBundle: null,
                    RequestOverlay: null,
                    Shape: lastShape,
                    Terminal: EmptyLayer(relation.Schema),
                    Instructions: specification.Local),
                SearchApplied: searchApplied,
                Ignored: ignored.ToImmutableArray());
            _memo[tableId] = result;
            return result;
        }
        finally
        {
            _visiting.Remove(requestedId);
        }
    }

    /// <summary>
    /// Binds a group declaration against the current output and appends a bound aggregate relation when valid.
    /// </summary>
    /// <param name="source">The relation whose columns are available as dimensions and metric inputs.</param>
    /// <param name="shape">The canonical group declaration to bind.</param>
    /// <param name="errors">The validation list that receives missing dimensions and limit violations.</param>
    /// <param name="ignored">The diagnostics list that receives policy-restricted or unknown columns.</param>
    /// <returns>The new group relation and compiled shape, or the unchanged source and a null shape when binding adds errors.</returns>
    private (BoundRelationNode Relation, CompiledShape? Shape) ApplyGroup(
        BoundRelationNode source,
        CanonicalGroupShape shape,
        List<ValidationError> errors,
        List<IgnoredItem> ignored)
    {
        var path = shape.Path;
        var before = errors.Count;
        var dimensions = ShapeBindingRules.BindDimensions(
            shape.By,
            "group",
            source.Output.ToReportSchema().Lookup,
            ignored);
        if (dimensions.Count == 0)
            errors.Add(new ValidationError($"{path}.by", "a group stage requires at least one valid group column"));
        ValidateMetricCount(shape.Values.Length, path, errors);
        var metrics = ShapeBindingRules.BindMetrics(
            shape.Values,
            source.Output.ToReportSchema(),
            errors,
            ignored);
        ValidateGroupedWidth(dimensions.Count, metrics.Count, $"{path}.by", errors);
        ValidateMedianProjectionWidth(dimensions, metrics, path, errors);
        if (errors.Count > before) return (source, null);

        var countName = UniqueLogicalName(
            dimensions.Select(column => column.Name)
                .Concat(metrics.Select(metric => metric.Id)),
            "__count");

        var relation = CreateGroupNode(
            source,
            $"{_definition.Name}#group",
            dimensions,
            metrics,
            countName,
            path);
        return (relation, new CompiledShape(
            ShapeKind.Group,
            path,
            Dimensions: dimensions,
            Metrics: metrics,
            CountName: countName));
    }

    /// <summary>
    /// Creates a bound grouping relation and derives the exact output contract for dimensions, count, and metrics.
    /// </summary>
    /// <param name="source">The relation to aggregate.</param>
    /// <param name="outputName">The logical name for the grouped output contract.</param>
    /// <param name="dimensions">The ordered pass-through grouping columns.</param>
    /// <param name="metrics">The validated aggregate metrics.</param>
    /// <param name="countName">The collision-free logical identifier for the generated row count.</param>
    /// <param name="sourcePath">The group or pivot declaration path retained for diagnostics.</param>
    /// <returns>A bound group node whose output carries aggregate lineage, labels, types, and inherited scalar formats.</returns>
    private static BoundGroupRelation CreateGroupNode(
        BoundRelationNode source,
        string outputName,
        IReadOnlyList<ColumnModel> dimensions,
        IReadOnlyList<ValidMetric> metrics,
        string countName,
        string sourcePath)
    {
        var boundDimensions = dimensions
            .Select(dimension => source.Output.GetRequired(dimension.Name) with
            {
                Lineage = new BoundPassThroughColumnLineage(dimension.Name),
            })
            .ToImmutableArray();
        var count = new BoundColumnContract(
            countName,
            "Count",
            "Count",
            typeof(long),
            IsNullable: false,
            IsComputed: false,
            new BoundAggregateColumnLineage(AggregateFn.Count, null));
        var boundMetrics = metrics.Select(metric => new BoundMetric(
                metric.Id,
                source.Output.GetRequired(metric.Column.Name),
                metric.Fn,
                sourcePath))
            .ToImmutableArray();
        var outputMetrics = boundMetrics.Select(metric =>
        {
            var input = metric.Input;
            var isCount = metric.Function is AggregateFn.Count or AggregateFn.CountDistinct;
            var model = MetricColumn(metric.Id, input.ToColumnModel(), metric.Function);
            return BoundColumnContract.FromColumn(
                model,
                new BoundAggregateColumnLineage(metric.Function, input.LogicalId),
                $"{ReportResultColumns.AggregateName(metric.Function)}({input.EffectiveLabel})",
                isCount ? null : input.LocalFormat,
                isCount ? null : input.ExportedMask,
                isCount || input.ExportedMask is null ? null : input.LogicalId);
        }).ToImmutableArray();
        var output = BoundOutputContract.Create(
            outputName,
            boundDimensions.Cast<BoundColumnContract>()
                .Concat([count])
                .Concat(outputMetrics));
        return new BoundGroupRelation(
            source,
            boundDimensions,
            boundMetrics,
            count,
            output,
            sourcePath);
    }

    /// <summary>
    /// Creates a chart relation with the fixed label/value roles expected by chart rendering.
    /// </summary>
    /// <param name="source">The relation containing the validated chart inputs.</param>
    /// <param name="chart">The bound chart definition, including optional aggregation.</param>
    /// <param name="outputName">The logical name for the chart output contract.</param>
    /// <param name="sourcePath">The chart declaration path retained for diagnostics.</param>
    /// <returns>A two-column bound chart relation with role lineage and applicable source formatting.</returns>
    private static BoundChartRelation CreateChartNode(
        BoundRelationNode source,
        ValidChart chart,
        string outputName,
        string sourcePath)
    {
        var shapeColumns = ReportResultColumns.ForChart(chart);
        var labelInput = source.Output.GetRequired(chart.Label.Name);
        var label = labelInput with
        {
            LogicalId = shapeColumns[0].Name,
            DefaultLabel = shapeColumns[0].Label,
            Lineage = new BoundChartColumnLineage("label", labelInput.LogicalId, null),
        };

        var metricInfo = shapeColumns[1];
        BoundColumnContract metric;
        if (chart.Value is null)
        {
            metric = new BoundColumnContract(
                metricInfo.Name,
                metricInfo.Label,
                metricInfo.Label,
                typeof(long),
                IsNullable: false,
                IsComputed: false,
                new BoundChartColumnLineage("value", null, chart.Fn));
        }
        else
        {
            var input = source.Output.GetRequired(chart.Value.Name);
            var isCount = chart.Fn is AggregateFn.Count or AggregateFn.CountDistinct;
            var type = chart.Fn switch
            {
                AggregateFn.Min or AggregateFn.Max => input.ClrType,
                AggregateFn.Count or AggregateFn.CountDistinct => typeof(long),
                null => input.ClrType,
                _ => typeof(decimal),
            };
            var effectiveLabel = chart.Fn is { } function
                ? $"{ReportResultColumns.AggregateName(function)}({input.EffectiveLabel})"
                : input.EffectiveLabel;
            metric = new BoundColumnContract(
                metricInfo.Name,
                metricInfo.Label,
                effectiveLabel,
                type,
                IsNullable: true,
                IsComputed: metricInfo.Computed,
                new BoundChartColumnLineage("value", input.LogicalId, chart.Fn),
                isCount ? null : input.LocalFormat,
                isCount ? null : input.ExportedMask,
                isCount || input.ExportedMask is null ? null : input.LogicalId);
        }

        return new BoundChartRelation(
            source,
            chart,
            BoundOutputContract.Create(outputName, [label, metric]),
            sourcePath);
    }

    /// <summary>
    /// Derives the public type and label of one grouped metric from its input column and aggregate function.
    /// </summary>
    /// <param name="id">The metric's authored logical identifier.</param>
    /// <param name="input">The validated source column.</param>
    /// <param name="function">The aggregate applied to the source column.</param>
    /// <returns>A column model using the source type for min/max, <c>long</c> for counts, and <c>decimal</c> otherwise.</returns>
    private static ColumnModel MetricColumn(
        string id,
        ColumnModel input,
        AggregateFn function)
        => new()
        {
            Name = id,
            Label = ReportResultColumns.AggregateLabel(new ValidAggregate(input, function)),
            ClrType = function switch
            {
                AggregateFn.Min or AggregateFn.Max => input.ClrType,
                AggregateFn.Count or AggregateFn.CountDistinct => typeof(long),
                _ => typeof(decimal),
            },
        };

    /// <summary>
    /// Binds a pivot, discovers its distinct column keys through a bounded query, and creates the wide bound relation.
    /// </summary>
    /// <param name="source">The relation whose columns supply pivot rows, keys, and metric inputs.</param>
    /// <param name="shape">The canonical pivot declaration to bind.</param>
    /// <param name="tableId">The owning table identifier used in generated output names and pivot-cell identities.</param>
    /// <param name="shapeOrdinal">The one-based shape number across the inherited table chain.</param>
    /// <param name="errors">The validation list that receives binding and width errors.</param>
    /// <param name="ignored">The diagnostics list that receives policy-restricted or unknown columns.</param>
    /// <param name="ct">Cancels pivot-key discovery.</param>
    /// <returns>The resolved wide pivot relation and compiled shape, or the unchanged source and a null shape when initial binding adds errors.</returns>
    /// <remarks>Executes the supplied pivot-reader callback and reserves stable generated column identifiers in the request-scoped registry.</remarks>
    /// <exception cref="ReportValidationException">Thrown when discovery exceeds group, column, generated-width, or binding limits.</exception>
    private async Task<(BoundRelationNode Relation, CompiledShape? Shape)> ApplyPivot(
        BoundRelationNode source,
        CanonicalPivotShape shape,
        string tableId,
        int shapeOrdinal,
        List<ValidationError> errors,
        List<IgnoredItem> ignored,
        CancellationToken ct)
    {
        var path = shape.Path;
        var before = errors.Count;
        var sourceSchema = source.Output.ToReportSchema();
        var rows = ShapeBindingRules.BindDimensions(
            shape.Rows,
            "pivot row",
            sourceSchema.Lookup,
            ignored);
        var columns = ShapeBindingRules.BindDimensions(
            shape.Columns,
            "pivot",
            sourceSchema.Lookup,
            ignored);
        if (rows.Count == 0)
            errors.Add(new ValidationError($"{path}.rows", "a pivot stage requires at least one valid row dimension"));
        if (columns.Count == 0)
            errors.Add(new ValidationError($"{path}.cols", "a pivot stage requires at least one valid column dimension"));
        var rowNames = new HashSet<string>(rows.Select(column => column.Name), StringComparer.OrdinalIgnoreCase);
        var overlap = columns.FirstOrDefault(column => rowNames.Contains(column.Name));
        if (overlap is not null)
            errors.Add(new ValidationError($"{path}.cols", $"pivot column '{overlap.Name}' is already a row dimension"));
        ValidateMetricCount(shape.Values.Length, path, errors);
        var metrics = ShapeBindingRules.BindMetrics(
            shape.Values,
            sourceSchema,
            errors,
            ignored);
        ValidateGroupedWidth(rows.Count + columns.Count, metrics.Count, path, errors);
        ValidateMedianProjectionWidth(rows.Concat(columns), metrics, path, errors);
        if (errors.Count > before) return (source, null);

        var discoveryColumns = (long)rows.Count + columns.Count + 1L + metrics.Count;
        if (discoveryColumns > MaxGeneratedColumns)
            throw Validation(
                path,
                $"pivot discovery would expose {discoveryColumns} columns (max {MaxGeneratedColumns})");

        var pivotCountName = UniqueLogicalName(
            rows.Concat(columns).Select(column => column.Name)
                .Concat(metrics.Select(metric => metric.Id)),
            "__count");
        var grouped = CreateGroupNode(
            source,
            $"{_definition.Name}#{tableId}#pivot-source{shapeOrdinal}",
            rows.Concat(columns).ToList(),
            metrics,
            pivotCountName,
            path);
        var loweredDiscovery = _lowerer.Lower(grouped);
        var totalsNode = shape.Totals
            ? CreateGroupNode(
                source,
                $"{_definition.Name}#{tableId}#pivot-totals{shapeOrdinal}",
                columns,
                metrics,
                pivotCountName,
                path)
            : null;
        var totalsRelation = totalsNode is null
            ? null
            : _lowerer.Lower(totalsNode).AsComposable();
        var groups = await _readPivotGroups(
            loweredDiscovery.Query.Clone().Limit(ReportExecutor.MaxPivotGroups + 1),
            rows.Count,
            columns.Count,
            metrics.Count,
            ct);
        if (groups.Count > ReportExecutor.MaxPivotGroups)
            throw Validation(
                path,
                $"pivot source exceeds {ReportExecutor.MaxPivotGroups} groups — filter further or choose lower-cardinality dimensions");

        var columnKeys = groups
            .Select(group => group.ColumnKey)
            .Distinct(PivotKeyComparer.Instance)
            .OrderBy(key => key, PivotKeyOrdering.Instance)
            .ToList();
        if (columnKeys.Count > _definition.MaxPivotColumns)
            throw Validation(
                $"{path}.cols",
                $"pivot would produce {columnKeys.Count} column groups (max {_definition.MaxPivotColumns}) — filter further or choose a lower-cardinality column dimension");

        var cellFamilies = Math.Max(1L, metrics.Count);
        var generatedColumnCount = (long)rows.Count + (columnKeys.Count * cellFamilies);
        if (generatedColumnCount > MaxGeneratedColumns)
            throw Validation(
                path,
                $"pivot would expose {generatedColumnCount} columns (max {MaxGeneratedColumns})");
        var bindingCount = columnKeys.Sum(key =>
            (long)key.Count(value => value is not null) * cellFamilies);
        if (bindingCount > MaxPivotBindings)
            throw Validation(
                path,
                $"pivot would require {bindingCount} bound cell predicates (max {MaxPivotBindings})");

        var keys = new List<PivotColumnKey>(columnKeys.Count);
        var boundKeys = ImmutableArray.CreateBuilder<BoundResolvedPivotKey>(columnKeys.Count);
        foreach (var key in columnKeys)
        {
            var typedKey = BoundPivotTypedKey.Create(key);
            var cellIds = metrics.Count == 0
                ? ["__count"]
                : metrics.Select(metric => metric.Id).ToArray();
            var keyLabel = string.Join(" · ", key.Select(FormatPivotKeyPart));
            var cells = new List<PivotCellColumn>();
            var boundCells = ImmutableArray.CreateBuilder<BoundPivotCell>(cellIds.Length);
            if (metrics.Count == 0)
            {
                var name = _pivotIdentities.Register(tableId, "__count", typedKey);
                var column = new ColumnModel
                {
                    Name = name,
                    Label = keyLabel,
                    ClrType = typeof(long),
                    IsNullable = true,
                };
                cells.Add(new PivotCellColumn(
                    pivotCountName,
                    column));
                boundCells.Add(new BoundPivotCell(
                    pivotCountName,
                    BoundColumnContract.FromColumn(
                        column,
                        new BoundPivotCellColumnLineage(
                            tableId,
                            "__count",
                            typedKey),
                        keyLabel)));
            }
            else
            {
                foreach (var metric in metrics)
                {
                    var name = _pivotIdentities.Register(tableId, metric.Id, typedKey);
                    var label = metrics.Count == 1
                        ? keyLabel
                        : $"{keyLabel} · {ReportResultColumns.AggregateLabel(metric.ToAggregate())}";
                    var column = new ColumnModel
                    {
                        Name = name,
                        Label = label,
                        ClrType = metric.Fn switch
                        {
                            AggregateFn.Min or AggregateFn.Max => metric.Column.ClrType,
                            AggregateFn.Count or AggregateFn.CountDistinct => typeof(long),
                            _ => typeof(decimal),
                        },
                        IsNullable = true,
                    };
                    cells.Add(new PivotCellColumn(metric.Id, column));
                    var isCount = metric.Fn is AggregateFn.Count or AggregateFn.CountDistinct;
                    var sourceContract = source.Output.GetRequired(metric.Column.Name);
                    var sourceLabel = sourceContract.EffectiveLabel;
                    var aggregateLabel = $"{ReportResultColumns.AggregateName(metric.Fn)}({sourceLabel})";
                    var effectiveLabel = metrics.Count == 1
                        ? keyLabel
                        : $"{keyLabel} · {aggregateLabel}";
                    boundCells.Add(new BoundPivotCell(
                        metric.Id,
                        BoundColumnContract.FromColumn(
                            column,
                            new BoundPivotCellColumnLineage(
                                tableId,
                                metric.Id,
                                typedKey),
                            effectiveLabel,
                            isCount ? null : sourceContract.LocalFormat,
                            isCount ? null : sourceContract.ExportedMask,
                            isCount || sourceContract.ExportedMask is null
                                ? null
                                : sourceContract.LogicalId)));
                }
            }
            keys.Add(new PivotColumnKey(key, cells));
            boundKeys.Add(new BoundResolvedPivotKey(typedKey, boundCells.ToImmutable()));
        }

        var boundRows = rows.Select(row => source.Output.GetRequired(row.Name) with
            {
                Lineage = new BoundPassThroughColumnLineage(row.Name),
            })
            .ToImmutableArray();
        var boundColumns = columns
            .Select(column => source.Output.GetRequired(column.Name))
            .ToImmutableArray();
        var boundMetrics = metrics.Select(metric => new BoundMetric(
                metric.Id,
                source.Output.GetRequired(metric.Column.Name),
                metric.Fn,
                path))
            .ToImmutableArray();
        var output = BoundOutputContract.Create(
            $"{_definition.Name}#{tableId}#pivot{shapeOrdinal}",
            boundRows.Cast<BoundColumnContract>()
                .Concat(boundKeys.SelectMany(pivotKey => pivotKey.Cells)
                    .Select(cell => cell.Output)));
        var wide = new BoundResolvedPivotRelation(
            grouped,
            boundRows,
            boundColumns,
            boundMetrics,
            boundKeys.ToImmutable(),
            output,
            path);
        return (
            wide,
            new CompiledShape(
                ShapeKind.Pivot,
                path,
                Dimensions: rows,
                Metrics: metrics,
                PivotColumns: columns,
                PivotTotals: shape.Totals,
                PivotTotalsRelation: totalsRelation,
                PivotKeys: keys));
    }

    /// <summary>
    /// Resolves a table identifier case-insensitively while preserving the document's canonical key spelling.
    /// </summary>
    /// <param name="requested">The table identifier supplied by the active target or a <c>from</c> edge.</param>
    /// <param name="incomingEdgePath">The source path to report when the table does not exist.</param>
    /// <returns>The stored identifier and its table definition.</returns>
    /// <exception cref="ReportValidationException">Thrown when the document has no matching table.</exception>
    private (string Id, ReportTable Table) ResolveTable(string requested, string incomingEdgePath)
    {
        if (_document.Tables is not { Count: > 0 })
            throw Validation(incomingEdgePath, $"unknown table '{requested}'");
        foreach (var (id, table) in _document.Tables)
            if (string.Equals(id, requested, StringComparison.OrdinalIgnoreCase))
                return (id, table);
        throw Validation(incomingEdgePath, $"unknown table '{requested}'");
    }

    /// <summary>
    /// Creates an empty owner-local renderer layer over an existing relation schema.
    /// </summary>
    /// <param name="schema">The relation schema the empty layer must preserve.</param>
    /// <returns>A local result with no selection, ordering, highlights, breaks, or aggregates.</returns>
    private static BoundLocalResult EmptyLayer(ReportSchema schema)
        => BoundLocalResult.Empty(schema);

    /// <summary>
    /// Rejects request search or post-pivot relation operations that would make a pivot's separately compiled totals inconsistent.
    /// </summary>
    /// <param name="plan">The completed structural plan whose last shape may be a totals-enabled pivot.</param>
    /// <exception cref="ReportValidationException">Thrown when totals precede a later compute, filter, or request search.</exception>
    private void ValidatePivotTotalsCompatibility(CompiledComposableTable plan)
    {
        if (plan.LastShape is not
            {
                Kind: ShapeKind.Pivot,
                PivotTotals: true,
            } pivot)
            return;

        var errors = new List<ValidationError>();
        AddPivotTotalsCompatibilityErrors(
            pivot.Path,
            pivot.HasPostShapeComputed,
            pivot.HasPostShapeFilters,
            !string.IsNullOrWhiteSpace(_document.Search),
            errors);

        if (errors.Count > 0)
            throw new ReportValidationException(errors);
    }

    /// <summary>
    /// Rejects same-table computed columns, effective filters, or active request search before running pivot-key discovery.
    /// </summary>
    /// <param name="specification">The canonical operations declared on the pivot's table.</param>
    /// <param name="pivot">The totals-enabled pivot being prepared.</param>
    /// <param name="source">The pre-pivot output used to prove whether filter references are policy-restricted.</param>
    /// <param name="tableId">The pivot's table identifier, used to determine whether request search targets it.</param>
    /// <exception cref="ReportValidationException">Thrown when totals would be computed from a different relation than the displayed pivot.</exception>
    private void ValidatePivotTotalsBeforeDiscovery(
        CanonicalTableSpec specification,
        CanonicalPivotShape pivot,
        BoundOutputContract source,
        string tableId)
    {
        if (!pivot.Totals) return;

        var hasFilters = !specification.Filters.IsEmpty
            && !AllFiltersAreStaticallyRestricted(specification.Filters, source);
        var hasRequestSearch = string.Equals(
                _document.ActiveTable?.Trim(),
                tableId,
                StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(_document.Search);
        var errors = new List<ValidationError>();
        AddPivotTotalsCompatibilityErrors(
            pivot.Path,
            !specification.Computed.IsEmpty,
            hasFilters,
            hasRequestSearch,
            errors);
        if (errors.Count > 0)
            throw new ReportValidationException(errors);
    }

    /// <summary>
    /// Determines whether every filter contains at least one column that policy makes non-filterable.
    /// Such filters will be ignored by binding and therefore cannot invalidate pre-pivot totals.
    /// </summary>
    /// <param name="filters">The canonical filter expressions to inspect without executing them.</param>
    /// <param name="source">The output contract used to resolve referenced column names.</param>
    /// <returns><see langword="true"/> only when filter restrictions exist and every expression parses and references a restricted column.</returns>
    private bool AllFiltersAreStaticallyRestricted(
        ImmutableArray<CanonicalFilter> filters,
        BoundOutputContract source)
    {
        if (!_policy.HasFilterRestrictions) return false;

        var schema = source.ToReportSchema();
        foreach (var filter in filters)
        {
            var (ast, _) = ExprParser.ParseCondition(filter.Expression, schema.Lookup);
            if (ast is null
                || !ExprColumns.Collect(ast).Any(name =>
                    schema.TryGetValue(name, out var column)
                    && !_policy.IsFilterable(column)))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Appends one actionable diagnostic for each operation that would run after a separately compiled pivot-totals relation.
    /// </summary>
    /// <param name="pivotPath">The pivot composable's source path.</param>
    /// <param name="hasComputed">Whether computed columns would be applied after the pivot source is captured.</param>
    /// <param name="hasFilters">Whether effective filters would be applied after the pivot source is captured.</param>
    /// <param name="hasRequestSearch">Whether the active request adds a search overlay after the pivot source is captured.</param>
    /// <param name="errors">The validation list to append to.</param>
    private static void AddPivotTotalsCompatibilityErrors(
        string pivotPath,
        bool hasComputed,
        bool hasFilters,
        bool hasRequestSearch,
        List<ValidationError> errors)
    {
        var totalsPath = $"{pivotPath}.totals";
        if (hasComputed)
            errors.Add(new ValidationError(
                totalsPath,
                "pivot totals cannot currently be combined with computed columns declared "
                + "on the same table because the totals relation is produced before them; "
                + "disable totals or move the computation to another table"));
        if (hasFilters)
            errors.Add(new ValidationError(
                totalsPath,
                "pivot totals cannot currently be combined with filters declared on the "
                + "same table because the totals relation is produced before them; disable "
                + "totals or move a pre-Pivot filter to the parent table"));
        if (hasRequestSearch)
            errors.Add(new ValidationError(
                totalsPath,
                "pivot totals cannot currently be combined with request search because the "
                + "totals relation is produced before the search overlay; clear search or "
                + "disable totals"));
    }

    /// <summary>
    /// Prevents footer aggregates from emitting the same response key as a pivot-total cell with the same function.
    /// </summary>
    /// <param name="plan">The active compiled plan whose last shape may provide pivot totals.</param>
    /// <param name="terminal">The bound local result containing footer aggregates.</param>
    /// <param name="errors">The validation list that receives response-key collision diagnostics.</param>
    private static void ValidatePivotFooterAggregateCompatibility(
        CompiledComposableTable plan,
        BoundLocalResult terminal,
        List<ValidationError> errors)
    {
        if (plan.LastShape is not
            {
                Kind: ShapeKind.Pivot,
                PivotTotals: true,
                Metrics: { } metrics,
                PivotKeys: { } keys,
            } pivot)
            return;

        var metricFunctions = metrics.ToDictionary(
            metric => metric.Id,
            metric => metric.Fn,
            StringComparer.OrdinalIgnoreCase);
        var shapeFunctions = new Dictionary<string, AggregateFn>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            foreach (var cell in key.Cells)
            {
                if (metricFunctions.TryGetValue(cell.SourceName, out var function))
                    shapeFunctions[cell.Column.Name] = function;
                else if (metrics.Count == 0)
                    shapeFunctions[cell.Column.Name] = AggregateFn.Count;
            }
        }

        foreach (var aggregate in terminal.Aggregates)
        {
            if (!shapeFunctions.TryGetValue(aggregate.Column.Name, out var shapeFunction)
                || shapeFunction != aggregate.Fn)
                continue;

            errors.Add(new ValidationError(
                $"{pivot.Path}.totals",
                $"pivot totals cannot be combined with footer aggregate "
                + $"'{aggregate.Fn.ToString().ToLowerInvariant()}' on generated cell "
                + $"'{aggregate.Column.Name}' because both produce the same response "
                + "aggregate key from different relations; disable pivot totals or "
                + "remove or change the footer aggregate"));
        }
    }

    /// <summary>
    /// Removes a rule collection suffix from a rule path to recover the owning composable path.
    /// </summary>
    /// <param name="rulePath">A rule path such as <c>tables.x.composables[0].aggregates[1]</c>.</param>
    /// <param name="property">The collection property name, without punctuation.</param>
    /// <returns>The path before <c>.{property}[</c>, or the original path when that marker is absent.</returns>
    private static string OwningComposablePath(string rulePath, string property)
    {
        var marker = $".{property}[";
        var markerIndex = rulePath.LastIndexOf(marker, StringComparison.Ordinal);
        return markerIndex < 0 ? rulePath : rulePath[..markerIndex];
    }

    /// <summary>
    /// Reports a shape whose authored metric count exceeds the compiler's bound.
    /// </summary>
    /// <param name="valueCount">The number of metric declarations on the shape.</param>
    /// <param name="path">The shape composable's source path.</param>
    /// <param name="errors">The validation list to append to.</param>
    private static void ValidateMetricCount(
        int valueCount,
        string path,
        List<ValidationError> errors)
    {
        if (valueCount > MaxShapeMetrics)
            errors.Add(new ValidationError(
                $"{path}.values",
                $"a shape may contain at most {MaxShapeMetrics} metrics"));
    }

    /// <summary>
    /// Reports a group or pivot-discovery projection that would exceed the generated-column bound.
    /// </summary>
    /// <param name="dimensionCount">The number of grouping dimensions projected unchanged.</param>
    /// <param name="metricCount">The number of aggregate metrics projected alongside the generated count.</param>
    /// <param name="path">The declaration path to use for the diagnostic.</param>
    /// <param name="errors">The validation list to append to.</param>
    private void ValidateGroupedWidth(
        int dimensionCount,
        int metricCount,
        string path,
        List<ValidationError> errors)
    {
        var outputCount = (long)dimensionCount + 1L + metricCount;
        if (outputCount > MaxGeneratedColumns)
            errors.Add(new ValidationError(
                path,
                $"a grouped relation may expose at most {MaxGeneratedColumns} columns"));
    }

    /// <summary>
    /// Validates footer-aggregate, control-break, and median helper projections before terminal SQL is built.
    /// </summary>
    /// <param name="plan">The compiled table used to recover exact declaration paths.</param>
    /// <param name="terminal">The bound local result containing break columns and footer aggregates.</param>
    /// <param name="errors">The validation list that receives width violations.</param>
    private void ValidateTerminalWidths(
        CompiledComposableTable plan,
        BoundLocalResult terminal,
        List<ValidationError> errors)
    {
        var local = plan.Local.Instructions;
        var breakPath = local.Breaks?.SourcePath;
        var aggregatePath = local.Aggregates.IsEmpty
            ? null
            : local.Aggregates
                .Select(aggregate => OwningComposablePath(
                    aggregate.SourcePath,
                    "aggregates"))
                .OrderBy(path => path, StringComparer.Ordinal)
                .First();
        if (terminal.Aggregates.Length > MaxGeneratedColumns)
            errors.Add(new ValidationError(
                aggregatePath is null ? "tables" : $"{aggregatePath}.aggregates",
                $"terminal aggregates may expose at most {MaxGeneratedColumns} values"));
        var breakOutputCount = (long)terminal.Breaks.Length + 1L + terminal.Aggregates.Length;
        if (terminal.Breaks.Length > 0 && breakOutputCount > MaxGeneratedColumns)
            errors.Add(new ValidationError(
                aggregatePath ?? breakPath ?? "tables",
                $"break totals may expose at most {MaxGeneratedColumns} columns"));
        if (terminal.Aggregates.Length > 0)
            ValidateMedianProjectionWidth(
                terminal.Breaks,
                terminal.Aggregates.Select(aggregate =>
                    (aggregate.Column, aggregate.Fn)),
                aggregatePath ?? breakPath ?? "tables",
                errors);
    }

    /// <summary>
    /// Validates the hidden ranking columns required by median metrics in a group or pivot.
    /// </summary>
    /// <param name="dimensions">The grouping columns that must be repeated in the ranked projection.</param>
    /// <param name="metrics">The validated metrics whose median functions require two helper columns each.</param>
    /// <param name="path">The declaration path to use for a width diagnostic.</param>
    /// <param name="errors">The validation list to append to.</param>
    private static void ValidateMedianProjectionWidth(
        IEnumerable<ColumnModel> dimensions,
        IEnumerable<ValidMetric> metrics,
        string path,
        List<ValidationError> errors)
        => ValidateMedianProjectionWidth(
            dimensions,
            metrics.Select(metric => (metric.Column, metric.Fn)),
            path,
            errors);

    /// <summary>
    /// Counts distinct projected inputs and two ranking helpers per median, then reports an excessive projection.
    /// </summary>
    /// <param name="dimensions">The grouping columns included in the ranked projection.</param>
    /// <param name="metrics">Source-column and aggregate-function pairs included in the projection.</param>
    /// <param name="path">The declaration path to use for a width diagnostic.</param>
    /// <param name="errors">The validation list to append to.</param>
    private static void ValidateMedianProjectionWidth(
        IEnumerable<ColumnModel> dimensions,
        IEnumerable<(ColumnModel Column, AggregateFn Fn)> metrics,
        string path,
        List<ValidationError> errors)
    {
        var metricList = metrics.ToList();
        var medianCount = metricList.Count(metric => metric.Fn == AggregateFn.Median);
        if (medianCount == 0) return;
        var inputCount = dimensions
            .Concat(metricList.Select(metric => metric.Column))
            .Select(column => column.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .LongCount();
        var rankedProjectionCount = inputCount + (2L * medianCount);
        if (rankedProjectionCount > MaxGeneratedColumns)
            errors.Add(new ValidationError(
                path,
                $"median ranking may expose at most {MaxGeneratedColumns} helper columns"));
    }

    /// <summary>
    /// Prefixes underscores until a generated logical identifier is unique under case-insensitive comparison.
    /// </summary>
    /// <param name="existing">Logical identifiers already present in the output.</param>
    /// <param name="candidate">The preferred generated identifier.</param>
    /// <returns>The candidate itself when available, otherwise the first underscore-prefixed variant not in <paramref name="existing"/>.</returns>
    private static string UniqueLogicalName(IEnumerable<string> existing, string candidate)
    {
        var used = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        while (!used.Add(candidate)) candidate = $"_{candidate}";
        return candidate;
    }

    /// <summary>
    /// Enumerates all authored identifiers that dynamic pivot columns must not claim.
    /// </summary>
    /// <param name="document">The report document containing computed-column and metric identifiers.</param>
    /// <param name="definitionSchema">The base schema containing authoritative column names.</param>
    /// <returns>Base column names followed by non-empty computed and metric identifiers in document order.</returns>
    private static IEnumerable<string> ReservedLogicalIds(
        ReportState document,
        ReportSchema definitionSchema)
    {
        foreach (var column in definitionSchema.Columns)
            yield return column.Name;

        foreach (var table in document.Tables?.Values ?? Enumerable.Empty<ReportTable>())
        foreach (var composable in table?.Composables ?? [])
        {
            foreach (var computed in composable?.Computed ?? [])
                if (!string.IsNullOrWhiteSpace(computed?.Id))
                    yield return computed.Id.Trim();
            foreach (var metric in composable?.Values ?? [])
                if (!string.IsNullOrWhiteSpace(metric?.Id))
                    yield return metric.Id.Trim();
        }
    }

    /// <summary>
    /// Enforces dialect-specific relation nesting and cross-provider output-width limits.
    /// </summary>
    /// <param name="path">The operation path to use for a validation error.</param>
    /// <param name="relation">The lowered relation whose nesting depth and schema width will be checked.</param>
    /// <exception cref="ReportValidationException">Thrown when either complexity bound is exceeded.</exception>
    private void EnsureRelationComplexity(
        string path,
        ComposableSqlRelation relation)
    {
        var stageLimit = _definition.GetEffectiveDialect() == ReportDialect.SqlServer
            ? MaxSqlServerRelationStages
            : MaxRelationStages;
        if (relation.NestingDepth > stageLimit)
            throw Validation(path, $"relational composition may contain at most {stageLimit} SQL stages for this dialect");
        if (relation.Schema.Count > MaxGeneratedColumns)
            throw Validation(path, $"a composed table may expose at most {MaxGeneratedColumns} columns");
    }

    /// <summary>
    /// Creates a single-error report validation exception for an exact document path.
    /// </summary>
    /// <param name="path">The invalid document location.</param>
    /// <param name="message">The user-facing validation message.</param>
    /// <returns>An exception containing one <see cref="ValidationError"/>.</returns>
    private static ReportValidationException Validation(string path, string message)
        => new([new ValidationError(path, message)]);

    private static readonly JsonSerializerOptions PivotKeyJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Serializes a provider-valued pivot key into the stable public identity used for generated columns.
    /// </summary>
    /// <param name="key">The ordered pivot-dimension values.</param>
    /// <returns>A JSON array whose values use invariant, type-appropriate representations.</returns>
    internal static string PivotKeyName(object?[] key)
        => JsonSerializer.Serialize(
            key.Select(CanonicalPivotKeyPart),
            PivotKeyJson);

    /// <summary>
    /// Compares two pivot keys through the typed identity used by dynamic-column allocation.
    /// </summary>
    /// <param name="left">The first ordered provider-value array.</param>
    /// <param name="right">The second ordered provider-value array.</param>
    /// <returns><see langword="true"/> when both arrays have the same typed pivot identity; otherwise, <see langword="false"/>.</returns>
    internal static bool PivotKeysEqual(object?[] left, object?[] right)
        => PivotKeyComparer.Instance.Equals(left, right);

    /// <summary>
    /// Converts one provider value to the invariant scalar representation embedded in a public pivot-key name.
    /// </summary>
    /// <param name="value">The provider value to encode.</param>
    /// <returns>An invariant string, base-64 byte representation, or <see langword="null"/> for a database null.</returns>
    private static string? CanonicalPivotKeyPart(object? value)
        => value switch
        {
            null => null,
            DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset offset => offset.ToString("O", CultureInfo.InvariantCulture),
            DateOnly dateOnly => dateOnly.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            TimeOnly timeOnly => timeOnly.ToString("O", CultureInfo.InvariantCulture),
            double number => number.ToString("R", CultureInfo.InvariantCulture),
            float number => number.ToString("R", CultureInfo.InvariantCulture),
            decimal number => number.ToString("G29", CultureInfo.InvariantCulture),
            Guid guid => guid.ToString("D"),
            byte[] bytes => Convert.ToBase64String(bytes),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };

    /// <summary>
    /// Formats one pivot-key value for a human-readable generated column label.
    /// </summary>
    /// <param name="value">The provider value to display.</param>
    /// <returns><c>(blank)</c> for null; otherwise, the invariant string representation or an empty fallback.</returns>
    private static string FormatPivotKeyPart(object? value)
        => value is null ? "(blank)" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";

    /// <summary>Compares provider-value arrays through the pivot registry's typed-key identity.</summary>
    private sealed class PivotKeyComparer : IEqualityComparer<object?[]>
    {
        public static readonly PivotKeyComparer Instance = new();

        /// <summary>
        /// Determines whether two non-null arrays produce the same typed pivot key.
        /// </summary>
        /// <param name="left">The first pivot-key value array.</param>
        /// <param name="right">The second pivot-key value array.</param>
        /// <returns><see langword="true"/> when both non-null arrays have equal typed identities; otherwise, <see langword="false"/>.</returns>
        public bool Equals(object?[]? left, object?[]? right)
        {
            if (left is null || right is null || left.Length != right.Length) return false;
            return BoundPivotTypedKey.Create(left).Equals(BoundPivotTypedKey.Create(right));
        }

        /// <summary>
        /// Returns the typed pivot identity's hash code.
        /// </summary>
        /// <param name="key">The non-null provider-value array to hash.</param>
        /// <returns>A hash code consistent with this comparer's typed-key equality.</returns>
        public int GetHashCode(object?[] key)
            => BoundPivotTypedKey.Create(key).GetHashCode();
    }

    /// <summary>Provides a deterministic order for discovered pivot keys independent of provider row order.</summary>
    private sealed class PivotKeyOrdering : IComparer<object?[]>
    {
        public static readonly PivotKeyOrdering Instance = new();

        /// <summary>
        /// Compares arrays lexicographically by provider value, with null arrays first and array length as the final tie-breaker.
        /// </summary>
        /// <param name="left">The first pivot-key array.</param>
        /// <param name="right">The second pivot-key array.</param>
        /// <returns>A negative value when <paramref name="left"/> sorts first, zero when equivalent for ordering, or a positive value otherwise.</returns>
        public int Compare(object?[]? left, object?[]? right)
        {
            if (left is null || right is null) return (left is null).CompareTo(right is null);
            for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
            {
                var comparison = ComparePart(left[index], right[index]);
                if (comparison != 0) return comparison;
            }
            return left.Length.CompareTo(right.Length);
        }

        /// <summary>
        /// Compares one key position using null, ordinal string, lexicographic byte, native comparable,
        /// invariant-text, and runtime-type ordering in that sequence.
        /// </summary>
        /// <param name="left">The first provider value.</param>
        /// <param name="right">The second provider value.</param>
        /// <returns>A signed sort comparison with null values ordered first.</returns>
        private static int ComparePart(object? left, object? right)
        {
            if (left is null || right is null) return (left is not null).CompareTo(right is not null);
            if (left is string leftText && right is string rightText)
                return StringComparer.Ordinal.Compare(leftText, rightText);
            if (left is byte[] leftBytes && right is byte[] rightBytes)
            {
                for (var index = 0; index < Math.Min(leftBytes.Length, rightBytes.Length); index++)
                {
                    var comparison = leftBytes[index].CompareTo(rightBytes[index]);
                    if (comparison != 0) return comparison;
                }
                return leftBytes.Length.CompareTo(rightBytes.Length);
            }
            if (left.GetType() == right.GetType() && left is IComparable comparable)
                return comparable.CompareTo(right);
            var text = string.CompareOrdinal(
                Convert.ToString(left, CultureInfo.InvariantCulture),
                Convert.ToString(right, CultureInfo.InvariantCulture));
            if (text != 0) return text;

            // Distinct typed values can intentionally share one serialized public
            // key (for example numeric 1 and text "1"). Keep their ~2/~3 suffix
            // ownership stable even when the provider returns grouped rows in a
            // different order.
            return string.CompareOrdinal(
                left.GetType().FullName,
                right.GetType().FullName);
        }
    }
}

/// <summary>
/// The only state a child table may consume. It contains the completed relational
/// contract and inherited structural metadata, but no owner-local presentation.
/// </summary>
/// <param name="Bound">The immutable bound relation and output contract exported to descendants.</param>
/// <param name="ShapeCount">The number of structural shapes applied across the ancestry.</param>
/// <param name="RelationStages">The lowered relation depth recorded at compilation.</param>
/// <param name="ComputedRuleCount">The cumulative number of authored computed-column rules.</param>
/// <param name="FilterRuleCount">The cumulative number of authored filter rules.</param>
internal sealed record CompiledTableExport(
    BoundTableExport Bound,
    int ShapeCount,
    int RelationStages,
    int ComputedRuleCount,
    int FilterRuleCount)
{
    /// <summary>Gets exported scalar format metadata keyed by logical column identifier.</summary>
    public IReadOnlyDictionary<string, ColumnFormat> Formats
        => BoundContractMaps.Formats(Bound.Output);
}

/// <summary>
/// Instructions and renderer state owned by one named table. This value is reset at
/// every <c>from</c> edge and is never part of a child's imported relation.
/// </summary>
/// <param name="ExecutionNode">The request-specific bound relation, or null before target completion.</param>
/// <param name="ExecutionRelation">The lowered request-specific relation, or null before target completion.</param>
/// <param name="ExecutionBundle">Terminal data and aggregate queries, or null before target completion.</param>
/// <param name="RequestOverlay">Bound paging and search inputs, or null before target completion.</param>
/// <param name="Shape">The last shape declared by this table, if any.</param>
/// <param name="Terminal">The bound renderer instructions owned by this table.</param>
/// <param name="Instructions">The canonical local declarations awaiting or used for terminal binding.</param>
internal sealed record CompiledTableLocalResult(
    BoundRelationNode? ExecutionNode,
    ComposableSqlRelation? ExecutionRelation,
    TerminalExecutionBundle? ExecutionBundle,
    BoundRequestOverlay? RequestOverlay,
    CompiledShape? Shape,
    BoundLocalResult Terminal,
    CanonicalLocalResult Instructions);

/// <summary>
/// Combines the child-safe structural export with owner-local state and non-fatal binding diagnostics.
/// Before SQL execution, <see cref="ComposableTableCompiler.CompleteForTarget"/> supplies the request-specific fields.
/// </summary>
/// <param name="Export">The relational contract descendants are allowed to consume.</param>
/// <param name="Local">The declaring table's request and renderer state.</param>
/// <param name="SearchApplied">Whether request search was added to the active execution relation.</param>
/// <param name="Ignored">Non-fatal diagnostics for unknown or policy-restricted declarations.</param>
internal sealed record CompiledComposableTable(
    CompiledTableExport Export,
    CompiledTableLocalResult Local,
    bool SearchApplied,
    IReadOnlyList<IgnoredItem> Ignored)
{
    // Read-only forwarding properties keep lowering/execution call sites compact
    // without weakening the explicit Export/Local inheritance boundary above.
    /// <summary>
    /// The relation executed for the active request. Request overlays may replace it
    /// locally, while <see cref="Export"/> remains the only relation a child can consume.
    /// </summary>
    public ComposableSqlRelation Relation
        => Local.ExecutionRelation
            ?? throw new InvalidOperationException(
                "The table must be completed for an active target before SQL execution.");
    /// <summary>Gets the number of group, pivot, or chart shapes in the inherited relation.</summary>
    public int ShapeCount => Export.ShapeCount;
    /// <summary>Gets the relation depth recorded when the export was compiled.</summary>
    public int RelationStages => Export.RelationStages;
    /// <summary>Gets the cumulative number of authored computed-column rules.</summary>
    public int ComputedRuleCount => Export.ComputedRuleCount;
    /// <summary>Gets the cumulative number of authored filter rules.</summary>
    public int FilterRuleCount => Export.FilterRuleCount;
    /// <summary>Gets effective labels keyed case-insensitively by logical output identifier.</summary>
    public IReadOnlyDictionary<string, string> Labels
        => BoundContractMaps.Labels(Export.Bound.Relation.Output);
    /// <summary>Gets full effective formats declared on output columns owned by the active table.</summary>
    public IReadOnlyDictionary<string, ColumnFormat> Formats
        => BoundContractMaps.Formats(Export.Bound.Relation.Output);
    /// <summary>Gets each output column's scalar format-lineage source, when one is inherited.</summary>
    public IReadOnlyDictionary<string, string?> FormatSources
        => BoundContractMaps.FormatSources(Export.Bound.Relation.Output);
    /// <summary>Gets the last shape declared on this table, excluding inherited parent shapes.</summary>
    public CompiledShape? LastShape => Local.Shape;
    /// <summary>Gets the active table's bound selection, ordering, highlighting, breaks, and footer aggregates.</summary>
    public BoundLocalResult Terminal => Local.Terminal;
    /// <summary>Gets the terminal query bundle produced during target completion.</summary>
    /// <exception cref="InvalidOperationException">Thrown before the table has been completed for an active target.</exception>
    public TerminalExecutionBundle ExecutionBundle
        => Local.ExecutionBundle
            ?? throw new InvalidOperationException(
                "The table must be completed for an active target before execution.");
    /// <summary>Gets the paging and search overlay bound during target completion.</summary>
    /// <exception cref="InvalidOperationException">Thrown before the table has been completed for an active target.</exception>
    public BoundRequestOverlay RequestOverlay
        => Local.RequestOverlay
            ?? throw new InvalidOperationException(
                "The table must be completed for an active target before execution.");
}

/// <summary>
/// Projects immutable bound-column metadata into the dictionaries and mutable protocol models used by renderers.
/// </summary>
internal static class BoundContractMaps
{
    /// <summary>
    /// Builds a case-insensitive map of logical output identifiers to effective labels.
    /// </summary>
    /// <param name="contract">The bound output whose columns supply identifiers and labels.</param>
    /// <returns>An immutable label map containing every output column.</returns>
    public static IReadOnlyDictionary<string, string> Labels(BoundOutputContract contract)
        => contract.Columns.ToImmutableDictionary(
            column => column.LogicalId,
            column => column.EffectiveLabel,
            StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Builds a case-insensitive map of formats declared directly on output columns.
    /// </summary>
    /// <param name="contract">The bound output whose local formats will be projected.</param>
    /// <returns>An immutable map containing only columns with a non-null local format.</returns>
    public static IReadOnlyDictionary<string, ColumnFormat> Formats(BoundOutputContract contract)
    {
        var result = ImmutableDictionary.CreateBuilder<string, ColumnFormat>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var column in contract.Columns)
        {
            if (column.LocalFormat is null) continue;
            // A format belongs only to the output column that declares it. Scalar
            // mask lineage is carried separately by FormatSourceLogicalId; copying a
            // derived column's renderer back onto that source can overwrite a real
            // output column that happens to share the lineage id.
            result[column.LogicalId] = ToColumnFormat(column.LocalFormat);
        }
        return result.ToImmutable();
    }

    /// <summary>
    /// Builds a case-insensitive map from each output identifier to its inherited scalar-format source.
    /// </summary>
    /// <param name="contract">The bound output whose format lineage will be projected.</param>
    /// <returns>An immutable map containing every output identifier and a possibly null source identifier.</returns>
    public static IReadOnlyDictionary<string, string?> FormatSources(BoundOutputContract contract)
        => contract.Columns.ToImmutableDictionary(
            column => column.LogicalId,
            column => column.FormatSourceLogicalId,
            StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Copies an immutable canonical format into the mutable model serialized to clients.
    /// </summary>
    /// <param name="value">The canonical format to copy.</param>
    /// <returns>A new protocol format, including a mutable class list when classes are present.</returns>
    private static ColumnFormat ToColumnFormat(CanonicalColumnFormat value)
        => new()
        {
            Mask = value.Mask,
            Align = value.Align,
            Bold = value.Bold,
            Italic = value.Italic,
            Fg = value.Foreground,
            Bg = value.Background,
            Classes = value.Classes.IsDefaultOrEmpty ? null : value.Classes.ToList(),
            DisplayAs = value.DisplayAs,
            UrlColumn = value.UrlColumn,
            TextColumn = value.TextColumn,
            Command = value.Command,
            KeyColumn = value.KeyColumn,
        };
}

/// <summary>
/// Captures the bound renderer metadata for the most recent group, pivot, or chart shape declared by a table.
/// </summary>
/// <param name="Kind">The shape family.</param>
/// <param name="Path">The source path of the shape composable.</param>
/// <param name="Dimensions">Bound group dimensions or pivot row dimensions.</param>
/// <param name="Metrics">Bound aggregate metrics, if the shape defines them.</param>
/// <param name="CountName">The collision-free count identifier generated for group or pivot aggregation.</param>
/// <param name="PivotColumns">The bound dimensions whose values become pivot column groups.</param>
/// <param name="PivotTotals">Whether the response should include the separately compiled pivot totals relation.</param>
/// <param name="Chart">The validated chart roles and display options.</param>
/// <param name="PivotTotalsRelation">The relation used to compute totals by pivot column key.</param>
/// <param name="PivotKeys">The discovered keys and generated cell columns.</param>
/// <param name="HasPostShapeComputed">Whether the owning table adds computed columns after the pivot.</param>
/// <param name="HasPostShapeFilters">Whether the owning table adds effective filters after the pivot.</param>
internal sealed record CompiledShape(
    ShapeKind Kind,
    string Path,
    IReadOnlyList<ColumnModel>? Dimensions = null,
    IReadOnlyList<ValidMetric>? Metrics = null,
    string? CountName = null,
    IReadOnlyList<ColumnModel>? PivotColumns = null,
    bool PivotTotals = false,
    ValidChart? Chart = null,
    ComposableSqlRelation? PivotTotalsRelation = null,
    IReadOnlyList<PivotColumnKey>? PivotKeys = null,
    bool HasPostShapeComputed = false,
    bool HasPostShapeFilters = false);

/// <summary>Identifies the structural shape applied by a composable table.</summary>
internal enum ShapeKind
{
    /// <summary>Groups rows by dimensions and produces count and metric columns.</summary>
    Group,
    /// <summary>Turns distinct dimension values into dynamically generated metric columns.</summary>
    Pivot,
    /// <summary>Projects data into the chart renderer's label and value roles.</summary>
    Chart,
}
