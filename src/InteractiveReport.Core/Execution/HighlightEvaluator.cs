using System.Globalization;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Validation;

namespace InteractiveReport.Core.Execution;

/// <summary>
/// Converts database-evaluated highlight projection markers into response hits.
/// Row hits are emitted before cell hits so presentation layers can apply the
/// cell style last, giving it deterministic priority over the row style.
/// </summary>
public static class HighlightEvaluator
{
    public static List<HighlightHit> Evaluate(
        IReadOnlyList<CompiledRule<HighlightEffect>> rules,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        var hits = new List<HighlightHit>();
        var orderedRules = rules
            .OrderBy(rule => rule.Effect.Scope == HighlightScope.Cell ? 1 : 0)
            .ToList();

        for (var r = 0; r < rows.Count; r++)
        {
            foreach (var rule in orderedRules)
            {
                if (MarkerIsTrue(rows[r], rule.Effect.ProjectionName))
                    hits.Add(new HighlightHit(
                        r,
                        rule.Effect.Id,
                        rule.Effect.Scope == HighlightScope.Cell ? rule.Effect.Column!.Name : null));
            }
        }
        return hits;
    }

    private static bool MarkerIsTrue(
        IReadOnlyDictionary<string, object?> row,
        string projectionName)
    {
        if (!row.TryGetValue(projectionName, out var value) || value is null)
            return false;
        if (value is bool boolean) return boolean;

        try
        {
            return Convert.ToDecimal(value, CultureInfo.InvariantCulture) != 0;
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            return false;
        }
    }
}
