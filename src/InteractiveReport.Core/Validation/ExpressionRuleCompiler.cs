using InteractiveReport.Core.Expressions;
using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Validation;

/// <summary>
/// Shared rule pipeline: enforce the collection limit, discard disabled instructions,
/// validate effect-specific metadata, parse and bind the expression, enforce its result
/// contract, and pair the bound expression with its effect.
/// </summary>
internal static class ExpressionRuleCompiler
{
    public static List<CompiledRule<TEffect>> Compile<TRule, TEffect>(
        List<TRule>? rules,
        int maxRules,
        string collectionPath,
        IReadOnlyDictionary<string, ColumnModel> schema,
        ExpressionRequirement requirement,
        Func<TRule, int, Func<BoundExpression, TEffect>?> prepareEffect,
        List<ValidationError> errors)
        where TRule : ExpressionRule
        where TEffect : RuleEffect
    {
        var result = new List<CompiledRule<TEffect>>();
        if (rules is null) return result;

        if (rules.Count > maxRules)
        {
            errors.Add(new ValidationError(
                collectionPath,
                $"at most {maxRules} {RuleLabel(collectionPath)} per report state"));
            return result;
        }

        for (var index = 0; index < rules.Count; index++)
        {
            var rule = rules[index];
            if (!rule.Enabled) continue;

            var createEffect = prepareEffect(rule, index);
            if (createEffect is null) continue;

            var (ast, error) = ExprParser.Parse(rule.Expr, schema, requirement);
            if (error is not null)
            {
                errors.Add(new ValidationError($"{collectionPath}[{index}].expr", error));
                continue;
            }

            var expression = new BoundExpression(ast!);
            result.Add(new CompiledRule<TEffect>(expression, createEffect(expression)));
        }

        return result;
    }

    private static string RuleLabel(string collectionPath)
    {
        // Paths are composable-qualified ("tables.base.composables[0].filters"); the trailing
        // segment names the rule collection.
        var segment = collectionPath[(collectionPath.LastIndexOf('.') + 1)..];
        return segment switch
        {
            "computed" => "computed columns",
            "filters" => "filter rules",
            "highlights" => "highlight rules",
            _ => "rules",
        };
    }
}
