using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Validation;

/// <summary>
/// Turns a raw state document into a ValidatedState against the discovered schema.
/// Policy: elements referencing unknown columns are dropped into ignored[] (saved-report
/// resilience); structurally wrong requests (bad arity, untypeable values, text operators
/// on non-text columns) are precise validation errors.
/// </summary>
public static partial class StateValidator
{
    private const int MaxInListValues = 1000;

    public static ValidatedState Validate(ReportDefinition def, ReportState state, IReadOnlyList<ColumnModel> schema)
    {
        var errors = new List<ValidationError>();
        var ignored = new List<IgnoredItem>();
        var byName = schema.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

        var defaults = def.DefaultState;

        // Computed columns validate first against the BASE schema, then join the
        // effective schema — everything after this line treats them as ordinary columns.
        var computed = ValidateComputed(state.Computed ?? defaults?.Computed, byName, errors);
        var effectiveSchema = schema.Concat(computed.Select(c => c.Column)).ToList();
        foreach (var c in computed)
            byName[c.Column.Name] = c.Column;

        var filters = ValidateFilters(state.Filters, byName, errors, ignored);
        var sorts = ValidateSorts(state.Sorts is { Count: > 0 } ? state.Sorts : defaults?.Sorts, byName, ignored);
        var columns = ValidateColumns(state.Columns is { Count: > 0 } ? state.Columns : defaults?.Columns, effectiveSchema, byName, ignored);
        var aggregates = ValidateAggregates(state.Aggregates ?? defaults?.Aggregates, "aggregates", byName, errors, ignored);
        var breaks = ValidateBreaks(state.Breaks ?? defaults?.Breaks, byName, ignored);
        var highlights = ValidateHighlights(state.Highlights ?? defaults?.Highlights, byName, errors, ignored);
        var view = ValidateView(state.View ?? defaults?.View, byName, errors, ignored);

        if (view.Mode != ViewMode.Grid)
        {
            // Alternate views present aggregated rows; grid-only features are noted, not fatal.
            if (breaks.Count > 0)
            {
                ignored.Add(new IgnoredItem("view", "control breaks apply to the grid view only"));
                breaks = [];
            }
            if (highlights.Count > 0)
            {
                ignored.Add(new IgnoredItem("view", "highlights apply to the grid view only"));
                highlights = [];
            }
            if (aggregates.Count > 0)
            {
                ignored.Add(new IgnoredItem("view", "grid aggregates are ignored in alternate views (use view.values)"));
                aggregates = [];
            }

            if (view.Mode == ViewMode.GroupBy)
            {
                var dims = new HashSet<string>(view.GroupBy.Select(g => g.Name), StringComparer.OrdinalIgnoreCase);
                var kept = sorts.Where(s => dims.Contains(s.Column.Name)).ToList();
                if (kept.Count != sorts.Count)
                    ignored.Add(new IgnoredItem("view", "sorts on non-grouped columns are ignored in groupBy view"));
                sorts = kept;
            }
            else if (sorts.Count > 0)
            {
                ignored.Add(new IgnoredItem("view", "pivot view orders by its dimensions; sorts are ignored"));
                sorts = [];
            }
        }

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

        var (pageIndex, pageSize) = ClampPage(state.Page, def);

        if (errors.Count > 0)
            throw new ReportValidationException(errors);

        return new ValidatedState
        {
            Filters = filters,
            Search = search,
            Sorts = sorts,
            SelectColumns = columns,
            Computed = computed,
            Highlights = highlights,
            Aggregates = aggregates,
            Breaks = breaks,
            View = view,
            PageIndex = pageIndex,
            PageSize = pageSize,
            Ignored = ignored,
        };
    }

    private static ValidView ValidateView(
        ViewSpec? spec,
        Dictionary<string, ColumnModel> byName,
        List<ValidationError> errors,
        List<IgnoredItem> ignored)
    {
        if (spec is null || string.Equals(spec.Mode, "grid", StringComparison.OrdinalIgnoreCase))
            return ValidView.Grid;

        List<ColumnModel> ResolveDims(List<string>? names, string what)
        {
            var result = new List<ColumnModel>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in names ?? [])
            {
                if (!byName.TryGetValue(name, out var col))
                {
                    ignored.Add(new IgnoredItem("view", $"unknown {what} column '{name}'"));
                    continue;
                }
                if (seen.Add(col.Name)) result.Add(col);
            }
            return result;
        }

        if (string.Equals(spec.Mode, "groupBy", StringComparison.OrdinalIgnoreCase))
        {
            var dims = ResolveDims(spec.GroupBy, "groupBy");
            if (dims.Count == 0)
            {
                errors.Add(new ValidationError("view.groupBy", "groupBy view requires at least one valid group column"));
                return ValidView.Grid;
            }
            var values = ValidateAggregates(spec.Values, "view.values", byName, errors, ignored);
            return new ValidView(ViewMode.GroupBy, dims, [], [], values);
        }

        if (string.Equals(spec.Mode, "pivot", StringComparison.OrdinalIgnoreCase))
        {
            var rows = ResolveDims(spec.Rows, "pivot row");
            var cols = ResolveDims(spec.Cols, "pivot column");
            if (rows.Count == 0)
                errors.Add(new ValidationError("view.rows", "pivot view requires at least one valid row dimension"));
            if (cols.Count == 0)
                errors.Add(new ValidationError("view.cols", "pivot view requires at least one valid column dimension"));
            var overlap = rows.Select(r => r.Name)
                .Intersect(cols.Select(c => c.Name), StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (overlap is not null)
                errors.Add(new ValidationError("view", $"'{overlap}' cannot be both a pivot row and a pivot column"));
            if (rows.Count == 0 || cols.Count == 0 || overlap is not null)
                return ValidView.Grid;

            var values = ValidateAggregates(spec.Values, "view.values", byName, errors, ignored);
            return new ValidView(ViewMode.Pivot, [], rows, cols, values);
        }

        errors.Add(new ValidationError("view.mode", $"view mode must be 'grid', 'groupBy', or 'pivot', got '{spec.Mode}'"));
        return ValidView.Grid;
    }

    private static List<ValidComputed> ValidateComputed(
        List<Model.ComputedColumn>? rules,
        Dictionary<string, ColumnModel> baseSchema,
        List<ValidationError> errors)
    {
        var result = new List<ValidComputed>();
        if (rules is null) return result;

        if (rules.Count > 20)
        {
            errors.Add(new ValidationError("computed", "at most 20 computed columns per report state"));
            return result;
        }

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            var path = $"computed[{i}]";

            if (!ComputedIdPattern().IsMatch(rule.Id))
            {
                errors.Add(new ValidationError(path, $"computed column id '{rule.Id}' must match c1, c2, … (lowercase c + digits)"));
                continue;
            }
            if (!seenIds.Add(rule.Id))
            {
                errors.Add(new ValidationError(path, $"duplicate computed column id '{rule.Id}'"));
                continue;
            }
            if (baseSchema.ContainsKey(rule.Id))
            {
                errors.Add(new ValidationError(path, $"computed column id '{rule.Id}' shadows a schema column"));
                continue;
            }

            var (ast, error) = Expressions.ExprParser.Parse(rule.Expr, baseSchema);
            if (ast is null)
            {
                errors.Add(new ValidationError($"{path}.expr", error!));
                continue;
            }

            var clrType = ast.Kind switch
            {
                ColumnKind.Number => typeof(decimal),
                ColumnKind.Date => typeof(DateTime),
                _ => typeof(string),
            };
            result.Add(new ValidComputed(new ColumnModel
            {
                Name = rule.Id,
                Label = string.IsNullOrWhiteSpace(rule.Label) ? rule.Id : rule.Label.Trim(),
                ClrType = clrType,
                IsComputed = true,
            }, ast));
        }
        return result;
    }

    private static List<ValidHighlight> ValidateHighlights(
        List<HighlightRule>? rules,
        Dictionary<string, ColumnModel> byName,
        List<ValidationError> errors,
        List<IgnoredItem> ignored)
    {
        var result = new List<ValidHighlight>();
        if (rules is null) return result;

        if (rules.Count > 50)
        {
            errors.Add(new ValidationError("highlights", "at most 50 highlight rules per report state"));
            return result;
        }

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            var path = $"highlights[{i}]";

            if (string.IsNullOrWhiteSpace(rule.Id))
            {
                errors.Add(new ValidationError(path, "highlight id is required"));
                continue;
            }
            if (!seenIds.Add(rule.Id))
            {
                errors.Add(new ValidationError(path, $"duplicate highlight id '{rule.Id}'"));
                continue;
            }

            HighlightScope scope;
            if (string.Equals(rule.Scope, "row", StringComparison.OrdinalIgnoreCase)) scope = HighlightScope.Row;
            else if (string.Equals(rule.Scope, "cell", StringComparison.OrdinalIgnoreCase)) scope = HighlightScope.Cell;
            else
            {
                errors.Add(new ValidationError(path, $"scope must be 'row' or 'cell', got '{rule.Scope}'"));
                continue;
            }

            ColumnModel? cellCol = null;
            if (scope == HighlightScope.Cell)
            {
                if (rule.Col is null || !byName.TryGetValue(rule.Col, out cellCol))
                {
                    ignored.Add(new IgnoredItem("highlight", $"'{rule.Id}': unknown cell column '{rule.Col}'"));
                    continue;
                }
            }

            if (rule.Condition is null)
            {
                errors.Add(new ValidationError(path, "highlight condition is required"));
                continue;
            }
            if (!byName.TryGetValue(rule.Condition.Col, out var condCol))
            {
                ignored.Add(new IgnoredItem("highlight", $"'{rule.Id}': unknown condition column '{rule.Condition.Col}'"));
                continue;
            }

            var condition = ValidateFilter(rule.Condition, condCol, $"{path}.condition", errors);
            if (condition is null) continue;

            result.Add(new ValidHighlight(rule.Id, scope, cellCol, condition));
        }
        return result;
    }

    [GeneratedRegex(@"^c\d+$")]
    private static partial Regex ComputedIdPattern();

    private static List<ValidAggregate> ValidateAggregates(
        List<AggregateRule>? rules,
        string pathPrefix,
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
            var path = $"{pathPrefix}[{i}]";

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


    private static string OpName(FilterOp op) => JsonNamingPolicy.CamelCase.ConvertName(op.ToString());
}
