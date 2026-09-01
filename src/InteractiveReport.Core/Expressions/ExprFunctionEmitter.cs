using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Expressions;

/// <summary>
/// Emits the portable function vocabulary as dialect-specific SQL. Function discovery,
/// arity, and type inference remain in <see cref="ExprFunctions"/>; this class owns only
/// SQL representation.
/// </summary>
internal static class ExprFunctionEmitter
{
    /// <summary>
    /// Emits a direct function call whose arguments need no special rewriting.
    /// </summary>
    /// <param name="context">The mutable SQL and binding accumulator.</param>
    /// <param name="name">The trusted dialect function name supplied by the registry.</param>
    /// <param name="arguments">Bound arguments emitted in call order.</param>
    /// <remarks>Appends SQL and nested bindings to <paramref name="context"/>.</remarks>
    public static void EmitPlain(EmitContext context, string name, IReadOnlyList<ExprNode> arguments)
    {
        context.Append(name).Append('(');
        for (var i = 0; i < arguments.Count; i++)
        {
            if (i > 0) context.Append(", ");
            context.Visit(arguments[i]);
        }
        context.Append(')');
    }

    /// <summary>
    /// Renders concatenation with NULL treated as empty on every supported dialect.
    /// </summary>
    /// <param name="context">The mutable SQL and binding accumulator.</param>
    /// <param name="arguments">Two or more bound text arguments.</param>
    /// <remarks>Appends SQL and nested bindings to <paramref name="context"/>.</remarks>
    public static void EmitConcat(EmitContext context, IReadOnlyList<ExprNode> arguments)
    {
        if (context.Dialect == ReportDialect.Oracle)
        {
            // Oracle CONCAT is two-argument only; native || already treats
            // NULL as empty.
            context.Append('(');
            for (var i = 0; i < arguments.Count; i++)
            {
                if (i > 0) context.Append(" || ");
                context.Visit(arguments[i]);
            }
            context.Append(')');
            return;
        }

        EmitPlain(context, "CONCAT", arguments);
    }

    /// <summary>
    /// Emits ROUND with the validated numeric and precision arguments.
    /// </summary>
    /// <param name="context">The mutable SQL and binding accumulator.</param>
    /// <param name="arguments">The bound number and optional integer precision.</param>
    /// <remarks>PostgreSQL casts both two-argument operands to signatures accepted by <c>ROUND</c>.</remarks>
    public static void EmitRound(EmitContext context, IReadOnlyList<ExprNode> arguments)
    {
        if (context.Dialect == ReportDialect.Postgres && arguments.Count == 2)
        {
            context.Append("ROUND(CAST(");
            context.Visit(arguments[0]);
            context.Append(" AS NUMERIC), CAST(");
            context.Visit(arguments[1]);
            context.Append(" AS INT))");
            return;
        }

        EmitPlain(context, "ROUND", arguments);
    }

    /// <summary>
    /// Emits the dialect-specific substring function.
    /// </summary>
    /// <param name="context">The mutable SQL and binding accumulator.</param>
    /// <param name="arguments">Text, one-based start, and optional length.</param>
    /// <remarks>SQL Server synthesizes an omitted length with <c>LEN</c>; other dialects use <c>SUBSTR</c>.</remarks>
    public static void EmitSubstr(EmitContext context, IReadOnlyList<ExprNode> arguments)
    {
        if (context.Dialect != ReportDialect.SqlServer)
        {
            EmitPlain(context, "SUBSTR", arguments);
            return;
        }

        context.Append("SUBSTRING(");
        context.Visit(arguments[0]);
        context.Append(", ");
        context.Visit(arguments[1]);
        context.Append(", ");
        if (arguments.Count == 3)
        {
            context.Visit(arguments[2]);
        }
        else
        {
            context.Append("LEN(");
            context.Visit(arguments[0]);
            context.Append(')');
        }
        context.Append(')');
    }

    /// <summary>
    /// Emits a portable case-insensitive LIKE predicate with wildcard pieces represented as bound literals.
    /// </summary>
    /// <param name="context">The mutable SQL and binding accumulator.</param>
    /// <param name="arguments">The bound candidate text and search text.</param>
    /// <param name="leadingWildcard">Indicates whether the generated pattern starts with a wildcard.</param>
    /// <param name="trailingWildcard">Indicates whether the generated pattern ends with a wildcard.</param>
    public static void EmitTextMatch(
        EmitContext context,
        IReadOnlyList<ExprNode> arguments,
        bool leadingWildcard,
        bool trailingWildcard)
    {
        context.Append("(LOWER(");
        context.Visit(arguments[0]);
        context.Append(") LIKE LOWER(");

        var pattern = new List<ExprNode>(3);
        if (leadingWildcard) pattern.Add(new StringLit("%"));
        pattern.Add(arguments[1]);
        if (trailingWildcard) pattern.Add(new StringLit("%"));
        EmitConcat(context, pattern);
        context.Append("))");
    }

    /// <summary>
    /// Emits a case-insensitive match whose bound user pattern uses <c>*</c> as its only
    /// wildcard and <c>\*</c> for a literal asterisk.
    /// </summary>
    /// <param name="context">The mutable SQL and binding accumulator.</param>
    /// <param name="arguments">The candidate text and validated literal user pattern.</param>
    public static void EmitWildcardMatch(EmitContext context, IReadOnlyList<ExprNode> arguments)
    {
        var pattern = (StringLit)arguments[1];
        context.Append("(LOWER(");
        context.Visit(arguments[0]);
        context.Append(") LIKE LOWER(");
        context.AppendBinding(ToSqlLikePattern(pattern.Value));
        context.Append(") ESCAPE '\\')");
    }

    /// <summary>
    /// Converts the public asterisk pattern to a SQL LIKE pattern while making SQL's
    /// native wildcard and escape characters literal.
    /// </summary>
    private static string ToSqlLikePattern(string value)
    {
        var pattern = new System.Text.StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '\\' && i + 1 < value.Length && value[i + 1] is '*' or '\\')
            {
                AppendLikeLiteral(pattern, value[++i]);
                continue;
            }

            if (c == '*')
            {
                pattern.Append('%');
                continue;
            }

            AppendLikeLiteral(pattern, c);
        }
        return pattern.ToString();
    }

    private static void AppendLikeLiteral(System.Text.StringBuilder pattern, char value)
    {
        if (value is '%' or '_' or '\\') pattern.Append('\\');
        pattern.Append(value);
    }

    /// <summary>
    /// Emits an IN-list predicate from one candidate followed by one or more values.
    /// </summary>
    /// <param name="context">The mutable SQL and binding accumulator.</param>
    /// <param name="arguments">The candidate expression followed by list values.</param>
    public static void EmitInList(EmitContext context, IReadOnlyList<ExprNode> arguments)
    {
        context.Append('(');
        context.Visit(arguments[0]);
        context.Append(" IN (");
        for (var i = 1; i < arguments.Count; i++)
        {
            if (i > 1) context.Append(", ");
            context.Visit(arguments[i]);
        }
        context.Append("))");
    }

    /// <summary>
    /// Emits the fixed request timestamp as a bound value.
    /// </summary>
    /// <param name="context">The mutable SQL and binding accumulator containing the normalized request time.</param>
    /// <param name="arguments">The validated empty argument list; it is unused during emission.</param>
    public static void EmitNow(EmitContext context, IReadOnlyList<ExprNode> arguments)
        // NOW is a request value, not an engine or session clock. Binding the
        // one UTC instant carried by the bound plan also keeps repeated occurrences and
        // separate terminal statements coherent.
        => context.AppendBinding(context.EvaluationUtcNow);

    /// <summary>
    /// Emits dialect-specific text-to-date conversion.
    /// </summary>
    /// <param name="context">The mutable SQL and binding accumulator.</param>
    /// <param name="arguments">The single bound text, date, or null value.</param>
    public static void EmitToDate(EmitContext context, IReadOnlyList<ExprNode> arguments)
    {
        var argument = arguments[0];
        if (argument is NullLit)
        {
            EmitDateNull(context);
            return;
        }

        if (argument.Kind == ColumnKind.Date)
        {
            if (context.Dialect == ReportDialect.Sqlite)
            {
                context.Append("datetime(");
                context.Visit(argument);
                context.Append(')');
                return;
            }
            context.Visit(argument);
            return;
        }

        switch (context.Dialect)
        {
            case ReportDialect.SqlServer:
                context.Append("CAST(");
                context.Visit(argument);
                context.Append(" AS DATETIME2)");
                break;
            case ReportDialect.Oracle or ReportDialect.Postgres:
                context.Append("TO_DATE(");
                context.Visit(argument);
                context.Append(", 'YYYY-MM-DD')");
                break;
            default:
                context.Append("datetime(");
                context.Visit(argument);
                context.Append(')');
                break;
        }
    }

    /// <summary>
    /// Emits dialect-specific date truncation.
    /// </summary>
    /// <param name="context">The mutable SQL and binding accumulator.</param>
    /// <param name="arguments">A validated truncation-unit literal followed by a bound date.</param>
    public static void EmitDateTrunc(EmitContext context, IReadOnlyList<ExprNode> arguments)
    {
        var unit = ExprDateRules.TruncUnit(arguments[0]);
        var date = arguments[1];
        if (date is NullLit)
        {
            EmitDateNull(context);
            return;
        }

        switch (context.Dialect)
        {
            case ReportDialect.SqlServer:
                EmitSqlServerDateTrunc(context, unit, date);
                break;
            case ReportDialect.Oracle:
                context.Append("TRUNC(");
                context.Visit(date);
                context.Append(unit switch
                {
                    "DAY" => ", 'DD')",
                    "MONTH" => ", 'MM')",
                    _ => ", 'YYYY')",
                });
                break;
            case ReportDialect.Postgres:
                context.Append("DATE_TRUNC('").Append(unit.ToLowerInvariant()).Append("', ");
                context.Visit(date);
                context.Append(')');
                break;
            default:
                context.Append("datetime(");
                context.Visit(date);
                context.Append(", 'start of ").Append(unit.ToLowerInvariant()).Append("')");
                break;
        }
    }

    /// <summary>
    /// Emits dialect-specific date or value formatting.
    /// </summary>
    /// <param name="context">The mutable SQL and binding accumulator.</param>
    /// <param name="arguments">A bound date/value and optional validated date-format literal.</param>
    public static void EmitToString(EmitContext context, IReadOnlyList<ExprNode> arguments)
    {
        var date = arguments[0];
        if (date is NullLit)
        {
            EmitTextNull(context);
            return;
        }

        var parts = arguments.Count == 2
            ? ExprDateRules.ParseDateFormat(ExprDateRules.FormatLiteral(arguments[1]))
            : ExprDateRules.DefaultFormat;
        var mask = ExprDateRules.TranslateFormat(context.Dialect, parts);

        switch (context.Dialect)
        {
            case ReportDialect.SqlServer:
                context.Append("FORMAT(");
                context.Visit(date);
                context.Append(", ");
                context.AppendBinding(mask);
                context.Append(", 'en-US')");
                break;
            case ReportDialect.Oracle or ReportDialect.Postgres:
                context.Append("TO_CHAR(");
                context.Visit(date);
                context.Append(", ");
                context.AppendBinding(mask);
                context.Append(')');
                break;
            default:
                context.Append("strftime(");
                context.AppendBinding(mask);
                context.Append(", ");
                context.Visit(date);
                context.Append(')');
                break;
        }
    }

    /// <summary>
    /// Emits dialect-specific date arithmetic.
    /// </summary>
    /// <param name="context">The mutable SQL and binding accumulator.</param>
    /// <param name="node">The bound date, day-count, and plus/minus operation.</param>
    public static void EmitDateAdd(EmitContext context, DateAdd node)
    {
        var subtract = node.Op == "-";
        switch (context.Dialect)
        {
            case ReportDialect.SqlServer:
                context.Append("DATEADD(DAY, ");
                if (subtract) context.Append("-(");
                context.Visit(node.Days);
                if (subtract) context.Append(')');
                context.Append(", ");
                context.Visit(node.Date);
                context.Append(')');
                break;
            case ReportDialect.Oracle:
                context.Append('(');
                context.Visit(node.Date);
                context.Append(subtract ? " - " : " + ");
                context.Visit(node.Days);
                context.Append(')');
                break;
            case ReportDialect.Postgres:
                context.Append('(');
                context.Visit(node.Date);
                context.Append(subtract ? " - (" : " + (");
                context.Visit(node.Days);
                context.Append(" * INTERVAL '1 day'))");
                break;
            default:
                context.Append("datetime(");
                context.Visit(node.Date);
                context.Append(", (");
                if (subtract) context.Append("-(");
                context.Visit(node.Days);
                if (subtract) context.Append(')');
                context.Append(") || ' days')");
                break;
        }
    }

    /// <summary>
    /// Emits dialect-specific date-part extraction.
    /// </summary>
    /// <param name="context">The mutable SQL and binding accumulator.</param>
    /// <param name="part">The trusted SQL date-part keyword selected by the function registry.</param>
    /// <param name="sqliteFormat">The trusted <c>strftime</c> directive for the same date part.</param>
    /// <param name="arguments">The single bound date or textual date argument.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="context"/> carries an unsupported dialect.</exception>
    public static void EmitDatePart(
        EmitContext context,
        string part,
        string sqliteFormat,
        IReadOnlyList<ExprNode> arguments)
    {
        var argument = arguments[0];
        var textual = argument.Kind == ColumnKind.Text || argument is NullLit;

        switch (context.Dialect)
        {
            case ReportDialect.SqlServer:
                EmitPlain(context, part, arguments);
                break;
            case ReportDialect.Oracle:
                context.Append("EXTRACT(").Append(part).Append(" FROM ");
                if (textual)
                {
                    context.Append("TO_DATE(SUBSTR(");
                    context.Visit(argument);
                    context.Append(", 1, 10), 'YYYY-MM-DD')");
                }
                else
                {
                    context.Visit(argument);
                }
                context.Append(')');
                break;
            case ReportDialect.Postgres:
                context.Append("EXTRACT(").Append(part).Append(" FROM ");
                if (textual)
                {
                    context.Append("CAST(");
                    context.Visit(argument);
                    context.Append(" AS TIMESTAMP)");
                }
                else
                {
                    context.Visit(argument);
                }
                context.Append(')');
                break;
            case ReportDialect.Sqlite:
                context.Append("CAST(strftime('").Append(sqliteFormat).Append("', ");
                context.Visit(argument);
                context.Append(") AS INTEGER)");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(context), context.Dialect, null);
        }
    }

    /// <summary>
    /// Emits SQL Server date truncation without relying on version-specific <c>DATETRUNC</c> support.
    /// </summary>
    /// <param name="context">The mutable SQL and binding accumulator.</param>
    /// <param name="unit">The validated <c>DAY</c>, <c>MONTH</c>, or <c>YEAR</c> token.</param>
    /// <param name="date">The bound date expression to truncate.</param>
    private static void EmitSqlServerDateTrunc(EmitContext context, string unit, ExprNode date)
    {
        switch (unit)
        {
            case "DAY":
                context.Append("CAST(CAST(");
                context.Visit(date);
                context.Append(" AS DATE) AS DATETIME2)");
                break;
            case "MONTH":
                context.Append("CAST(DATEFROMPARTS(YEAR(");
                context.Visit(date);
                context.Append("), MONTH(");
                context.Visit(date);
                context.Append("), 1) AS DATETIME2)");
                break;
            default:
                context.Append("CAST(DATEFROMPARTS(YEAR(");
                context.Visit(date);
                context.Append("), 1, 1) AS DATETIME2)");
                break;
        }
    }

    /// <summary>
    /// Emits a typed null expression suitable for date context.
    /// </summary>
    /// <param name="context">The mutable SQL accumulator whose dialect selects the typed null.</param>
    private static void EmitDateNull(EmitContext context)
        => context.Append(context.Dialect switch
        {
            ReportDialect.SqlServer => "CAST(NULL AS DATETIME2)",
            ReportDialect.Oracle => "CAST(NULL AS DATE)",
            ReportDialect.Postgres => "CAST(NULL AS TIMESTAMP)",
            _ => "NULL",
        });

    /// <summary>
    /// Emits a typed null expression suitable for text context.
    /// </summary>
    /// <param name="context">The mutable SQL accumulator whose dialect selects the typed null.</param>
    private static void EmitTextNull(EmitContext context)
        => context.Append(context.Dialect switch
        {
            ReportDialect.SqlServer => "CAST(NULL AS NVARCHAR(30))",
            ReportDialect.Oracle => "CAST(NULL AS VARCHAR2(30))",
            ReportDialect.Postgres => "CAST(NULL AS TEXT)",
            _ => "NULL",
        });
}
