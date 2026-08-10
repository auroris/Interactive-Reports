using System.Text.RegularExpressions;
using InteractiveReport.Core.Expressions;
using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Validation;

/// <summary>Validates computed-column identity, expression syntax, and inferred result type.</summary>
internal static partial class ComputedColumnValidator
{
    public static List<CompiledRule<DefineColumnEffect>> Validate(
        List<ComputedColumn>? rules,
        IReadOnlyDictionary<string, ColumnModel> baseSchema,
        List<ValidationError> errors,
        string collectionPath = "computed")
    {
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return ExpressionRuleCompiler.Compile<ComputedColumn, DefineColumnEffect>(
            rules,
            maxRules: 20,
            collectionPath,
            baseSchema,
            ExpressionRequirement.Value,
            prepareEffect: (rule, index) => PrepareEffect(rule, index, baseSchema, seenIds, errors, collectionPath),
            errors);
    }

    private static Func<BoundExpression, DefineColumnEffect>? PrepareEffect(
        ComputedColumn rule,
        int index,
        IReadOnlyDictionary<string, ColumnModel> baseSchema,
        HashSet<string> seenIds,
        List<ValidationError> errors,
        string collectionPath)
    {
        var path = $"{collectionPath}[{index}]";
        if (!ComputedIdPattern().IsMatch(rule.Id))
        {
            errors.Add(new ValidationError(
                path,
                $"computed column id '{rule.Id}' must match c1, c2, … (lowercase c + digits)"));
            return null;
        }
        if (!seenIds.Add(rule.Id))
        {
            errors.Add(new ValidationError(path, $"duplicate computed column id '{rule.Id}'"));
            return null;
        }
        if (baseSchema.ContainsKey(rule.Id))
        {
            errors.Add(new ValidationError(
                path,
                $"computed column id '{rule.Id}' shadows a column of this stage"));
            return null;
        }

        return expression => new DefineColumnEffect(new ColumnModel
        {
            Name = rule.Id,
            Label = string.IsNullOrWhiteSpace(rule.Label) ? rule.Id : rule.Label.Trim(),
            ClrType = expression.Kind switch
            {
                ColumnKind.Number => typeof(decimal),
                ColumnKind.Date => typeof(DateTime),
                _ => typeof(string),
            },
            IsComputed = true,
        });
    }

    [GeneratedRegex(@"^c\d+$")]
    private static partial Regex ComputedIdPattern();
}
