using System.Text.Json;
using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Validation;

/// <summary>Validates aggregate compatibility and removes duplicate column/function pairs.</summary>
internal static class AggregateRuleValidator
{
    public static List<ValidAggregate> Validate(
        List<AggregateRule>? rules,
        string pathPrefix,
        IReadOnlyDictionary<string, ColumnModel> columns,
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
            if (!columns.TryGetValue(rule.Col, out var column))
            {
                ignored.Add(new IgnoredItem("aggregate", $"unknown column '{rule.Col}'"));
                continue;
            }

            if (!AggregateCatalog.IsCompatible(column.Kind, rule.Fn))
            {
                var function = JsonNamingPolicy.CamelCase.ConvertName(rule.Fn.ToString());
                errors.Add(new ValidationError(
                    path,
                    $"aggregate '{function}' is not valid for {column.KindName} column '{column.Name}'"));
                continue;
            }

            if (seen.Add((column.Name, rule.Fn)))
                result.Add(new ValidAggregate(column, rule.Fn));
        }
        return result;
    }
}
