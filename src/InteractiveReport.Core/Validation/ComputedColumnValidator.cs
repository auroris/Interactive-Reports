using InteractiveReport.Core.Expressions;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Planning;

namespace InteractiveReport.Core.Validation;

/// <summary>Validates computed-column identity, expression syntax, and inferred result type.</summary>
internal static class ComputedColumnValidator
{
    /// <summary>
    /// Validates the non-expression portion of one canonical computed-column node.
    /// The expression is bound separately against the planner's current schema.
    /// </summary>
    internal static Func<BoundExpression, DefineColumnEffect>? PrepareEffect(
        string id,
        string? label,
        IReadOnlyDictionary<string, ColumnModel> baseSchema,
        HashSet<string> seenIds,
        List<ValidationError> errors,
        string rulePath)
    {
        if (!SyntheticColumnIdentityValidator.IsValidAuthoredId(id))
        {
            errors.Add(new ValidationError(
                rulePath,
                $"computed column id '{id}' must be a stable synthetic id such as ir1"));
            return null;
        }
        if (!seenIds.Add(id))
        {
            errors.Add(new ValidationError(rulePath, $"duplicate computed column id '{id}'"));
            return null;
        }
        if (baseSchema.ContainsKey(id))
        {
            errors.Add(new ValidationError(
                rulePath,
                $"computed column id '{id}' shadows a column of this stage"));
            return null;
        }

        return expression => new DefineColumnEffect(new ColumnModel
        {
            Name = id,
            Label = string.IsNullOrWhiteSpace(label) ? id : label.Trim(),
            ClrType = expression.Kind switch
            {
                ColumnKind.Number => typeof(decimal),
                ColumnKind.Date => typeof(DateTime),
                _ => typeof(string),
            },
            IsComputed = true,
        });
    }
}
