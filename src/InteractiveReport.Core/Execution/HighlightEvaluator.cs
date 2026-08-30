using System.Globalization;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Validation;

namespace InteractiveReport.Core.Execution;

/// <summary>
/// Converts database-evaluated highlight projection markers into response hits.
/// Row hits are emitted before cell hits so presentation layers can apply cell
/// styles last. Within each scope, rules apply from low to high sequence; the
/// higher sequence therefore wins when two matches set the same property.
/// </summary>
internal static class HighlightEvaluator
{
    internal static List<HighlightHit> Evaluate(
        IReadOnlyList<CompiledRule<HighlightEffect>> rules,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        var hits = new List<HighlightHit>();
        var orderedRules = rules
            .OrderBy(rule => rule.Effect.Scope == HighlightScope.Cell ? 1 : 0)
            .ThenBy(rule => rule.Effect.Sequence)
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
