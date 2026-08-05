using System.Globalization;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Validation;

namespace InteractiveReport.Core.Execution;

/// <summary>
/// Evaluates highlight conditions in C# over the fetched page — highlights are
/// presentation, they never change which rows come back, so they don't push down.
///
/// Semantics track the SQL filter layer where it matters:
/// - NULL fails every predicate except blank/nblank (SQL parity).
/// - blank means null-or-empty-string.
/// - contains/starts/ends are case-insensitive (the operator definition);
///   eq/ne and ordering comparisons on text are ordinal.
/// </summary>
public static class HighlightEvaluator
{
    public static List<HighlightHit> Evaluate(
        IReadOnlyList<ValidHighlight> rules,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        var hits = new List<HighlightHit>();
        for (var r = 0; r < rows.Count; r++)
        {
            foreach (var rule in rules)
            {
                if (Matches(rule.Condition, rows[r]))
                    hits.Add(new HighlightHit(r, rule.Id, rule.Scope == HighlightScope.Cell ? rule.Col!.Name : null));
            }
        }
        return hits;
    }

    internal static bool Matches(ValidFilter cond, IReadOnlyDictionary<string, object?> row)
    {
        row.TryGetValue(cond.Column.Name, out var raw);

        if (cond.Op == FilterOp.Blank) return IsBlank(raw);
        if (cond.Op == FilterOp.Nblank) return !IsBlank(raw);
        if (raw is null) return false;   // SQL parity: NULL fails every other predicate

        return cond.Column.Kind switch
        {
            ColumnKind.Number => MatchNumber(cond, Convert.ToDecimal(raw, CultureInfo.InvariantCulture)),
            ColumnKind.Date => MatchDate(cond, AsDate(raw)),
            ColumnKind.Bool => MatchBool(cond, Convert.ToBoolean(raw, CultureInfo.InvariantCulture)),
            _ => MatchText(cond, Convert.ToString(raw, CultureInfo.InvariantCulture) ?? ""),
        };
    }

    private static bool IsBlank(object? raw) => raw is null || raw is string { Length: 0 };

    private static bool MatchNumber(ValidFilter cond, decimal value) => cond.Op switch
    {
        FilterOp.Eq => value == D(cond.Value),
        FilterOp.Ne => value != D(cond.Value),
        FilterOp.Lt => value < D(cond.Value),
        FilterOp.Le => value <= D(cond.Value),
        FilterOp.Gt => value > D(cond.Value),
        FilterOp.Ge => value >= D(cond.Value),
        FilterOp.Between => value >= D(cond.Value) && value <= D(cond.Value2),
        FilterOp.In => cond.Values!.Any(v => value == D(v)),
        FilterOp.Nin => cond.Values!.All(v => value != D(v)),
        _ => false,
    };

    private static bool MatchDate(ValidFilter cond, DateTime value) => cond.Op switch
    {
        FilterOp.Eq => value == T(cond.Value),
        FilterOp.Ne => value != T(cond.Value),
        FilterOp.Lt => value < T(cond.Value),
        FilterOp.Le => value <= T(cond.Value),
        FilterOp.Gt => value > T(cond.Value),
        FilterOp.Ge => value >= T(cond.Value),
        FilterOp.Between => value >= T(cond.Value) && value <= T(cond.Value2),
        _ => false,
    };

    private static bool MatchBool(ValidFilter cond, bool value) => cond.Op switch
    {
        FilterOp.Eq => value == (bool)cond.Value!,
        FilterOp.Ne => value != (bool)cond.Value!,
        _ => false,
    };

    private static bool MatchText(ValidFilter cond, string value)
    {
        var expected = cond.Value as string;
        return cond.Op switch
        {
            FilterOp.Eq => string.Equals(value, expected, StringComparison.Ordinal),
            FilterOp.Ne => !string.Equals(value, expected, StringComparison.Ordinal),
            FilterOp.Contains => value.Contains(expected!, StringComparison.OrdinalIgnoreCase),
            FilterOp.Ncontains => !value.Contains(expected!, StringComparison.OrdinalIgnoreCase),
            FilterOp.Starts => value.StartsWith(expected!, StringComparison.OrdinalIgnoreCase),
            FilterOp.Ends => value.EndsWith(expected!, StringComparison.OrdinalIgnoreCase),
            FilterOp.Lt => string.CompareOrdinal(value, expected) < 0,
            FilterOp.Le => string.CompareOrdinal(value, expected) <= 0,
            FilterOp.Gt => string.CompareOrdinal(value, expected) > 0,
            FilterOp.Ge => string.CompareOrdinal(value, expected) >= 0,
            FilterOp.Between => string.CompareOrdinal(value, (string)cond.Value!) >= 0
                             && string.CompareOrdinal(value, (string)cond.Value2!) <= 0,
            FilterOp.In => cond.Values!.Any(v => string.Equals(value, (string)v, StringComparison.Ordinal)),
            FilterOp.Nin => cond.Values!.All(v => !string.Equals(value, (string)v, StringComparison.Ordinal)),
            _ => false,
        };
    }

    private static decimal D(object? v) => Convert.ToDecimal(v, CultureInfo.InvariantCulture);

    private static DateTime T(object? v) => v is DateTime dt
        ? dt
        : DateTime.Parse(Convert.ToString(v, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture);

    private static DateTime AsDate(object raw) => raw is DateTime dt
        ? dt
        : DateTime.Parse(Convert.ToString(raw, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture);
}
