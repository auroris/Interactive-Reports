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

        errors.Add(new ValidationError(
            "view.mode",
            $"view mode must be 'grid', 'groupBy', or 'pivot', got '{specification.Mode}'"));
        return ValidView.Grid;
    }

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
