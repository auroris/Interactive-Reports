using InteractiveReport.Core.Model;
using InteractiveReport.Core.Planning;
using InteractiveReport.Core.Schema;

namespace InteractiveReport.Core.Validation;

/// <summary>
/// Schema-binds the domain values carried by canonical group, pivot, and chart
/// transformations. Relation construction remains the responsibility of the canonical
/// table compiler.
/// </summary>
internal static class ShapeBindingRules
{
    /// <summary>
    /// Resolves group or pivot metrics, validates their ids and aggregate compatibility, and removes invalid entries.
    /// </summary>
    /// <param name="rules">The canonical metrics in deterministic source order.</param>
    /// <param name="effectiveSchema">The relation schema at the shape boundary.</param>
    /// <param name="errors">The validation list that receives identifier and aggregate/type errors.</param>
    /// <param name="ignored">The diagnostics list that receives metrics with unknown source columns.</param>
    /// <returns>Valid metrics in source order.</returns>
    /// <remarks>Appends identity and type failures to <paramref name="errors"/> and unknown columns to <paramref name="ignored"/>.</remarks>
    internal static List<ValidMetric> BindMetrics(
        IReadOnlyList<CanonicalMetric> rules,
        ReportSchema effectiveSchema,
        List<ValidationError> errors,
        List<IgnoredItem> ignored)
    {
        var result = new List<ValidMetric>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules)
        {
            if (!SyntheticColumnIdentityValidator.IsValidAuthoredId(rule.Id))
            {
                errors.Add(new ValidationError(
                    rule.SourcePath,
                    $"metric id '{rule.Id}' must be a stable synthetic id such as ir1"));
                continue;
            }
            if (!seenIds.Add(rule.Id))
            {
                errors.Add(new ValidationError(
                    rule.SourcePath,
                    $"duplicate metric id '{rule.Id}'"));
                continue;
            }
            if (effectiveSchema.Lookup.ContainsKey(rule.Id))
            {
                errors.Add(new ValidationError(
                    rule.SourcePath,
                    $"metric id '{rule.Id}' shadows a schema column"));
                continue;
            }
            if (!effectiveSchema.TryGetValue(rule.Column, out var column))
            {
                ignored.Add(new IgnoredItem(
                    "metric",
                    $"unknown column '{rule.Column}'"));
                continue;
            }
            if (!AggregateCatalog.IsCompatible(column.Kind, rule.Function))
            {
                var function = System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(
                    rule.Function.ToString());
                errors.Add(new ValidationError(
                    rule.SourcePath,
                    $"aggregate '{function}' is not valid for {column.KindName} column '{column.Name}'"));
                continue;
            }
            result.Add(new ValidMetric(rule.Id, column, rule.Function));
        }
        return result;
    }

    /// <summary>
    /// Applies stricter rules than grid aggregation: the metric must be numeric, while a bare value column
    /// must itself be numeric.
    /// </summary>
    /// <param name="shape">The canonical chart declaration and its source path.</param>
    /// <param name="columns">The source relation columns available to the chart.</param>
    /// <param name="errors">The validation list that receives invalid chart type, role, column, and aggregation errors.</param>
    /// <returns>The fully bound chart, or <see langword="null"/> when any chart property is invalid.</returns>
    /// <remarks>Appends all independently detectable chart errors before returning.</remarks>
    internal static ValidChart? BindChart(
        CanonicalChartShape shape,
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
                $"{shape.Path}.type",
                $"chart type must be 'bar', 'line', 'area', or 'pie', got '{shape.Type}'"));

        ColumnModel? label = null;
        if (string.IsNullOrWhiteSpace(shape.Label))
            errors.Add(new ValidationError(
                $"{shape.Path}.label",
                "a chart stage requires a label column"));
        else if (!columns.TryGetValue(shape.Label, out label))
            errors.Add(new ValidationError(
                $"{shape.Path}.label",
                $"unknown chart label column '{shape.Label}'"));
        else if (label.Kind == ColumnKind.Other)
        {
            errors.Add(new ValidationError(
                $"{shape.Path}.label",
                $"{label.KindName} column '{label.Name}' cannot label a chart (text, number, date, or bool required)"));
            label = null;
        }

        ColumnModel? value = null;
        if (string.IsNullOrWhiteSpace(shape.Value))
        {
            if (shape.Function != AggregateFn.Count)
                errors.Add(new ValidationError(
                    $"{shape.Path}.value",
                    shape.Function is null
                        ? "a chart stage requires a value column (or fn 'count' to count rows)"
                        : $"chart aggregate '{FunctionName(shape.Function.Value)}' requires a value column ('count' alone counts rows)"));
        }
        else if (!columns.TryGetValue(shape.Value, out value))
        {
            errors.Add(new ValidationError(
                $"{shape.Path}.value",
                $"unknown chart value column '{shape.Value}'"));
        }
        else if (shape.Function is { } function
            && !AggregateCatalog.IsChartCompatible(value.Kind, function))
        {
            errors.Add(new ValidationError(
                $"{shape.Path}.value",
                $"chart values must be numeric: '{FunctionName(function)}' of {value.KindName} column '{value.Name}' does not produce a number"));
        }
        else if (shape.Function is null && value.Kind != ColumnKind.Number)
        {
            errors.Add(new ValidationError(
                $"{shape.Path}.value",
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
                $"{shape.Path}.orientation",
                $"chart orientation must be 'vertical' or 'horizontal', got '{shape.Orientation}'"));

        ChartSortBy? sortBy = shape.Sort?.By?.ToLowerInvariant() switch
        {
            null or "" or "label" => ChartSortBy.Label,
            "value" => ChartSortBy.Value,
            _ => null,
        };
        if (sortBy is null)
            errors.Add(new ValidationError(
                $"{shape.Path}.sort.by",
                $"chart sort must be by 'label' or 'value', got '{shape.Sort?.By}'"));

        if (errors.Count > before) return null;

        return new ValidChart(
            type!.Value,
            label!,
            value,
            shape.Function,
            orientation!.Value,
            sortBy!.Value,
            shape.Sort?.Direction ?? SortDir.Asc,
            NormalizeTitle(shape.LabelAxisTitle),
            NormalizeTitle(shape.ValueAxisTitle));
    }

    /// <summary>
    /// Resolves distinct dimension names against the current schema and ignores unknown entries.
    /// </summary>
    /// <param name="names">The authored dimension names, or <see langword="null"/>.</param>
    /// <param name="description">The dimension role used in ignored-item diagnostics.</param>
    /// <param name="columns">The source relation columns available to the shape.</param>
    /// <param name="ignored">The collection that receives non-fatal ignored-item diagnostics.</param>
    /// <returns>Distinct resolved columns in authored order.</returns>
    /// <remarks>Appends unknown-column diagnostics to <paramref name="ignored"/>.</remarks>
    internal static List<ColumnModel> BindDimensions(
        IEnumerable<string>? names,
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

    /// <summary>
    /// Returns the canonical aggregate-function name.
    /// </summary>
    /// <param name="function">The aggregate function to serialize.</param>
    /// <returns>The canonical function name.</returns>
    private static string FunctionName(AggregateFn function)
        => System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(function.ToString());

    /// <summary>
    /// Trims an optional axis title and collapses blank text to <see langword="null"/>.
    /// </summary>
    /// <param name="title">The authored axis title.</param>
    /// <returns>The trimmed title, or <see langword="null"/> when blank.</returns>
    private static string? NormalizeTitle(string? title)
        => string.IsNullOrWhiteSpace(title) ? null : title.Trim();
}
