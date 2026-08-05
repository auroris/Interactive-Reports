using System.Globalization;
using System.Text.Json;
using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Validation;

/// <summary>
/// Turns a raw state document into a ValidatedState against the discovered schema.
/// Policy: elements referencing unknown columns are dropped into ignored[] (saved-report
/// resilience); structurally wrong requests (bad arity, untypeable values, text operators
/// on non-text columns) are precise validation errors.
/// </summary>
public static class StateValidator
{
    private const int MaxInListValues = 1000;

    public static ValidatedState Validate(ReportDefinition def, ReportState state, IReadOnlyList<ColumnModel> schema)
    {
        var errors = new List<ValidationError>();
        var ignored = new List<IgnoredItem>();
        var byName = schema.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

        var defaults = def.DefaultState;

        var filters = ValidateFilters(state.Filters, byName, errors, ignored);
        var sorts = ValidateSorts(state.Sorts is { Count: > 0 } ? state.Sorts : defaults?.Sorts, byName, ignored);
        var columns = ValidateColumns(state.Columns is { Count: > 0 } ? state.Columns : defaults?.Columns, schema, byName, ignored);
        var aggregates = ValidateAggregates(state.Aggregates ?? defaults?.Aggregates, byName, errors, ignored);
        var breaks = ValidateBreaks(state.Breaks ?? defaults?.Breaks, byName, ignored);

        // Break columns must be selected — renderers group page rows by their values.
        foreach (var b in breaks)
        {
            if (!columns.Any(c => string.Equals(c.Name, b.Name, StringComparison.OrdinalIgnoreCase)))
                columns.Add(b);
        }

        var search = string.IsNullOrWhiteSpace(state.Search) ? null : state.Search.Trim();
        if (search is not null && !columns.Any(c => c.Kind == ColumnKind.Text))
        {
            ignored.Add(new IgnoredItem("search", "no visible text columns to search"));
            search = null;
        }

        NoteNotImplemented(state, ignored);

        var (pageIndex, pageSize) = ClampPage(state.Page, def);

        if (errors.Count > 0)
            throw new ReportValidationException(errors);

        return new ValidatedState
        {
            Filters = filters,
            Search = search,
            Sorts = sorts,
            SelectColumns = columns,
            Aggregates = aggregates,
            Breaks = breaks,
            PageIndex = pageIndex,
            PageSize = pageSize,
            Ignored = ignored,
        };
    }

    private static List<ValidAggregate> ValidateAggregates(
        List<AggregateRule>? rules,
        Dictionary<string, ColumnModel> byName,
        List<ValidationError> errors,
        List<IgnoredItem> ignored)
    {
        var result = new List<ValidAggregate>();
        if (rules is null) return result;

        var seen = new HashSet<(string, AggregateFn)>();
        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            var path = $"aggregates[{i}]";

            if (!byName.TryGetValue(rule.Col, out var col))
            {
                ignored.Add(new IgnoredItem("aggregate", $"unknown column '{rule.Col}'"));
                continue;
            }

            var ok = rule.Fn switch
            {
                AggregateFn.Sum or AggregateFn.Avg => col.Kind == ColumnKind.Number,
                AggregateFn.Min or AggregateFn.Max =>
                    col.Kind is ColumnKind.Number or ColumnKind.Date or ColumnKind.Text,
                AggregateFn.Count or AggregateFn.CountDistinct => true,
                _ => false,
            };
            if (!ok)
            {
                errors.Add(new ValidationError(path,
                    $"aggregate '{JsonNamingPolicy.CamelCase.ConvertName(rule.Fn.ToString())}' is not valid for {col.KindName} column '{col.Name}'"));
                continue;
            }

            if (seen.Add((col.Name, rule.Fn)))
                result.Add(new ValidAggregate(col, rule.Fn));
        }
        return result;
    }

    private static List<ColumnModel> ValidateBreaks(
        List<string>? requested,
        Dictionary<string, ColumnModel> byName,
        List<IgnoredItem> ignored)
    {
        var result = new List<ColumnModel>();
        if (requested is null) return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in requested)
        {
            if (!byName.TryGetValue(name, out var col))
            {
                ignored.Add(new IgnoredItem("break", $"unknown column '{name}'"));
                continue;
            }
            if (seen.Add(col.Name))
                result.Add(col);
        }
        return result;
    }

    private static (int Index, int Size) ClampPage(PageRequest? page, ReportDefinition def)
    {
        var size = page?.Size ?? def.DefaultPageSize;
        size = Math.Clamp(size, 1, def.MaxPageSize);
        var index = Math.Max(1, page?.Index ?? 1);
        return (index, size);
    }

    private static List<ValidFilter> ValidateFilters(
        List<FilterRule>? rules,
        Dictionary<string, ColumnModel> byName,
        List<ValidationError> errors,
        List<IgnoredItem> ignored)
    {
        var result = new List<ValidFilter>();
        if (rules is null) return result;

        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            var path = $"filters[{i}]";

            if (!byName.TryGetValue(rule.Col, out var col))
            {
                ignored.Add(new IgnoredItem("filter", $"unknown column '{rule.Col}'"));
                continue;
            }

            var valid = ValidateFilter(rule, col, path, errors);
            if (valid is not null) result.Add(valid);
        }

        return result;
    }

    private static ValidFilter? ValidateFilter(FilterRule rule, ColumnModel col, string path, List<ValidationError> errors)
    {
        switch (rule.Op)
        {
            case FilterOp.Blank:
            case FilterOp.Nblank:
                return new ValidFilter(col, rule.Op);

            case FilterOp.Contains:
            case FilterOp.Ncontains:
            case FilterOp.Starts:
            case FilterOp.Ends:
            {
                if (col.Kind != ColumnKind.Text)
                {
                    errors.Add(new ValidationError(path, $"operator '{OpName(rule.Op)}' requires a text column; '{col.Name}' is {col.KindName}"));
                    return null;
                }
                var s = RequireScalar(rule, col, path, errors) as string;
                if (s is null) return null;
                if (s.Length == 0)
                {
                    errors.Add(new ValidationError(path, "text-match value must be non-empty (use 'blank' to match empty)"));
                    return null;
                }
                return new ValidFilter(col, rule.Op, s);
            }

            case FilterOp.Between:
            {
                if (rule.Value is not { ValueKind: JsonValueKind.Array } arr || arr.GetArrayLength() != 2)
                {
                    errors.Add(new ValidationError(path, "'between' requires a two-element array value"));
                    return null;
                }
                var items = arr.EnumerateArray().ToArray();
                var lo = ConvertScalar(items[0], col, path, errors);
                var hi = ConvertScalar(items[1], col, path, errors);
                if (lo is null || hi is null) return null;
                return new ValidFilter(col, rule.Op, lo, hi);
            }

            case FilterOp.In:
            case FilterOp.Nin:
            {
                if (rule.Value is not { ValueKind: JsonValueKind.Array } arr || arr.GetArrayLength() == 0)
                {
                    errors.Add(new ValidationError(path, $"'{OpName(rule.Op)}' requires a non-empty array value"));
                    return null;
                }
                if (arr.GetArrayLength() > MaxInListValues)
                {
                    errors.Add(new ValidationError(path, $"'{OpName(rule.Op)}' list exceeds {MaxInListValues} values"));
                    return null;
                }
                var values = new List<object>();
                foreach (var item in arr.EnumerateArray())
                {
                    var v = ConvertScalar(item, col, path, errors);
                    if (v is null) return null;
                    values.Add(v);
                }
                return new ValidFilter(col, rule.Op, Values: values);
            }

            case FilterOp.Eq:
            case FilterOp.Ne:
            case FilterOp.Lt:
            case FilterOp.Le:
            case FilterOp.Gt:
            case FilterOp.Ge:
            {
                if (col.Kind == ColumnKind.Bool && rule.Op is not (FilterOp.Eq or FilterOp.Ne))
                {
                    errors.Add(new ValidationError(path, $"operator '{OpName(rule.Op)}' is not valid for bool column '{col.Name}'"));
                    return null;
                }
                var v = RequireScalar(rule, col, path, errors);
                if (v is null) return null;
                return new ValidFilter(col, rule.Op, v);
            }

            default:
                errors.Add(new ValidationError(path, $"unsupported operator '{rule.Op}'"));
                return null;
        }
    }

    private static object? RequireScalar(FilterRule rule, ColumnModel col, string path, List<ValidationError> errors)
    {
        if (rule.Value is not { } el || el.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            errors.Add(new ValidationError(path, $"operator '{OpName(rule.Op)}' requires a value (use 'blank'/'nblank' for null tests)"));
            return null;
        }
        if (el.ValueKind == JsonValueKind.Array)
        {
            errors.Add(new ValidationError(path, $"operator '{OpName(rule.Op)}' takes a scalar value, not an array"));
            return null;
        }
        return ConvertScalar(el, col, path, errors);
    }

    /// <summary>Converts a JSON scalar to the column's CLR family; adds a precise error and returns null on mismatch.</summary>
    private static object? ConvertScalar(JsonElement el, ColumnModel col, string path, List<ValidationError> errors)
    {
        try
        {
            switch (col.Kind)
            {
                case ColumnKind.Text:
                    return el.ValueKind == JsonValueKind.String
                        ? el.GetString()!
                        : el.GetRawText(); // numbers/bools against text columns compare as their literal text

                case ColumnKind.Number:
                    if (el.ValueKind == JsonValueKind.Number) return el.GetDecimal();
                    if (el.ValueKind == JsonValueKind.String
                        && decimal.TryParse(el.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var dec))
                        return dec;
                    break;

                case ColumnKind.Date:
                    if (el.ValueKind == JsonValueKind.String
                        && DateTime.TryParse(el.GetString(), CultureInfo.InvariantCulture,
                            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out var dt))
                        return dt;
                    break;

                case ColumnKind.Bool:
                    if (el.ValueKind is JsonValueKind.True or JsonValueKind.False) return el.GetBoolean();
                    break;

                default:
                    if (el.ValueKind == JsonValueKind.String) return el.GetString()!;
                    if (el.ValueKind == JsonValueKind.Number) return el.GetDecimal();
                    break;
            }
        }
        catch (FormatException)
        {
            // fall through to the error below
        }

        errors.Add(new ValidationError(path, $"value {el.GetRawText()} is not valid for {col.KindName} column '{col.Name}'"));
        return null;
    }

    private static List<ValidSort> ValidateSorts(
        List<SortRule>? sorts,
        Dictionary<string, ColumnModel> byName,
        List<IgnoredItem> ignored)
    {
        var result = new List<ValidSort>();
        if (sorts is null) return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sorts)
        {
            if (!byName.TryGetValue(s.Col, out var col))
            {
                ignored.Add(new IgnoredItem("sort", $"unknown column '{s.Col}'"));
                continue;
            }
            if (seen.Add(col.Name))
                result.Add(new ValidSort(col, s.Dir));
        }
        return result;
    }

    private static List<ColumnModel> ValidateColumns(
        List<string>? requested,
        IReadOnlyList<ColumnModel> schema,
        Dictionary<string, ColumnModel> byName,
        List<IgnoredItem> ignored)
    {
        if (requested is null || requested.Count == 0)
            return schema.ToList();

        var result = new List<ColumnModel>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in requested)
        {
            if (!byName.TryGetValue(name, out var col))
            {
                ignored.Add(new IgnoredItem("column", $"unknown column '{name}'"));
                continue;
            }
            if (seen.Add(col.Name))
                result.Add(col);
        }

        return result.Count > 0 ? result : schema.ToList();
    }

    private static void NoteNotImplemented(ReportState state, List<IgnoredItem> ignored)
    {
        if (state.Computed is { Count: > 0 })
            ignored.Add(new IgnoredItem("not-implemented", "computed columns arrive in M3"));
        if (state.Highlights is { Count: > 0 })
            ignored.Add(new IgnoredItem("not-implemented", "highlights arrive in M3"));
        if (state.View is { } v && !string.Equals(v.Mode, "grid", StringComparison.OrdinalIgnoreCase))
            ignored.Add(new IgnoredItem("not-implemented", $"view mode '{v.Mode}' arrives in M4"));
    }

    private static string OpName(FilterOp op) => JsonNamingPolicy.CamelCase.ConvertName(op.ToString());
}
