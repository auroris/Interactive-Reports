using System.Collections.Immutable;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using InteractiveReport.Core.Composition;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Planning;
using InteractiveReport.Core.Schema;
using InteractiveReport.Core.Validation;
using SqlKata;

namespace InteractiveReport.Core.Execution;

/// <summary>
/// Compiles a named-table graph parent first. A table's <c>from</c> consumes the
/// parent's completed relation and schema; terminal presentation never leaks into a
/// child. SQL-backed shapes remain relations, including Pivot's data-derived wide
/// output, so any later compositor can bind and compose normally.
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

    /// <summary>Completed plans are memoized by table id for shared ancestors.</summary>
    public IReadOnlyDictionary<string, CompiledComposableTable> Completed => _memo;

    public async Task<CompiledComposableTable> Compile(string tableId, CancellationToken ct)
    {
        var requested = tableId.Trim();
        if (string.Equals(requested, "definition", StringComparison.OrdinalIgnoreCase))
            return Definition();
        return await CompileTable(requested, depth: 1, "activeTable", ct);
    }

    /// <summary>
    /// Applies the request-local search overlay to the completed active relation. It is
    /// deliberately absent from memoized exports, so a child table never inherits a
    /// presentation request made against its parent.
    /// </summary>
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
                isCount
                    ? null
                    : input.FormatSourceLogicalId ?? input.LogicalId);
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
                isCount ? null : input.FormatSourceLogicalId ?? input.LogicalId);
        }

        return new BoundChartRelation(
            source,
            chart,
            BoundOutputContract.Create(outputName, [label, metric]),
            sourcePath);
    }

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
                            isCount
                                ? null
                                : sourceContract.FormatSourceLogicalId
                                    ?? sourceContract.LogicalId)));
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

    private (string Id, ReportTable Table) ResolveTable(string requested, string incomingEdgePath)
    {
        if (_document.Tables is not { Count: > 0 })
            throw Validation(incomingEdgePath, $"unknown table '{requested}'");
        foreach (var (id, table) in _document.Tables)
            if (string.Equals(id, requested, StringComparison.OrdinalIgnoreCase))
                return (id, table);
        throw Validation(incomingEdgePath, $"unknown table '{requested}'");
    }

    private static BoundLocalResult EmptyLayer(ReportSchema schema)
        => BoundLocalResult.Empty(schema);

    private void ValidatePivotTotalsCompatibility(CompiledComposableTable plan)
    {
        if (plan.LastShape is not
            {
                Kind: ShapeKind.Pivot,
                PivotTotals: true,
            } pivot)
            return;

        var errors = new List<ValidationError>();
        var totalsPath = $"{pivot.Path}.totals";
        if (pivot.HasPostShapeComputed)
            errors.Add(new ValidationError(
                totalsPath,
                "pivot totals cannot currently be combined with computed columns declared "
                + "on the same table because the totals relation is produced before them; "
                + "disable totals or move the computation to another table"));
        if (pivot.HasPostShapeFilters)
            errors.Add(new ValidationError(
                totalsPath,
                "pivot totals cannot currently be combined with filters declared on the "
                + "same table because the totals relation is produced before them; disable "
                + "totals or move a pre-Pivot filter to the parent table"));
        if (!string.IsNullOrWhiteSpace(_document.Search))
            errors.Add(new ValidationError(
                totalsPath,
                "pivot totals cannot currently be combined with request search because the "
                + "totals relation is produced before the search overlay; clear search or "
                + "disable totals"));

        if (errors.Count > 0)
            throw new ReportValidationException(errors);
    }

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
                else if (metrics.Count == 0
                         && string.Equals(
                             cell.SourceName,
                             "__count",
                             StringComparison.OrdinalIgnoreCase))
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

    private static string OwningComposablePath(string rulePath, string property)
    {
        var marker = $".{property}[";
        var markerIndex = rulePath.LastIndexOf(marker, StringComparison.Ordinal);
        return markerIndex < 0 ? rulePath : rulePath[..markerIndex];
    }

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

    private static string UniqueLogicalName(IEnumerable<string> existing, string candidate)
    {
        var used = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        while (!used.Add(candidate)) candidate = $"_{candidate}";
        return candidate;
    }

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

    private static ReportValidationException Validation(string path, string message)
        => new([new ValidationError(path, message)]);

    private static readonly JsonSerializerOptions PivotKeyJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal static string PivotKeyName(object?[] key)
        => JsonSerializer.Serialize(
            key.Select(CanonicalPivotKeyPart),
            PivotKeyJson);

    internal static bool PivotKeysEqual(object?[] left, object?[] right)
        => PivotKeyComparer.Instance.Equals(left, right);

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

    private static string FormatPivotKeyPart(object? value)
        => value is null ? "(blank)" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";

    private sealed class PivotKeyComparer : IEqualityComparer<object?[]>
    {
        public static readonly PivotKeyComparer Instance = new();

        public bool Equals(object?[]? left, object?[]? right)
        {
            if (left is null || right is null || left.Length != right.Length) return false;
            return BoundPivotTypedKey.Create(left).Equals(BoundPivotTypedKey.Create(right));
        }

        public int GetHashCode(object?[] key)
            => BoundPivotTypedKey.Create(key).GetHashCode();
    }

    private sealed class PivotKeyOrdering : IComparer<object?[]>
    {
        public static readonly PivotKeyOrdering Instance = new();

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
internal sealed record CompiledTableExport(
    BoundTableExport Bound,
    int ShapeCount,
    int RelationStages,
    int ComputedRuleCount,
    int FilterRuleCount)
{
    /// <summary>Inherited scalar metadata; every value contains only Mask.</summary>
    public IReadOnlyDictionary<string, ColumnFormat> Formats
        => BoundContractMaps.Formats(Bound.Output);
}

/// <summary>
/// Instructions and renderer state owned by one named table. This value is reset at
/// every <c>from</c> edge and is never part of a child's imported relation.
/// </summary>
internal sealed record CompiledTableLocalResult(
    BoundRelationNode? ExecutionNode,
    ComposableSqlRelation? ExecutionRelation,
    TerminalExecutionBundle? ExecutionBundle,
    BoundRequestOverlay? RequestOverlay,
    CompiledShape? Shape,
    BoundLocalResult Terminal,
    CanonicalLocalResult Instructions);

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
    public int ShapeCount => Export.ShapeCount;
    public int RelationStages => Export.RelationStages;
    public int ComputedRuleCount => Export.ComputedRuleCount;
    public int FilterRuleCount => Export.FilterRuleCount;
    public IReadOnlyDictionary<string, string> Labels
        => BoundContractMaps.Labels(Export.Bound.Relation.Output);
    /// <summary>Full effective formats owned by the active table.</summary>
    public IReadOnlyDictionary<string, ColumnFormat> Formats
        => BoundContractMaps.Formats(Export.Bound.Relation.Output);
    public IReadOnlyDictionary<string, string?> FormatSources
        => BoundContractMaps.FormatSources(Export.Bound.Relation.Output);
    public CompiledShape? LastShape => Local.Shape;
    public BoundLocalResult Terminal => Local.Terminal;
    public TerminalExecutionBundle ExecutionBundle
        => Local.ExecutionBundle
            ?? throw new InvalidOperationException(
                "The table must be completed for an active target before execution.");
    public BoundRequestOverlay RequestOverlay
        => Local.RequestOverlay
            ?? throw new InvalidOperationException(
                "The table must be completed for an active target before execution.");
}

internal static class BoundContractMaps
{
    public static IReadOnlyDictionary<string, string> Labels(BoundOutputContract contract)
        => contract.Columns.ToImmutableDictionary(
            column => column.LogicalId,
            column => column.EffectiveLabel,
            StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyDictionary<string, ColumnFormat> Formats(BoundOutputContract contract)
    {
        var result = ImmutableDictionary.CreateBuilder<string, ColumnFormat>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var column in contract.Columns)
        {
            if (column.LocalFormat is null) continue;
            var format = ToColumnFormat(column.LocalFormat);
            result[column.LogicalId] = format;
            if (!string.IsNullOrWhiteSpace(column.FormatSourceLogicalId))
                result[column.FormatSourceLogicalId] = ToColumnFormat(column.LocalFormat);
        }
        return result.ToImmutable();
    }

    public static IReadOnlyDictionary<string, string?> FormatSources(BoundOutputContract contract)
        => contract.Columns.ToImmutableDictionary(
            column => column.LogicalId,
            column => column.FormatSourceLogicalId,
            StringComparer.OrdinalIgnoreCase);

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

internal enum ShapeKind
{
    Group,
    Pivot,
    Chart,
}
