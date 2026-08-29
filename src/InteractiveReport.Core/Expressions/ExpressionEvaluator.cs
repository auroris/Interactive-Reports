using System.Globalization;
using System.Text;
using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Expressions;

/// <summary>
/// Evaluates the same typed portable-expression AST used by SQL composition. Pivot
/// materialization produces a data-dependent schema in memory, so its layer runs here
/// after the long grouped query has been materialized as a wide table. Materialized
/// text comparison and sorting are ordinal. SQL-backed expressions necessarily inherit
/// the report database's collation because the supported dialects share neither a
/// collation name nor collation syntax; exact cross-path text parity therefore requires
/// a binary/ordinal database collation.
/// </summary>
internal sealed class ExpressionEvaluator(DateTime evaluationUtcNow)
{
    private readonly DateTime _evaluationUtcNow = evaluationUtcNow.Kind switch
    {
        DateTimeKind.Utc => evaluationUtcNow,
        DateTimeKind.Local => evaluationUtcNow.ToUniversalTime(),
        _ => DateTime.SpecifyKind(evaluationUtcNow, DateTimeKind.Utc),
    };

    public object? Evaluate(
        ExprNode expression,
        IReadOnlyDictionary<string, object?> row)
        => expression switch
        {
            NumberLit number => number.Value,
            StringLit text => text.Value,
            NullLit => null,
            ColumnRef column => row.TryGetValue(column.Column.Name, out var value) ? value : null,
            UnaryMinus unary => Numeric(unary.Operand, row) is { } value ? -value : null,
            BinaryOp binary => Binary(binary, row),
            Comparison comparison => CompareExpression(comparison, row),
            Between between => BetweenExpression(between, row),
            DateAdd dateAdd => AddDate(dateAdd, row),
            LogicalOp logical => Logical(logical, row),
            NotOp not => Truth(Evaluate(not.Operand, row)) is { } value ? !value : null,
            NullTest test => test.Negated
                ? Evaluate(test.Operand, row) is not null
                : Evaluate(test.Operand, row) is null,
            CaseWhen @case => Case(@case, row),
            FuncCall call => Function(call, row),
            _ => throw new InvalidOperationException($"Unsupported expression node {expression.GetType().Name}.")
        };

    public bool IsTrue(ExprNode expression, IReadOnlyDictionary<string, object?> row)
        => Truth(Evaluate(expression, row)) == true;

    private object? Binary(BinaryOp binary, IReadOnlyDictionary<string, object?> row)
    {
        var left = Evaluate(binary.Left, row);
        var right = Evaluate(binary.Right, row);
        if (binary.Op == "||")
            return Text(left) + Text(right);
        if (left is null || right is null) return null;

        var l = Decimal(left);
        var r = Decimal(right);
        return binary.Op switch
        {
            "+" => l + r,
            "-" => l - r,
            "*" => l * r,
            "/" => r == 0 ? throw new DivideByZeroException("Expression division by zero.") : l / r,
            _ => throw new InvalidOperationException($"Unsupported binary operator '{binary.Op}'.")
        };
    }

    private object? CompareExpression(
        Comparison comparison,
        IReadOnlyDictionary<string, object?> row)
    {
        var left = Evaluate(comparison.Left, row);
        var right = Evaluate(comparison.Right, row);
        if (left is null || right is null) return null;
        var value = Compare(left, right, comparison.Left.Kind);
        return comparison.Op switch
        {
            "=" => value == 0,
            "<>" => value != 0,
            "<" => value < 0,
            "<=" => value <= 0,
            ">" => value > 0,
            ">=" => value >= 0,
            _ => throw new InvalidOperationException($"Unsupported comparison '{comparison.Op}'.")
        };
    }

    private object? BetweenExpression(
        Between between,
        IReadOnlyDictionary<string, object?> row)
    {
        var value = Evaluate(between.Operand, row);
        var lower = Evaluate(between.Lower, row);
        var upper = Evaluate(between.Upper, row);
        if (value is null || lower is null || upper is null) return null;
        return Compare(value, lower, between.Operand.Kind) >= 0
            && Compare(value, upper, between.Operand.Kind) <= 0;
    }

    private object? AddDate(DateAdd expression, IReadOnlyDictionary<string, object?> row)
    {
        var date = Evaluate(expression.Date, row);
        var days = Evaluate(expression.Days, row);
        if (date is null || days is null) return null;
        var offset = Decimal(days);
        var signed = expression.Op == "-" ? -offset : offset;
        return Date(date).AddDays((double)signed);
    }

    private object? Logical(LogicalOp expression, IReadOnlyDictionary<string, object?> row)
    {
        var left = Truth(Evaluate(expression.Left, row));
        var right = Truth(Evaluate(expression.Right, row));
        return expression.Op switch
        {
            "AND" when left == false || right == false => false,
            "AND" when left == true && right == true => true,
            "AND" => null,
            "OR" when left == true || right == true => true,
            "OR" when left == false && right == false => false,
            "OR" => null,
            _ => throw new InvalidOperationException($"Unsupported logical operator '{expression.Op}'.")
        };
    }

    private object? Case(CaseWhen expression, IReadOnlyDictionary<string, object?> row)
    {
        if (expression.Operand is null)
        {
            foreach (var branch in expression.Branches)
                if (IsTrue(branch.When, row)) return Evaluate(branch.Then, row);
        }
        else
        {
            var operand = Evaluate(expression.Operand, row);
            if (operand is not null)
            {
                foreach (var branch in expression.Branches)
                {
                    var when = Evaluate(branch.When, row);
                    if (when is not null && Compare(operand, when, expression.Operand.Kind) == 0)
                        return Evaluate(branch.Then, row);
                }
            }
        }
        return expression.Else is null ? null : Evaluate(expression.Else, row);
    }

    private object? Function(FuncCall call, IReadOnlyDictionary<string, object?> row)
    {
        var values = call.Args.Select(argument => Evaluate(argument, row)).ToArray();
        return call.Name.ToUpperInvariant() switch
        {
            "UPPER" => values[0] is null ? null : Text(values[0]).ToUpperInvariant(),
            "LOWER" => values[0] is null ? null : Text(values[0]).ToLowerInvariant(),
            "TRIM" => values[0] is null ? null : Text(values[0]).Trim(),
            "LENGTH" => values[0] is null ? null : (decimal)Text(values[0]).Length,
            "SUBSTR" => Substring(values),
            "CONCAT" => string.Concat(values.Select(Text)),
            "ROUND" => values[0] is null ? null : Round(
                Decimal(values[0]!),
                values.Length == 1 || values[1] is null ? 0 : checked((int)Decimal(values[1]!))),
            "ABS" => values[0] is null ? null : decimal.Abs(Decimal(values[0]!)),
            "COALESCE" => values.FirstOrDefault(value => value is not null),
            "CONTAINS" => TextMatch(values, static (source, value) => source.Contains(value, StringComparison.OrdinalIgnoreCase)),
            "STARTS_WITH" => TextMatch(values, static (source, value) => source.StartsWith(value, StringComparison.OrdinalIgnoreCase)),
            "ENDS_WITH" => TextMatch(values, static (source, value) => source.EndsWith(value, StringComparison.OrdinalIgnoreCase)),
            "IN_LIST" => InList(call, values),
            "YEAR" => values[0] is null ? null : (decimal)Date(values[0]!).Year,
            "MONTH" => values[0] is null ? null : (decimal)Date(values[0]!).Month,
            "DAY" => values[0] is null ? null : (decimal)Date(values[0]!).Day,
            "NOW" => _evaluationUtcNow,
            "TO_DATE" => values[0] is null ? null : Date(values[0]!).Date,
            "DATE_TRUNC" => values[1] is null ? null : TruncateDate((string)values[0]!, Date(values[1]!)),
            "TO_STRING" => values[0] is null ? null : FormatDate(
                Date(values[0]!),
                values.Length == 1 ? ExprDateRules.DefaultFormat : ExprDateRules.ParseDateFormat((string)values[1]!)),
            _ => throw new InvalidOperationException($"Unsupported function '{call.Name}'.")
        };
    }

    private static object? Substring(object?[] values)
    {
        if (values.Any(value => value is null)) return null;
        var source = Text(values[0]);
        var start = checked((int)Decimal(values[1]!));
        var zeroBased = start > 0 ? start - 1 : Math.Max(0, source.Length + start);
        if (zeroBased >= source.Length) return "";
        return values.Length == 2
            ? source[zeroBased..]
            : source.Substring(zeroBased, Math.Clamp(checked((int)Decimal(values[2]!)), 0, source.Length - zeroBased));
    }

    private static object? TextMatch(object?[] values, Func<string, string, bool> predicate)
        => values[0] is null || values[1] is null ? null : predicate(Text(values[0]), Text(values[1]));

    private static object? InList(FuncCall call, object?[] values)
    {
        if (values[0] is null) return null;
        var sawNull = false;
        for (var i = 1; i < values.Length; i++)
        {
            if (values[i] is null) { sawNull = true; continue; }
            if (Compare(values[0]!, values[i]!, call.Args[0].Kind) == 0) return true;
        }
        return sawNull ? null : false;
    }

    private static DateTime TruncateDate(string unit, DateTime value)
        => unit.ToUpperInvariant() switch
        {
            "DAY" => value.Date,
            "MONTH" => new DateTime(value.Year, value.Month, 1, 0, 0, 0, value.Kind),
            "YEAR" => new DateTime(value.Year, 1, 1, 0, 0, 0, value.Kind),
            _ => throw new InvalidOperationException($"Unsupported DATE_TRUNC unit '{unit}'.")
        };

    private static decimal Round(decimal value, int places)
    {
        if (places >= 0)
            return decimal.Round(value, Math.Min(28, places), MidpointRounding.AwayFromZero);
        var factor = 1m;
        for (var index = 0; index < -places; index++) factor *= 10m;
        return decimal.Round(value / factor, 0, MidpointRounding.AwayFromZero) * factor;
    }

    private static string FormatDate(DateTime value, IReadOnlyList<string> parts)
    {
        var result = new StringBuilder();
        foreach (var part in parts)
            result.Append(part switch
            {
                "YYYY" => value.ToString("yyyy", CultureInfo.InvariantCulture),
                "MM" => value.ToString("MM", CultureInfo.InvariantCulture),
                "DD" => value.ToString("dd", CultureInfo.InvariantCulture),
                "HH24" => value.ToString("HH", CultureInfo.InvariantCulture),
                "MI" => value.ToString("mm", CultureInfo.InvariantCulture),
                "SS" => value.ToString("ss", CultureInfo.InvariantCulture),
                _ => part,
            });
        return result.ToString();
    }

    internal static int Compare(object left, object right, ColumnKind kind)
        => kind switch
        {
            ColumnKind.Number => Decimal(left).CompareTo(Decimal(right)),
            ColumnKind.Date => Date(left).CompareTo(Date(right)),
            // One culture-independent rule serves comparisons, BETWEEN, IN_LIST,
            // CASE matching, terminal sorting, and materialized MIN/MAX.
            ColumnKind.Text => string.CompareOrdinal(Text(left), Text(right)),
            ColumnKind.Bool => Convert.ToBoolean(left, CultureInfo.InvariantCulture)
                .CompareTo(Convert.ToBoolean(right, CultureInfo.InvariantCulture)),
            _ when left is IComparable comparable && left.GetType().IsInstanceOfType(right)
                => comparable.CompareTo(right),
            _ => string.CompareOrdinal(Text(left), Text(right)),
        };

    private decimal? Numeric(ExprNode expression, IReadOnlyDictionary<string, object?> row)
    {
        var value = Evaluate(expression, row);
        return value is null ? null : Decimal(value);
    }

    private static decimal Decimal(object value)
        => Convert.ToDecimal(value, CultureInfo.InvariantCulture);

    private static string Text(object? value)
        => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";

    private static DateTime Date(object value)
        => value switch
        {
            DateTime date => date,
            DateTimeOffset offset => offset.UtcDateTime,
            DateOnly date => date.ToDateTime(TimeOnly.MinValue),
            string text => DateTime.ParseExact(
                text,
                ["yyyy-MM-dd", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss", "O"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind),
            _ => Convert.ToDateTime(value, CultureInfo.InvariantCulture),
        };

    private static bool? Truth(object? value)
        => value switch
        {
            null => null,
            bool boolean => boolean,
            _ => Convert.ToDecimal(value, CultureInfo.InvariantCulture) != 0,
        };
}
