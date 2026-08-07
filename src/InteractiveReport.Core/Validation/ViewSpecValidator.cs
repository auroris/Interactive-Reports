using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Validation;

/// <summary>Validates alternate-view dimensions and aggregate values.</summary>
internal static class ViewSpecValidator
{
    public static ValidView Validate(
        ViewSpec? specification,
        IReadOnlyDictionary<string, ColumnModel> columns,
        List<ValidationError> errors,
        List<IgnoredItem> ignored)
    {
        if (specification is null
            || string.Equals(specification.Mode, "grid", StringComparison.OrdinalIgnoreCase))
            return ValidView.Grid;

        if (string.Equals(specification.Mode, "groupBy", StringComparison.OrdinalIgnoreCase))
        {
            var dimensions = ResolveDimensions(
                specification.GroupBy,
                "groupBy",
                columns,
                ignored);
            if (dimensions.Count == 0)
            {
                errors.Add(new ValidationError(
                    "view.groupBy",
                    "groupBy view requires at least one valid group column"));
                return ValidView.Grid;
            }
            var values = AggregateRuleValidator.Validate(
                specification.Values,
                "view.values",
                columns,
                errors,
                ignored);
            return new ValidView(ViewMode.GroupBy, dimensions, [], [], values);
        }

        if (string.Equals(specification.Mode, "pivot", StringComparison.OrdinalIgnoreCase))
        {
            var rows = ResolveDimensions(specification.Rows, "pivot row", columns, ignored);
            var pivotColumns = ResolveDimensions(
                specification.Cols,
                "pivot column",
                columns,
                ignored);
            if (rows.Count == 0)
                errors.Add(new ValidationError(
                    "view.rows",
                    "pivot view requires at least one valid row dimension"));
            if (pivotColumns.Count == 0)
                errors.Add(new ValidationError(
                    "view.cols",
                    "pivot view requires at least one valid column dimension"));

            var overlap = rows.Select(row => row.Name)
                .Intersect(pivotColumns.Select(column => column.Name), StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (overlap is not null)
                errors.Add(new ValidationError(
                    "view",
                    $"'{overlap}' cannot be both a pivot row and a pivot column"));
            if (rows.Count == 0 || pivotColumns.Count == 0 || overlap is not null)
                return ValidView.Grid;

            var values = AggregateRuleValidator.Validate(
                specification.Values,
                "view.values",
                columns,
                errors,
                ignored);
            return new ValidView(ViewMode.Pivot, [], rows, pivotColumns, values);
        }

        if (string.Equals(specification.Mode, "chart", StringComparison.OrdinalIgnoreCase))
            return ValidateChart(specification, columns, errors);

        errors.Add(new ValidationError(
            "view.mode",
            $"view mode must be 'grid', 'groupBy', 'pivot', or 'chart', got '{specification.Mode}'"));
        return ValidView.Grid;
    }

    /// <summary>
    /// Chart validation is stricter than grid aggregation: the metric must come out
    /// numeric, so min/max only chart number columns and a bare (fn-less) value column
    /// must itself be a number. All problems are precise errors — a chart with a broken
    /// spec has no degraded rendering the way a grid with a dropped column does.
    /// </summary>
    private static ValidView ValidateChart(
        ViewSpec specification,
        IReadOnlyDictionary<string, ColumnModel> columns,
        List<ValidationError> errors)
    {
        var before = errors.Count;

        ChartType? type = specification.Type?.ToLowerInvariant() switch
        {
            "bar" => ChartType.Bar,
            "line" => ChartType.Line,
            "area" => ChartType.Area,
            "pie" => ChartType.Pie,
            _ => null,
        };
        if (type is null)
            errors.Add(new ValidationError(
                "view.type",
                $"chart type must be 'bar', 'line', 'area', or 'pie', got '{specification.Type}'"));

        ColumnModel? label = null;
        if (string.IsNullOrWhiteSpace(specification.Label))
            errors.Add(new ValidationError("view.label", "chart view requires a label column"));
        else if (!columns.TryGetValue(specification.Label, out label))
            errors.Add(new ValidationError(
                "view.label",
                $"unknown chart label column '{specification.Label}'"));
        else if (label.Kind == ColumnKind.Other)
        {
            errors.Add(new ValidationError(
                "view.label",
                $"{label.KindName} column '{label.Name}' cannot label a chart (text, number, date, or bool required)"));
            label = null;
        }

        var fn = specification.Fn;
        ColumnModel? value = null;
        if (string.IsNullOrWhiteSpace(specification.Value))
        {
            if (fn != AggregateFn.Count)
                errors.Add(new ValidationError(
                    "view.value",
                    fn is null
                        ? "chart view requires a value column (or fn 'count' to count rows)"
                        : $"chart aggregate '{FnName(fn.Value)}' requires a value column ('count' alone counts rows)"));
        }
        else if (!columns.TryGetValue(specification.Value, out value))
        {
            errors.Add(new ValidationError(
                "view.value",
                $"unknown chart value column '{specification.Value}'"));
        }
        else if (fn is { } f && !AggregateCatalog.IsChartCompatible(value.Kind, f))
        {
            errors.Add(new ValidationError(
                "view.value",
                $"chart values must be numeric: '{FnName(f)}' of {value.KindName} column '{value.Name}' does not produce a number"));
        }
        else if (fn is null && value.Kind != ColumnKind.Number)
        {
            errors.Add(new ValidationError(
                "view.value",
                $"charting one point per row requires a number value column; '{value.Name}' is {value.KindName}"));
        }

        ChartOrientation? orientation = specification.Orientation?.ToLowerInvariant() switch
        {
            null or "" or "vertical" => ChartOrientation.Vertical,
            "horizontal" => ChartOrientation.Horizontal,
            _ => null,
        };
        if (orientation is null)
            errors.Add(new ValidationError(
                "view.orientation",
                $"chart orientation must be 'vertical' or 'horizontal', got '{specification.Orientation}'"));

        ChartSortBy? sortBy = specification.Sort?.By?.ToLowerInvariant() switch
        {
            null or "" or "label" => ChartSortBy.Label,
            "value" => ChartSortBy.Value,
            _ => null,
        };
        if (sortBy is null)
            errors.Add(new ValidationError(
                "view.sort.by",
                $"chart sort must be by 'label' or 'value', got '{specification.Sort!.By}'"));

        if (errors.Count > before)
            return ValidView.Grid;

        return new ValidView(ViewMode.Chart, [], [], [], [], new ValidChart(
            type!.Value,
            label!,
            value,
            fn,
            orientation!.Value,
            sortBy!.Value,
            specification.Sort?.Dir ?? SortDir.Asc,
            NormalizeTitle(specification.LabelAxisTitle),
            NormalizeTitle(specification.ValueAxisTitle)));
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
}
