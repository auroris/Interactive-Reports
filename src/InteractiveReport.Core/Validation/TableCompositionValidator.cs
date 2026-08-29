using System.Text.RegularExpressions;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Expressions;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Schema;

namespace InteractiveReport.Core.Validation;

/// <summary>
/// Resolves the active named table and folds its delegated composables into a typed
/// execution plan. Table identifiers are opaque; only the reserved "definition"
/// input has engine meaning.
/// </summary>
internal static partial class TableCompositionValidator
{
    /// <summary>
    /// Ordered composition split only at the optional shape boundary. Input and output
    /// retain the document order and provenance of every ordinary composable.
    /// </summary>
    public sealed record Composition(
        IReadOnlyList<LocatedTableComposable> Input,
        LocatedTableComposable? Shape,
        IReadOnlyList<LocatedTableComposable> Output)
    {
        public bool IsGrid => Shape is null;
        public string? ShapePath => Shape?.Path;

        public string ShapeProperty(string property)
            => $"{ShapePath ?? "tables"}.{property}";
    }

    private static readonly Composition BareSource = new([], null, []);

    /// <summary>
    /// Resolves activeTable through From delegation, then folds relational composables
    /// from the definition outward. Presentation composables belong to the active table.
    /// </summary>
    public static Composition Fold(ReportState state, List<ValidationError> errors)
    {
        if (state.Tables is not { Count: > 0 })
            return BareSource;

        if (string.IsNullOrWhiteSpace(state.ActiveTable))
        {
            errors.Add(new ValidationError(
                "activeTable",
                "activeTable is required when tables are present"));
            return BareSource;
        }

        var tables = new Dictionary<string, ReportTable>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, table) in state.Tables)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                errors.Add(new ValidationError("tables", "table identifiers cannot be blank"));
                continue;
            }
            if (!tables.TryAdd(name, table))
                errors.Add(new ValidationError(
                    $"tables.{name}",
                    $"table identifier '{name}' differs from another identifier only by case"));
        }

        var chain = ResolveChain(state.ActiveTable.Trim(), tables, errors);
        if (chain.Count == 0) return BareSource;

        var input = new List<LocatedTableComposable>();
        var output = new List<LocatedTableComposable>();
        var activeOrdinary = new List<(LocatedTableComposable Item, bool AfterShape)>();
        LocatedTableComposable? shape = null;
        for (var tableIndex = 0; tableIndex < chain.Count; tableIndex++)
        {
            var (name, table) = chain[tableIndex];
            var active = tableIndex == chain.Count - 1;
            var composables = table.Composables ?? [];
            for (var index = 0; index < composables.Count; index++)
            {
                var composable = composables[index];
                var path = $"tables.{name}.composables[{index}]";
                var kind = (composable.Kind ?? "").Trim().ToLowerInvariant();
                if (kind is "group" or "pivot" or "chart")
                {
                    if (shape is not null)
                    {
                        errors.Add(new ValidationError(
                            path,
                            $"'{kind}' cannot follow the existing '{shape.Value.Kind}' shape composable yet"));
                        continue;
                    }
                    shape = new LocatedTableComposable(composable, path);
                    continue;
                }

                if (kind.Length == 0)
                {
                    errors.Add(new ValidationError($"{path}.kind", "composable kind is required"));
                    continue;
                }
                else if (!IsOrdinary(kind))
                {
                    errors.Add(new ValidationError($"{path}.kind", $"unknown composable kind '{composable.Kind}'"));
                    continue;
                }

                var located = new LocatedTableComposable(composable, path);
                if (active)
                {
                    activeOrdinary.Add((located, shape is not null));
                    continue;
                }

                // A parent contributes its relational output plus input metadata that
                // synthetic shape columns can inherit through label/format sources.
                // Selection, sorting, breaks, totals, and highlights remain that
                // parent's terminal UI state.
                if (IsRelational(kind) || IsMetadata(kind))
                    (shape is null ? input : output).Add(located);
            }
        }

        foreach (var (item, afterShape) in activeOrdinary)
        {
            var kind = item.Value.Kind.Trim().ToLowerInvariant();
            if (shape is null || ((IsRelational(kind) || IsMetadata(kind)) && !afterShape)) input.Add(item);
            else output.Add(item);
        }

        ValidateCrossBoundaryRuleBudget(
            input,
            output,
            "compute",
            static composable => composable.Computed?.Count ?? 0,
            20,
            "computed",
            "computed columns",
            errors);
        ValidateCrossBoundaryRuleBudget(
            input,
            output,
            "filter",
            static composable => composable.Filters?.Count ?? 0,
            50,
            "filters",
            "filter rules",
            errors);

        return new Composition(input, shape, output);
    }

    /// <summary>
    /// A shape splits schema binding into two passes, but it must not split resource
    /// budgets. Per-layer validators handle a layer that exceeds its own limit; this
    /// check catches only the case where individually valid input and output layers
    /// exceed the active composition's limit together.
    /// </summary>
    private static void ValidateCrossBoundaryRuleBudget(
        IReadOnlyList<LocatedTableComposable> input,
        IReadOnlyList<LocatedTableComposable> output,
        string kind,
        Func<TableComposable, int> countRules,
        int limit,
        string property,
        string description,
        List<ValidationError> errors)
    {
        var inputCount = input
            .Where(item => string.Equals(item.Value.Kind, kind, StringComparison.OrdinalIgnoreCase))
            .Sum(item => countRules(item.Value));
        var outputCount = output
            .Where(item => string.Equals(item.Value.Kind, kind, StringComparison.OrdinalIgnoreCase))
            .Sum(item => countRules(item.Value));
        if (inputCount > limit || outputCount > limit || inputCount + outputCount <= limit)
            return;

        var running = inputCount;
        foreach (var item in output.Where(item =>
                     string.Equals(item.Value.Kind, kind, StringComparison.OrdinalIgnoreCase)))
        {
            running += countRules(item.Value);
            if (running <= limit) continue;
            errors.Add(new ValidationError(
                $"{item.Path}.{property}",
                $"at most {limit} {description} per report state"));
            return;
        }
    }

    private static List<(string Name, ReportTable Table)> ResolveChain(
        string active,
        IReadOnlyDictionary<string, ReportTable> tables,
        List<ValidationError> errors)
    {
        var reversed = new List<(string, ReportTable)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = active;
        while (!string.Equals(current, "definition", StringComparison.OrdinalIgnoreCase))
        {
            if (!seen.Add(current))
            {
                errors.Add(new ValidationError("tables", $"table delegation contains a cycle at '{current}'"));
                return [];
            }
            if (!tables.TryGetValue(current, out var table))
            {
                errors.Add(new ValidationError(
                    reversed.Count == 0 ? "activeTable" : $"tables.{reversed[^1].Item1}.from",
                    $"unknown table '{current}'"));
                return [];
            }
            reversed.Add((current, table));
            if (string.IsNullOrWhiteSpace(table.From))
            {
                errors.Add(new ValidationError(
                    $"tables.{current}.from",
                    "from is required and must be 'definition' or another table identifier"));
                return [];
            }
            current = table.From.Trim();
        }
        reversed.Reverse();
        return reversed;
    }

    private static bool IsRelational(string kind)
        => kind is "compute" or "filter";

    private static bool IsMetadata(string kind)
        => kind is "labels" or "formats";

    private static bool IsOrdinary(string kind)
        => kind is "select" or "labels" or "formats" or "compute" or "filter"
            or "sort" or "highlight" or "break" or "aggregate";

    /// <summary>Validates the optional shape, then resumes the ordinary table fold.</summary>
    public static ValidView ValidateTail(
        Composition stages,
        string reportName,
        ReportSchema effectiveSchema,
        List<ValidationError> errors,
        List<IgnoredItem> ignored,
        ColumnPolicy? policy = null)
    {
        var kind = stages.Shape?.Value.Kind?.Trim().ToLowerInvariant();
        return kind switch
        {
            "group" => ValidateGroupTail(stages, reportName, effectiveSchema, errors, ignored, policy),
            "pivot" => ValidatePivot(stages, reportName, effectiveSchema, errors, ignored),
            "chart" => ValidateChartTail(stages, reportName, effectiveSchema, errors, ignored, policy),
            _ => ValidView.Grid,
        };
    }

    private static ValidView ValidateGroupTail(
        Composition stages,
        string reportName,
        ReportSchema effectiveSchema,
        List<ValidationError> errors,
        List<IgnoredItem> ignored,
        ColumnPolicy? policy)
    {
        var shape = stages.Shape!.Value;
        var before = errors.Count;

        var dims = ResolveDimensions(shape.By, "group", effectiveSchema.Lookup, ignored);
        if (dims.Count == 0)
        {
            errors.Add(new ValidationError(
                stages.ShapeProperty("by"),
                "a group stage requires at least one valid group column"));
            return ValidView.Grid;
        }
        if (dims.Any(dim => string.Equals(dim.Name, "__count", StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add(new ValidationError(
                stages.ShapeProperty("by"),
                "'__count' is reserved in a group stage and cannot be a group column"));
            return ValidView.Grid;
        }

        var metrics = ValidateMetrics(shape.Values, stages.ShapeProperty("values"), effectiveSchema, errors, ignored);

        var stageSchema = BuildStageSchema(reportName, dims, metrics);
        var layer = TableLayerValidator.Validate(
            stages.Output,
            $"{reportName}#group",
            stageSchema,
            policy ?? ColumnPolicy.Unrestricted,
            errors,
            ignored);

        if (errors.Count > before)
            return ValidView.Grid;

        return new ValidView(
            ViewMode.GroupBy,
            dims,
            [],
            [],
            metrics,
            Output: layer,
            ShapePath: stages.ShapePath);
    }

    private static ValidView ValidatePivot(
        Composition stages,
        string reportName,
        ReportSchema effectiveSchema,
        List<ValidationError> errors,
        List<IgnoredItem> ignored)
    {
        var shape = stages.Shape!.Value;
        var before = errors.Count;
        var rows = ResolveDimensions(shape.Rows, "pivot row", effectiveSchema.Lookup, ignored);
        var cols = ResolveDimensions(shape.Cols, "pivot", effectiveSchema.Lookup, ignored);

        if (rows.Count == 0)
            errors.Add(new ValidationError(
                stages.ShapeProperty("rows"),
                "a pivot stage requires at least one valid row dimension"));
        if (cols.Count == 0)
            errors.Add(new ValidationError(
                stages.ShapeProperty("cols"),
                "a pivot stage requires at least one valid column dimension"));

        var rowNames = new HashSet<string>(rows.Select(column => column.Name), StringComparer.OrdinalIgnoreCase);
        var overlap = cols.FirstOrDefault(column => rowNames.Contains(column.Name));
        if (overlap is not null)
            errors.Add(new ValidationError(
                stages.ShapeProperty("cols"),
                $"pivot column '{overlap.Name}' is already a row dimension"));

        var metrics = ValidateMetrics(shape.Values, stages.ShapeProperty("values"), effectiveSchema, errors, ignored);
        if (errors.Count > before)
            return ValidView.Grid;

        return new ValidView(
            ViewMode.Pivot,
            [],
            rows,
            cols,
            metrics,
            shape.Totals == true,
            DeferredOutput: stages.Output,
            ShapePath: stages.ShapePath);
    }

    private static ValidView ValidateChartTail(
        Composition stages,
        string reportName,
        ReportSchema effectiveSchema,
        List<ValidationError> errors,
        List<IgnoredItem> ignored,
        ColumnPolicy? policy)
    {
        var chart = ValidateChartShape(
            stages.Shape!.Value,
            stages.ShapePath ?? "tables",
            effectiveSchema.Lookup,
            errors);
        if (chart is null) return ValidView.Grid;

        var outputSchema = ChartSchema(reportName, chart);
        var output = TableLayerValidator.Validate(
            stages.Output,
            $"{reportName}#chart",
            outputSchema,
            policy ?? ColumnPolicy.Unrestricted,
            errors,
            ignored);
        return new ValidView(
            ViewMode.Chart,
            [],
            [],
            [],
            [],
            Chart: chart,
            Output: output,
            ShapePath: stages.ShapePath);
    }

    private static ReportSchema ChartSchema(string reportName, ValidChart chart)
        => ReportSchema.Create(
            $"{reportName}#chart",
            ReportResultColumns.ForChart(chart).Select(column => new ColumnModel
            {
                Name = column.Name,
                Label = column.Label,
                ClrType = column.Type switch
                {
                    "number" => typeof(decimal),
                    "date" => typeof(DateTime),
                    "bool" => typeof(bool),
                    "text" => typeof(string),
                    _ => typeof(object),
                },
                IsComputed = column.Computed,
            }));

    /// <summary>
    /// The group stage's static output schema: dims + __count + metrics by id. Layer
    /// computed columns extend it afterwards, exactly as input computed columns extend
    /// the definition schema.
    /// </summary>
    private static ReportSchema BuildStageSchema(
        string reportName,
        IReadOnlyList<ColumnModel> dims,
        IReadOnlyList<ValidMetric> metrics)
    {
        var columns = new List<ColumnModel>(dims)
        {
            new()
            {
                Name = "__count",
                Label = "Count",
                ClrType = typeof(long),
                IsNullable = false,
            },
        };
        foreach (var metric in metrics)
        {
            columns.Add(new ColumnModel
            {
                Name = metric.Id,
                Label = ReportResultColumns.AggregateLabel(metric.ToAggregate()),
                ClrType = metric.Fn switch
                {
                    AggregateFn.Min or AggregateFn.Max => metric.Column.ClrType,
                    AggregateFn.Count or AggregateFn.CountDistinct => typeof(long),
                    _ => typeof(decimal),
                },
            });
        }
        return ReportSchema.Create($"{reportName}#group", columns);
    }

    private static List<ValidMetric> ValidateMetrics(
        List<MetricRule>? rules,
        string valuesPath,
        ReportSchema effectiveSchema,
        List<ValidationError> errors,
        List<IgnoredItem> ignored)
    {
        var result = new List<ValidMetric>();
        if (rules is null) return result;

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            var path = $"{valuesPath}[{i}]";
            if (!MetricIdPattern().IsMatch(rule.Id))
            {
                errors.Add(new ValidationError(
                    path,
                    $"metric id '{rule.Id}' must match m1, m2, … (lowercase m + digits)"));
                continue;
            }
            if (!seenIds.Add(rule.Id))
            {
                errors.Add(new ValidationError(path, $"duplicate metric id '{rule.Id}'"));
                continue;
            }
            if (effectiveSchema.Lookup.ContainsKey(rule.Id))
            {
                errors.Add(new ValidationError(
                    path,
                    $"metric id '{rule.Id}' shadows a schema column"));
                continue;
            }
            if (!effectiveSchema.TryGetValue(rule.Col, out var column))
            {
                ignored.Add(new IgnoredItem("metric", $"unknown column '{rule.Col}'"));
                continue;
            }
            if (!AggregateCatalog.IsCompatible(column.Kind, rule.Fn))
            {
                var function = System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(rule.Fn.ToString());
                errors.Add(new ValidationError(
                    path,
                    $"aggregate '{function}' is not valid for {column.KindName} column '{column.Name}'"));
                continue;
            }
            result.Add(new ValidMetric(rule.Id, column, rule.Fn));
        }
        return result;
    }

    /// <summary>
    /// Chart validation is stricter than grid aggregation: the metric must come out
    /// numeric, so min/max only chart number columns and a bare (fn-less) value column
    /// must itself be a number. All problems are precise errors — a chart with a broken
    /// spec has no degraded rendering the way a grid with a dropped column does.
    /// </summary>
    private static ValidChart? ValidateChartShape(
        TableComposable shape,
        string shapePath,
        IReadOnlyDictionary<string, ColumnModel> columns,
        List<ValidationError> errors)
    {
        var before = errors.Count;

        ChartType? type = shape.Type?.ToLowerInvariant() switch
        {
            "bar" => ChartType.Bar,
            "line" => ChartType.Line,
            "area" => ChartType.Area,
            "pie" => ChartType.Pie,
            _ => null,
        };
        if (type is null)
            errors.Add(new ValidationError(
                $"{shapePath}.type",
                $"chart type must be 'bar', 'line', 'area', or 'pie', got '{shape.Type}'"));

        ColumnModel? label = null;
        if (string.IsNullOrWhiteSpace(shape.Label))
            errors.Add(new ValidationError($"{shapePath}.label", "a chart stage requires a label column"));
        else if (!columns.TryGetValue(shape.Label, out label))
            errors.Add(new ValidationError(
                $"{shapePath}.label",
                $"unknown chart label column '{shape.Label}'"));
        else if (label.Kind == ColumnKind.Other)
        {
            errors.Add(new ValidationError(
                $"{shapePath}.label",
                $"{label.KindName} column '{label.Name}' cannot label a chart (text, number, date, or bool required)"));
            label = null;
        }

        var fn = shape.Fn;
        ColumnModel? value = null;
        if (string.IsNullOrWhiteSpace(shape.Value))
        {
            if (fn != AggregateFn.Count)
                errors.Add(new ValidationError(
                    $"{shapePath}.value",
                    fn is null
                        ? "a chart stage requires a value column (or fn 'count' to count rows)"
                        : $"chart aggregate '{FnName(fn.Value)}' requires a value column ('count' alone counts rows)"));
        }
        else if (!columns.TryGetValue(shape.Value, out value))
        {
            errors.Add(new ValidationError(
                $"{shapePath}.value",
                $"unknown chart value column '{shape.Value}'"));
        }
        else if (fn is { } f && !AggregateCatalog.IsChartCompatible(value.Kind, f))
        {
            errors.Add(new ValidationError(
                $"{shapePath}.value",
                $"chart values must be numeric: '{FnName(f)}' of {value.KindName} column '{value.Name}' does not produce a number"));
        }
        else if (fn is null && value.Kind != ColumnKind.Number)
        {
            errors.Add(new ValidationError(
                $"{shapePath}.value",
                $"charting one point per row requires a number value column; '{value.Name}' is {value.KindName}"));
        }

        ChartOrientation? orientation = shape.Orientation?.ToLowerInvariant() switch
        {
            null or "" or "vertical" => ChartOrientation.Vertical,
            "horizontal" => ChartOrientation.Horizontal,
            _ => null,
        };
        if (orientation is null)
            errors.Add(new ValidationError(
                $"{shapePath}.orientation",
                $"chart orientation must be 'vertical' or 'horizontal', got '{shape.Orientation}'"));

        ChartSortBy? sortBy = shape.Sort?.By?.ToLowerInvariant() switch
        {
            null or "" or "label" => ChartSortBy.Label,
            "value" => ChartSortBy.Value,
            _ => null,
        };
        if (sortBy is null)
            errors.Add(new ValidationError(
                $"{shapePath}.sort.by",
                $"chart sort must be by 'label' or 'value', got '{shape.Sort!.By}'"));

        if (errors.Count > before)
            return null;

        return new ValidChart(
            type!.Value,
            label!,
            value,
            fn,
            orientation!.Value,
            sortBy!.Value,
            shape.Sort?.Dir ?? SortDir.Asc,
            NormalizeTitle(shape.LabelAxisTitle),
            NormalizeTitle(shape.ValueAxisTitle));
    }

    private static string FnName(AggregateFn fn)
        => System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(fn.ToString());

    private static string? NormalizeTitle(string? title)
        => string.IsNullOrWhiteSpace(title) ? null : title.Trim();

    private static List<ColumnModel> ResolveDimensions(
        List<string>? names,
        string description,
        IReadOnlyDictionary<string, ColumnModel> columns,
        List<IgnoredItem> ignored)
    {
        var result = new List<ColumnModel>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names ?? [])
        {
            if (!columns.TryGetValue(name, out var column))
            {
                ignored.Add(new IgnoredItem(
                    "view",
                    $"unknown {description} column '{name}'"));
                continue;
            }
            if (seen.Add(column.Name)) result.Add(column);
        }
        return result;
    }

    [GeneratedRegex(@"^m\d+$")]
    private static partial Regex MetricIdPattern();
}
