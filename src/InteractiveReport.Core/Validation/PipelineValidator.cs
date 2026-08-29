using System.Text.RegularExpressions;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Expressions;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Schema;

namespace InteractiveReport.Core.Validation;

/// <summary>
/// Normalizes the stage pipeline and validates its tail. Group, pivot, and chart are
/// independent views over the same source table; none consumes another view.
/// </summary>
internal static partial class PipelineValidator
{
    /// <summary>Normalized stage bundle; null members mean the stage is absent.</summary>
    public sealed record Stages(
        StageLayer Source,
        StageShape? Group,
        StageLayer? GroupLayer,
        StageShape? Pivot,
        StageLayer? PivotLayer,
        StageShape? Chart)
    {
        public bool IsGrid => Group is null && Pivot is null && Chart is null;
    }

    private static readonly Stages BareSource = new(new StageLayer(), null, null, null, null, null);

    /// <summary>
    /// Shape-checks the pipeline sequence. Sequence errors are precise and fall back to
    /// the bare source stage so subsequent validation can still report layer problems.
    /// </summary>
    public static Stages Normalize(List<PipelineStage>? pipeline, List<ValidationError> errors)
    {
        if (pipeline is null || pipeline.Count == 0)
            return BareSource;

        var kinds = pipeline
            .Select(stage => (stage.Shape?.Kind ?? "source").Trim())
            .ToList();

        if (!string.Equals(kinds[0], "source", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(new ValidationError(
                "pipeline[0].shape.kind",
                $"the first pipeline stage must be 'source', got '{kinds[0]}'"));
            return BareSource;
        }

        var source = pipeline[0].Layer ?? new StageLayer();
        var tail = string.Join(",", kinds.Skip(1).Select(kind => kind.ToLowerInvariant()));
        switch (tail)
        {
            case "":
                return new Stages(source, null, null, null, null, null);
            case "group":
                return new Stages(source, pipeline[1].Shape!, pipeline[1].Layer, null, null, null);
            case "pivot":
                return new Stages(source, null, null, pipeline[1].Shape!, pipeline[1].Layer, null);
            case "chart":
                return new Stages(source, null, null, null, null, pipeline[1].Shape!);
            default:
                errors.Add(new ValidationError(
                    "pipeline",
                    $"unsupported pipeline shape [source, {tail}] — supported tails are none, group, pivot, and chart"));
                return new Stages(source, null, null, null, null, null);
        }
    }

    /// <summary>Validates the tail stages against the source's effective schema.</summary>
    public static ValidView ValidateTail(
        Stages stages,
        string reportName,
        ReportSchema effectiveSchema,
        List<ValidationError> errors,
        List<IgnoredItem> ignored,
        ColumnPolicy? policy = null)
    {
        if (stages.Chart is { } chart)
            return ValidateChart(chart, effectiveSchema.Lookup, errors);
        if (stages.Pivot is not null)
            return ValidatePivot(stages, reportName, effectiveSchema, errors, ignored);
        if (stages.Group is null)
            return ValidView.Grid;

        return ValidateGroupTail(stages, reportName, effectiveSchema, errors, ignored, policy);
    }

    private static ValidView ValidateGroupTail(
        Stages stages,
        string reportName,
        ReportSchema effectiveSchema,
        List<ValidationError> errors,
        List<IgnoredItem> ignored,
        ColumnPolicy? policy)
    {
        var shape = stages.Group!;
        var before = errors.Count;

        var dims = ResolveDimensions(shape.By, "group", effectiveSchema.Lookup, ignored);
        if (dims.Count == 0)
        {
            errors.Add(new ValidationError(
                "pipeline[1].shape.by",
                "a group stage requires at least one valid group column"));
            return ValidView.Grid;
        }
        if (dims.Any(dim => string.Equals(dim.Name, "__count", StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add(new ValidationError(
                "pipeline[1].shape.by",
                "'__count' is reserved in a group stage and cannot be a group column"));
            return ValidView.Grid;
        }

        var metrics = ValidateMetrics(shape.Values, effectiveSchema, errors, ignored);

        RejectUnsupportedLayer(
            stages.GroupLayer,
            "pipeline[1].layer",
            errors,
            allowLabels: true,
            allowFormats: true,
            allowColumns: true,
            allowComputed: true,
            allowFilters: true,
            allowSorts: true,
            allowHighlights: true,
            allowBreaks: true,
            allowAggregates: true);

        var stageSchema = BuildStageSchema(reportName, dims, metrics);
        var layer = ValidateGroupLayer(
            stages,
            reportName,
            stageSchema,
            errors,
            ignored,
            policy);

        if (errors.Count > before)
            return ValidView.Grid;

        return new ValidView(
            ViewMode.GroupBy,
            dims,
            [],
            [],
            metrics,
            GroupLayer: layer);
    }

    private static ValidView ValidatePivot(
        Stages stages,
        string reportName,
        ReportSchema effectiveSchema,
        List<ValidationError> errors,
        List<IgnoredItem> ignored)
    {
        var shape = stages.Pivot!;
        var before = errors.Count;
        var rows = ResolveDimensions(shape.Rows, "pivot row", effectiveSchema.Lookup, ignored);
        var cols = ResolveDimensions(shape.Cols, "pivot", effectiveSchema.Lookup, ignored);

        if (rows.Count == 0)
            errors.Add(new ValidationError(
                "pipeline[1].shape.rows",
                "a pivot stage requires at least one valid row dimension"));
        if (cols.Count == 0)
            errors.Add(new ValidationError(
                "pipeline[1].shape.cols",
                "a pivot stage requires at least one valid column dimension"));

        var rowNames = new HashSet<string>(rows.Select(column => column.Name), StringComparer.OrdinalIgnoreCase);
        var overlap = cols.FirstOrDefault(column => rowNames.Contains(column.Name));
        if (overlap is not null)
            errors.Add(new ValidationError(
                "pipeline[1].shape.cols",
                $"pivot column '{overlap.Name}' is already a row dimension"));

        var metrics = ValidateMetrics(shape.Values, effectiveSchema, errors, ignored);
        RejectUnsupportedLayer(
            stages.PivotLayer,
            "pipeline[1].layer",
            errors,
            allowLabels: true,
            allowFormats: true,
            allowColumns: true,
            allowComputed: true,
            allowFilters: true,
            allowSorts: true,
            allowHighlights: true);

        if (errors.Count > before)
            return ValidView.Grid;

        return new ValidView(
            ViewMode.Pivot,
            [],
            rows,
            cols,
            metrics,
            shape.Totals == true,
            PivotLayer: stages.PivotLayer ?? new StageLayer());
    }

    /// <summary>
    /// The group stage's static output schema: dims + __count + metrics by id. Layer
    /// computed columns extend it afterwards, exactly as source computed extend the
    /// base schema.
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

    private static ValidStageLayer ValidateGroupLayer(
        Stages stages,
        string reportName,
        ReportSchema stageSchema,
        List<ValidationError> errors,
        List<IgnoredItem> ignored,
        ColumnPolicy? policy)
    {
        var layer = stages.GroupLayer ?? new StageLayer();

        var computed = ComputedColumnValidator.Validate(
            layer.Computed,
            stageSchema.Lookup,
            errors,
            collectionPath: "pipeline[1].layer.computed");
        var extended = stageSchema.Extend(
            $"{reportName}#group",
            computed.Select(rule => rule.Effect.Column));

        var filters = ExpressionRuleCompiler.Compile<FilterRule, IncludeRowEffect>(
            layer.Filters,
            maxRules: 50,
            collectionPath: "pipeline[1].layer.filters",
            extended.Lookup,
            ExpressionRequirement.Predicate,
            prepareEffect: static (_, _) => _ => new IncludeRowEffect(),
            errors);

        // The policy reaches pass-through dims here: a dim is the base ColumnModel
        // itself, so a non-sortable base column stays unsortable under a group tail.
        var sorts = StateValidator.ValidateSorts(layer.Sorts, extended, ignored, policy);
        var decorations = HighlightRuleValidator.Validate(
            layer.Highlights,
            extended.Lookup,
            errors,
            ignored,
            collectionPath: "pipeline[1].layer.highlights");
        var select = StateValidator.ValidateColumns(layer.Columns, extended, ignored);
        var aggregates = AggregateRuleValidator.Validate(
            layer.Aggregates,
            "pipeline[1].layer.aggregates",
            extended.Lookup,
            errors,
            ignored);
        var breaks = StateValidator.ValidateBreaks(layer.Breaks, extended, ignored, policy);

        // A break key is structural row data. Keep it in the result even when an
        // explicit projection omitted it so the renderer can recognize boundaries.
        foreach (var column in breaks)
            if (!select.Any(selected => string.Equals(
                    selected.Name,
                    column.Name,
                    StringComparison.OrdinalIgnoreCase)))
                select.Add(column);

        return new ValidStageLayer(
            extended,
            computed,
            filters,
            decorations,
            sorts,
            select,
            aggregates,
            breaks,
            StateValidator.ResolveLabels(layer.Labels));
    }

    private static List<ValidMetric> ValidateMetrics(
        List<MetricRule>? rules,
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
            var path = $"pipeline[1].shape.values[{i}]";
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

    /// <summary>Reports precise errors for layer slots a stage kind does not support at T0.</summary>
    private static void RejectUnsupportedLayer(
        StageLayer? layer,
        string path,
        List<ValidationError> errors,
        bool allowLabels = false,
        bool allowFormats = false,
        bool allowColumns = false,
        bool allowComputed = false,
        bool allowFilters = false,
        bool allowSorts = false,
        bool allowHighlights = false,
        bool allowBreaks = false,
        bool allowAggregates = false)
    {
        if (layer is null) return;

        void Reject(bool allowed, bool present, string slot, string reason)
        {
            if (!allowed && present)
                errors.Add(new ValidationError($"{path}.{slot}", reason));
        }

        Reject(allowColumns, layer.Columns is { Count: > 0 }, "columns", "this stage does not support a column selection");
        Reject(allowLabels, layer.Labels is { Count: > 0 }, "labels", "this stage does not support labels");
        Reject(allowFormats, layer.Formats is { Count: > 0 }, "formats", "this stage does not support formats");
        Reject(allowComputed, layer.Computed is { Count: > 0 }, "computed", "computed columns are not supported on this stage yet");
        Reject(allowFilters, layer.Filters is { Count: > 0 }, "filters", "this stage does not support filters");
        Reject(allowSorts, layer.Sorts is { Count: > 0 }, "sorts", "this stage does not support sorts");
        Reject(allowHighlights, layer.Highlights is { Count: > 0 }, "highlights", "highlights are not supported on this stage yet");
        Reject(allowBreaks, layer.Breaks is { Count: > 0 }, "breaks", "this stage does not support control breaks");
        Reject(allowAggregates, layer.Aggregates is { Count: > 0 }, "aggregates", "this stage does not support footer aggregates");
    }

    /// <summary>
    /// Chart validation is stricter than grid aggregation: the metric must come out
    /// numeric, so min/max only chart number columns and a bare (fn-less) value column
    /// must itself be a number. All problems are precise errors — a chart with a broken
    /// spec has no degraded rendering the way a grid with a dropped column does.
    /// </summary>
    private static ValidView ValidateChart(
        StageShape shape,
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
                "pipeline[1].shape.type",
                $"chart type must be 'bar', 'line', 'area', or 'pie', got '{shape.Type}'"));

        ColumnModel? label = null;
        if (string.IsNullOrWhiteSpace(shape.Label))
            errors.Add(new ValidationError("pipeline[1].shape.label", "a chart stage requires a label column"));
        else if (!columns.TryGetValue(shape.Label, out label))
            errors.Add(new ValidationError(
                "pipeline[1].shape.label",
                $"unknown chart label column '{shape.Label}'"));
        else if (label.Kind == ColumnKind.Other)
        {
            errors.Add(new ValidationError(
                "pipeline[1].shape.label",
                $"{label.KindName} column '{label.Name}' cannot label a chart (text, number, date, or bool required)"));
            label = null;
        }

        var fn = shape.Fn;
        ColumnModel? value = null;
        if (string.IsNullOrWhiteSpace(shape.Value))
        {
            if (fn != AggregateFn.Count)
                errors.Add(new ValidationError(
                    "pipeline[1].shape.value",
                    fn is null
                        ? "a chart stage requires a value column (or fn 'count' to count rows)"
                        : $"chart aggregate '{FnName(fn.Value)}' requires a value column ('count' alone counts rows)"));
        }
        else if (!columns.TryGetValue(shape.Value, out value))
        {
            errors.Add(new ValidationError(
                "pipeline[1].shape.value",
                $"unknown chart value column '{shape.Value}'"));
        }
        else if (fn is { } f && !AggregateCatalog.IsChartCompatible(value.Kind, f))
        {
            errors.Add(new ValidationError(
                "pipeline[1].shape.value",
                $"chart values must be numeric: '{FnName(f)}' of {value.KindName} column '{value.Name}' does not produce a number"));
        }
        else if (fn is null && value.Kind != ColumnKind.Number)
        {
            errors.Add(new ValidationError(
                "pipeline[1].shape.value",
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
                "pipeline[1].shape.orientation",
                $"chart orientation must be 'vertical' or 'horizontal', got '{shape.Orientation}'"));

        ChartSortBy? sortBy = shape.Sort?.By?.ToLowerInvariant() switch
        {
            null or "" or "label" => ChartSortBy.Label,
            "value" => ChartSortBy.Value,
            _ => null,
        };
        if (sortBy is null)
            errors.Add(new ValidationError(
                "pipeline[1].shape.sort.by",
                $"chart sort must be by 'label' or 'value', got '{shape.Sort!.By}'"));

        if (errors.Count > before)
            return ValidView.Grid;

        return new ValidView(ViewMode.Chart, [], [], [], [], Chart: new ValidChart(
            type!.Value,
            label!,
            value,
            fn,
            orientation!.Value,
            sortBy!.Value,
            shape.Sort?.Dir ?? SortDir.Asc,
            NormalizeTitle(shape.LabelAxisTitle),
            NormalizeTitle(shape.ValueAxisTitle)));
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
