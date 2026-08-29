using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using InteractiveReport.Core.Composition;
using InteractiveReport.Core.Model;
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
    /// Applies the request search overlay at the only remaining legal point. Plans
    /// containing a shape already applied it immediately before their first shape.
    /// </summary>
    public CompiledComposableTable CompleteForTarget(CompiledComposableTable plan)
    {
        if (!plan.SearchApplied && !string.IsNullOrWhiteSpace(_document.Search))
        {
            plan = plan with
            {
                Relation = ComposableSqlPlanner.ApplySearch(plan.Relation, _document.Search),
                SearchApplied = true,
            };
        }
        EnsureRelationComplexity("tables", plan.Relation);

        var errors = new List<ValidationError>();
        var ignored = plan.Ignored.ToList();
        var terminal = TableLayerValidator.Validate(
            plan.TerminalItems,
            $"{plan.Relation.SchemaName}#terminal",
            plan.Relation.Schema,
            _policy,
            errors,
            ignored);
        ValidateTerminalWidths(plan, terminal, errors);
        var projection = StateValidator.ResolveRendererColumns(
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
            StateValidator.AddEditLinkColumns(
                _definition.EditLink,
                projection,
                plan.Relation.Schema,
                ignored);
        terminal = terminal with
        {
            Labels = plan.Labels,
            Formats = plan.Formats,
            ProjectionColumns = projection,
        };
        if (errors.Count > 0) throw new ReportValidationException(errors);
        return plan with { Terminal = terminal, Ignored = ignored };
    }

    private CompiledComposableTable Definition()
        => new(
            ComposableSqlRelation.Definition(_definition, _definitionSchema),
            SearchApplied: false,
            ShapeCount: 0,
            RelationStages: 0,
            ComputedRuleCount: 0,
            FilterRuleCount: 0,
            LastShape: null,
            Terminal: EmptyLayer(_definitionSchema),
            Labels: new Dictionary<string, string>(
                StateValidator.ResolveLabels(_definition.GetEffectiveColumnLabels()),
                StringComparer.OrdinalIgnoreCase),
            Formats: new Dictionary<string, ColumnFormat>(StringComparer.OrdinalIgnoreCase),
            FormatSources: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase),
            TerminalItems: [],
            Ignored: []);

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
            var relation = parent.Relation;
            var searchApplied = parent.SearchApplied;
            var shapeCount = parent.ShapeCount;
            var computedCount = parent.ComputedRuleCount;
            var filterCount = parent.FilterRuleCount;
            // A child consumes the parent's relation, schema, and metadata. Renderer
            // hints are terminal state owned by the named table that declared them.
            CompiledShape? lastShape = null;
            var ignored = parent.Ignored.ToList();
            var errors = new List<ValidationError>();
            var terminalItems = new List<LocatedTableComposable>();
            var labels = new Dictionary<string, string>(parent.Labels, StringComparer.OrdinalIgnoreCase);
            var formats = new Dictionary<string, ColumnFormat>(parent.Formats, StringComparer.OrdinalIgnoreCase);
            var formatSources = new Dictionary<string, string?>(parent.FormatSources, StringComparer.OrdinalIgnoreCase);
            var localLabelsSeen = false;

            var composables = table.Composables ?? [];
            for (var index = 0; index < composables.Count; index++)
            {
                var composable = composables[index];
                var path = $"tables.{tableId}.composables[{index}]";
                var located = new LocatedTableComposable(composable, path);
                var kind = (composable.Kind ?? "").Trim().ToLowerInvariant();
                if (kind.Length > 0) composable.Kind = kind;
                switch (kind)
                {
                    case "compute":
                    case "filter":
                    {
                        computedCount += kind == "compute" ? composable.Computed?.Count ?? 0 : 0;
                        filterCount += kind == "filter" ? composable.Filters?.Count ?? 0 : 0;
                        if (computedCount > 20)
                            errors.Add(new ValidationError(
                                $"{path}.computed",
                                "at most 20 computed columns per report state"));
                        if (filterCount > 50)
                            errors.Add(new ValidationError(
                                $"{path}.filters",
                                "at most 50 filter rules per report state"));

                        var layer = TableLayerValidator.Validate(
                            [located],
                            $"{_definition.Name}#{tableId}",
                            relation.Schema,
                            _policy,
                            errors,
                            ignored);
                        if (layer.Operations.Count > 0)
                        {
                            relation = ComposableSqlPlanner.ApplyOperations(
                                relation,
                                layer.Operations,
                                _definition.GetEffectiveDialect(),
                                _evaluationUtcNow);
                            EnsureRelationComplexity(path, relation);
                        }
                        break;
                    }
                    case "group":
                    {
                        if (!searchApplied)
                        {
                            relation = ComposableSqlPlanner.ApplySearch(relation, _document.Search);
                            searchApplied = true;
                        }
                        (relation, lastShape) = ApplyGroup(
                            relation,
                            composable,
                            path,
                            errors,
                            ignored);
                        if (lastShape is { Metrics: { } groupMetrics, Dimensions: { } groupDimensions })
                        {
                            formatSources = GroupFormatSources(
                                formatSources,
                                groupDimensions,
                                groupMetrics,
                                lastShape.CountName ?? "__count",
                                formats);
                            ApplyGroupLabels(labels, groupMetrics);
                        }
                        shapeCount++;
                        EnsureRelationComplexity(path, relation);
                        break;
                    }
                    case "chart":
                    {
                        if (!searchApplied)
                        {
                            relation = ComposableSqlPlanner.ApplySearch(relation, _document.Search);
                            searchApplied = true;
                        }
                        var chart = TableCompositionValidator.ValidateChartShape(
                            composable,
                            path,
                            relation.Schema.Lookup,
                            errors);
                        if (chart is not null)
                        {
                            var priorFormatSources = formatSources;
                            relation = ComposableSqlPlanner.Chart(
                                relation,
                                $"{_definition.Name}#{tableId}#chart{shapeCount + 1}",
                                chart,
                                _definition.GetEffectiveDialect());
                            lastShape = new CompiledShape(ShapeKind.Chart, path, Chart: chart);
                            formatSources = ChartFormatSources(
                                priorFormatSources,
                                chart,
                                relation.Schema,
                                formats);
                            ApplyChartLabels(labels, chart, relation.Schema);
                        }
                        shapeCount++;
                        EnsureRelationComplexity(path, relation);
                        break;
                    }
                    case "pivot":
                    {
                        if (!searchApplied)
                        {
                            relation = ComposableSqlPlanner.ApplySearch(relation, _document.Search);
                            searchApplied = true;
                        }
                        (relation, lastShape) = await ApplyPivot(
                            relation,
                            composable,
                            path,
                            tableId,
                            shapeCount + 1,
                            errors,
                            ignored,
                            labels,
                            formats,
                            formatSources,
                            ct);
                        shapeCount++;
                        EnsureRelationComplexity(path, relation);
                        break;
                    }
                    case "select":
                    case "sort":
                    case "highlight":
                    case "break":
                    case "aggregate":
                        terminalItems.Add(located);
                        break;
                    case "labels":
                        Merge(
                            labels,
                            StateValidator.ResolveLabels(composable.Labels),
                            composable.Labels is { Count: 0 }
                                || (!localLabelsSeen && string.Equals(
                                    table.From.Trim(),
                                    "definition",
                                    StringComparison.OrdinalIgnoreCase)));
                        localLabelsSeen = true;
                        break;
                    case "formats":
                        var clearFormats = composable.Formats is { Count: 0 };
                        Merge(
                            formats,
                            StateValidator.ResolveFormats(composable.Formats),
                            clearFormats);
                        if (clearFormats)
                            formatSources = relation.Schema.Columns.ToDictionary(
                                column => column.Name,
                                _ => (string?)null,
                                StringComparer.OrdinalIgnoreCase);
                        break;
                    case "":
                        errors.Add(new ValidationError($"{path}.kind", "composable kind is required"));
                        break;
                    default:
                        errors.Add(new ValidationError(
                            $"{path}.kind",
                            $"unknown composable kind '{composable.Kind}'"));
                        break;
                }
            }

            if (errors.Count > 0) throw new ReportValidationException(errors);
            var result = new CompiledComposableTable(
                relation,
                searchApplied,
                shapeCount,
                relation.NestingDepth,
                computedCount,
                filterCount,
                lastShape,
                EmptyLayer(relation.Schema),
                labels,
                formats,
                formatSources,
                terminalItems,
                ignored);
            _memo[tableId] = result;
            return result;
        }
        finally
        {
            _visiting.Remove(requestedId);
        }
    }

    private (ComposableSqlRelation Relation, CompiledShape? Shape) ApplyGroup(
        ComposableSqlRelation source,
        TableComposable composable,
        string path,
        List<ValidationError> errors,
        List<IgnoredItem> ignored)
    {
        var before = errors.Count;
        var dimensions = TableCompositionValidator.ResolveDimensions(
            composable.By,
            "group",
            source.Schema.Lookup,
            ignored);
        if (dimensions.Count == 0)
            errors.Add(new ValidationError($"{path}.by", "a group stage requires at least one valid group column"));
        ValidateMetricCount(composable.Values, path, errors);
        var metrics = TableCompositionValidator.ValidateMetrics(
            composable.Values,
            $"{path}.values",
            source.Schema,
            errors,
            ignored);
        ValidateGroupedWidth(dimensions.Count, metrics.Count, $"{path}.by", errors);
        ValidateMedianProjectionWidth(dimensions, metrics, path, errors);
        if (errors.Count > before) return (source, null);

        var countName = UniqueLogicalName(
            dimensions.Select(column => column.Name)
                .Concat(metrics.Select(metric => metric.Id)),
            "__count");

        var relation = ComposableSqlPlanner.Group(
            source,
            $"{_definition.Name}#group",
            dimensions,
            metrics,
            _definition.GetEffectiveDialect(),
            countName);
        return (relation, new CompiledShape(
            ShapeKind.Group,
            path,
            Dimensions: dimensions,
            Metrics: metrics,
            CountName: countName));
    }

    private async Task<(ComposableSqlRelation Relation, CompiledShape? Shape)> ApplyPivot(
        ComposableSqlRelation source,
        TableComposable composable,
        string path,
        string tableId,
        int shapeOrdinal,
        List<ValidationError> errors,
        List<IgnoredItem> ignored,
        Dictionary<string, string> labels,
        IReadOnlyDictionary<string, ColumnFormat> formats,
        Dictionary<string, string?> formatSources,
        CancellationToken ct)
    {
        var before = errors.Count;
        var rows = TableCompositionValidator.ResolveDimensions(
            composable.Rows,
            "pivot row",
            source.Schema.Lookup,
            ignored);
        var columns = TableCompositionValidator.ResolveDimensions(
            composable.Cols,
            "pivot",
            source.Schema.Lookup,
            ignored);
        if (rows.Count == 0)
            errors.Add(new ValidationError($"{path}.rows", "a pivot stage requires at least one valid row dimension"));
        if (columns.Count == 0)
            errors.Add(new ValidationError($"{path}.cols", "a pivot stage requires at least one valid column dimension"));
        var rowNames = new HashSet<string>(rows.Select(column => column.Name), StringComparer.OrdinalIgnoreCase);
        var overlap = columns.FirstOrDefault(column => rowNames.Contains(column.Name));
        if (overlap is not null)
            errors.Add(new ValidationError($"{path}.cols", $"pivot column '{overlap.Name}' is already a row dimension"));
        ValidateMetricCount(composable.Values, path, errors);
        var metrics = TableCompositionValidator.ValidateMetrics(
            composable.Values,
            $"{path}.values",
            source.Schema,
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
        var grouped = ComposableSqlPlanner.Group(
            source,
            $"{_definition.Name}#{tableId}#pivot-source{shapeOrdinal}",
            rows.Concat(columns).ToList(),
            metrics,
            _definition.GetEffectiveDialect(),
            pivotCountName);
        var totalsRelation = composable.Totals == true
            ? ComposableSqlPlanner.Group(
                source,
                $"{_definition.Name}#{tableId}#pivot-totals{shapeOrdinal}",
                columns,
                metrics,
                _definition.GetEffectiveDialect(),
                pivotCountName)
            : null;
        var groups = await _readPivotGroups(
            grouped.Query.Clone().Limit(ReportExecutor.MaxPivotGroups + 1),
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
        var publicColumnNames = new HashSet<string>(
            rows.Select(row => row.Name),
            StringComparer.OrdinalIgnoreCase);
        foreach (var key in columnKeys)
        {
            var baseKeyName = PivotKeyName(key);
            var keyName = baseKeyName;
            var suffix = 2;
            var cellIds = metrics.Count == 0
                ? ["__count"]
                : metrics.Select(metric => metric.Id).ToArray();
            while (cellIds.Any(id => publicColumnNames.Contains($"{id}@{keyName}")))
                keyName = $"{baseKeyName}~{suffix++}";
            foreach (var id in cellIds) publicColumnNames.Add($"{id}@{keyName}");
            var keyLabel = string.Join(" · ", key.Select(FormatPivotKeyPart));
            var cells = new List<PivotCellColumn>();
            if (metrics.Count == 0)
            {
                var name = $"__count@{keyName}";
                cells.Add(new PivotCellColumn(
                    pivotCountName,
                    new ColumnModel { Name = name, Label = keyLabel, ClrType = typeof(long), IsNullable = true }));
                formatSources[name] = null;
            }
            else
            {
                foreach (var metric in metrics)
                {
                    var name = $"{metric.Id}@{keyName}";
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
                    formatSources[name] = metric.Fn is AggregateFn.Count or AggregateFn.CountDistinct
                        ? null
                        : ResolveFormatSource(formatSources, metric.Column, formats);
                    var sourceLabel = labels.TryGetValue(metric.Column.Name, out var displayLabel)
                        ? displayLabel
                        : metric.Column.Label;
                    var aggregateLabel = $"{ReportResultColumns.AggregateName(metric.Fn)}({sourceLabel})";
                    labels[name] = metrics.Count == 1
                        ? keyLabel
                        : $"{keyLabel} · {aggregateLabel}";
                }
            }
            keys.Add(new PivotColumnKey(key, cells));
        }

        var wide = ComposableSqlPlanner.PivotWide(
            grouped,
            $"{_definition.Name}#{tableId}#pivot{shapeOrdinal}",
            rows,
            columns,
            metrics,
            keys);
        var retainedSources = rows.ToDictionary(
            row => row.Name,
            row => ResolveFormatSource(formatSources, row, formats),
            StringComparer.OrdinalIgnoreCase);
        foreach (var cell in keys.SelectMany(key => key.Cells))
            retainedSources[cell.Column.Name] = formatSources[cell.Column.Name];
        formatSources.Clear();
        foreach (var (name, sourceName) in retainedSources) formatSources[name] = sourceName;
        return (
            wide,
            new CompiledShape(
                ShapeKind.Pivot,
                path,
                Dimensions: rows,
                Metrics: metrics,
                PivotColumns: columns,
                PivotTotals: composable.Totals == true,
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

    private static ValidTableLayer EmptyLayer(ReportSchema schema)
        => new(schema, [], [], [], [], [], schema.Columns, schema.Columns, [], [],
            new Dictionary<string, string>(), new Dictionary<string, ColumnFormat>());

    private static void Merge<T>(
        Dictionary<string, T> target,
        IReadOnlyDictionary<string, T> source,
        bool clear)
    {
        if (clear) target.Clear();
        foreach (var (name, value) in source) target[name] = value;
    }

    private static Dictionary<string, string?> GroupFormatSources(
        IReadOnlyDictionary<string, string?> prior,
        IReadOnlyList<ColumnModel> dimensions,
        IReadOnlyList<ValidMetric> metrics,
        string countName,
        IReadOnlyDictionary<string, ColumnFormat> formats)
    {
        var result = dimensions.ToDictionary(
            dimension => dimension.Name,
            dimension => ResolveFormatSource(prior, dimension, formats),
            StringComparer.OrdinalIgnoreCase);
        result[countName] = null;
        foreach (var metric in metrics)
            result[metric.Id] = metric.Fn is AggregateFn.Count or AggregateFn.CountDistinct
                ? null
                : ResolveFormatSource(prior, metric.Column, formats);
        return result;
    }

    private static void ValidateMetricCount(
        IReadOnlyList<MetricRule>? values,
        string path,
        List<ValidationError> errors)
    {
        if (values is { Count: > MaxShapeMetrics })
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
        ValidTableLayer terminal,
        List<ValidationError> errors)
    {
        var breakItem = plan.TerminalItems.LastOrDefault(item => string.Equals(
            item.Value.Kind,
            "break",
            StringComparison.OrdinalIgnoreCase));
        var aggregateItem = plan.TerminalItems.LastOrDefault(item => string.Equals(
            item.Value.Kind,
            "aggregate",
            StringComparison.OrdinalIgnoreCase));
        if (terminal.Aggregates.Count > MaxGeneratedColumns)
            errors.Add(new ValidationError(
                aggregateItem is null ? "tables" : $"{aggregateItem.Path}.aggregates",
                $"terminal aggregates may expose at most {MaxGeneratedColumns} values"));
        var breakOutputCount = (long)terminal.Breaks.Count + 1L + terminal.Aggregates.Count;
        if (terminal.Breaks.Count > 0 && breakOutputCount > MaxGeneratedColumns)
            errors.Add(new ValidationError(
                aggregateItem?.Path ?? breakItem?.Path ?? "tables",
                $"break totals may expose at most {MaxGeneratedColumns} columns"));
        if (terminal.Aggregates.Count > 0)
            ValidateMedianProjectionWidth(
                terminal.Breaks,
                terminal.Aggregates.Select(aggregate =>
                    (aggregate.Column, aggregate.Fn)),
                aggregateItem?.Path ?? breakItem?.Path ?? "tables",
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

    private static void ApplyGroupLabels(
        Dictionary<string, string> labels,
        IReadOnlyList<ValidMetric> metrics)
    {
        foreach (var metric in metrics)
        {
            var sourceLabel = labels.TryGetValue(metric.Column.Name, out var displayLabel)
                ? displayLabel
                : metric.Column.Label;
            labels[metric.Id] = $"{ReportResultColumns.AggregateName(metric.Fn)}({sourceLabel})";
        }
    }

    private static void ApplyChartLabels(
        Dictionary<string, string> labels,
        ValidChart chart,
        ReportSchema output)
    {
        var metricName = output.Columns[1].Name;
        if (chart.Value is null)
        {
            labels[metricName] = "Count";
            return;
        }
        var sourceLabel = labels.TryGetValue(chart.Value.Name, out var displayLabel)
            ? displayLabel
            : chart.Value.Label;
        labels[metricName] = chart.Fn is { } function
            ? $"{ReportResultColumns.AggregateName(function)}({sourceLabel})"
            : sourceLabel;
    }

    private static Dictionary<string, string?> ChartFormatSources(
        IReadOnlyDictionary<string, string?> prior,
        ValidChart chart,
        ReportSchema output,
        IReadOnlyDictionary<string, ColumnFormat> formats)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [output.Columns[0].Name] = ResolveFormatSource(prior, chart.Label, formats),
        };
        result[output.Columns[1].Name] = chart.Value is null
            || chart.Fn is AggregateFn.Count or AggregateFn.CountDistinct
                ? null
                : ResolveFormatSource(prior, chart.Value, formats);
        return result;
    }

    private static string? ResolveFormatSource(
        IReadOnlyDictionary<string, string?> prior,
        ColumnModel column,
        IReadOnlyDictionary<string, ColumnFormat> formats)
        => formats.ContainsKey(column.Name)
            ? column.Name
            : prior.TryGetValue(column.Name, out var inherited)
            ? inherited
            : column.Name;

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
            for (var index = 0; index < left.Length; index++)
            {
                if (left[index] is byte[] leftBytes && right[index] is byte[] rightBytes)
                {
                    if (!leftBytes.AsSpan().SequenceEqual(rightBytes)) return false;
                    continue;
                }
                if (!EqualityComparer<object?>.Default.Equals(left[index], right[index])) return false;
            }
            return true;
        }

        public int GetHashCode(object?[] key)
        {
            var hash = new HashCode();
            foreach (var value in key)
            {
                if (value is byte[] bytes)
                {
                    hash.Add(typeof(byte[]));
                    foreach (var part in bytes) hash.Add(part);
                }
                else
                {
                    hash.Add(value);
                }
            }
            return hash.ToHashCode();
        }
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

internal sealed record CompiledComposableTable(
    ComposableSqlRelation Relation,
    bool SearchApplied,
    int ShapeCount,
    int RelationStages,
    int ComputedRuleCount,
    int FilterRuleCount,
    CompiledShape? LastShape,
    ValidTableLayer Terminal,
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlyDictionary<string, ColumnFormat> Formats,
    IReadOnlyDictionary<string, string?> FormatSources,
    IReadOnlyList<LocatedTableComposable> TerminalItems,
    IReadOnlyList<IgnoredItem> Ignored);

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
    IReadOnlyList<PivotColumnKey>? PivotKeys = null);

internal enum ShapeKind
{
    Group,
    Pivot,
    Chart,
}
