using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Expressions;

/// <summary>
/// Emits the portable function vocabulary as dialect-specific SQL. Function discovery,
/// arity, and type inference remain in <see cref="ExprFunctions"/>; this class owns only
/// SQL representation.
/// </summary>
internal static class ExprFunctionEmitter
{
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

    /// <summary>Concatenation treats NULL as empty across all supported dialects.</summary>
    public static void EmitConcat(EmitContext context, IReadOnlyList<ExprNode> arguments)
    {
        if (context.Dialect == ReportDialect.Oracle)
        {
            // Oracle CONCAT is two-argument only; native || already treats NULL as empty.
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

    /// <summary>Portable case-insensitive LIKE predicates with bound wildcard pieces.</summary>
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

    public static void EmitNow(EmitContext context, IReadOnlyList<ExprNode> arguments)
        => context.Append(context.Dialect switch
        {
            ReportDialect.SqlServer => "GETDATE()",
            ReportDialect.Oracle => "LOCALTIMESTAMP",
            ReportDialect.Postgres => "NOW()",
            _ => "datetime('now', 'localtime')",
        });

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

    private static void EmitDateNull(EmitContext context)
        => context.Append(context.Dialect switch
        {
            ReportDialect.SqlServer => "CAST(NULL AS DATETIME2)",
            ReportDialect.Oracle => "CAST(NULL AS DATE)",
            ReportDialect.Postgres => "CAST(NULL AS TIMESTAMP)",
            _ => "NULL",
        });

    private static void EmitTextNull(EmitContext context)
        => context.Append(context.Dialect switch
        {
            ReportDialect.SqlServer => "CAST(NULL AS NVARCHAR(30))",
            ReportDialect.Oracle => "CAST(NULL AS VARCHAR2(30))",
            ReportDialect.Postgres => "CAST(NULL AS TEXT)",
            _ => "NULL",
        });
}
