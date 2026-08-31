using InteractiveReport.Core.Expressions;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Planning;

namespace InteractiveReport.Core.Validation;

/// <summary>Validates computed-column identity and prepares the effect created after expression binding.</summary>
internal static class ComputedColumnValidator
{
    /// <summary>
    /// Validates the non-expression portion of one canonical computed-column node. The
    /// expression is bound separately against the planner's current schema.
    /// </summary>
    /// <param name="id">The authored synthetic column identifier.</param>
    /// <param name="label">The optional display label; blank values fall back to <paramref name="id"/>.</param>
    /// <param name="baseSchema">The current stage's base columns, which the synthetic id may not shadow.</param>
    /// <param name="seenIds">The document-wide authored synthetic ids already reserved.</param>
    /// <param name="errors">The validation list that receives invalid, duplicate, or shadowing identifier errors.</param>
    /// <param name="rulePath">The computed rule path to attach to identity errors.</param>
    /// <returns>A callback that maps the subsequently bound expression to its column-definition effect, or <see langword="null"/> when identity validation fails.</returns>
    /// <remarks>Reserves a valid new id in <paramref name="seenIds"/> and appends identity failures to <paramref name="errors"/>.</remarks>
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
