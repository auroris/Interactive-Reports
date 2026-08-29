using System.Text.Json;
using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Validation;

/// <summary>Validates aggregate compatibility and removes duplicate column/function pairs.</summary>
internal static class AggregateRuleValidator
{
    internal sealed class Context
    {
        public HashSet<string> SeenKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public static List<ValidAggregate> Validate(
        List<AggregateRule>? rules,
        string pathPrefix,
        IReadOnlyDictionary<string, ColumnModel> columns,
        List<ValidationError> errors,
        List<IgnoredItem> ignored,
        Context? context = null)
    {
        var result = new List<ValidAggregate>();
        if (rules is null) return result;

        context ??= new Context();
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

            if (context.SeenKeys.Add($"{column.Name}\0{rule.Fn}"))
                result.Add(new ValidAggregate(column, rule.Fn));
        }
        return result;
    }
}
