using System.Globalization;
using System.Text;
using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Expressions;

/// <summary>
/// The function registry: one entry per function in the portable subset, carrying
/// arity, argument rules, result-kind inference, and the per-dialect emitter.
/// Adding a function is adding a row here — no enum, no switches to grow.
/// </summary>
internal sealed record FunctionDef(
    string Name,
    int MinArgs,
    int MaxArgs,
    Func<FunctionArgs, ColumnKind> Bind,
    Action<EmitContext, IReadOnlyList<ExprNode>> Emit);

/// <summary>Bound, typed arguments handed to a function's Bind rule.</summary>
internal readonly struct FunctionArgs(string name, IReadOnlyList<ExprNode> args)
{
    public string Name { get; } = name;
    public IReadOnlyList<ExprNode> Args { get; } = args;

    /// <summary>NULL literals satisfy any requirement — NULL is a value of every type.</summary>
    public void Require(int index, string what, params ColumnKind[] kinds)
    {
        var arg = Args[index];
        if (arg is NullLit) return;
        if (!kinds.Contains(arg.Kind))
            throw new ExprError($"{Name} argument {index + 1} must be {what}");
    }
}

internal static class ExprFunctions
{
    public static bool TryGet(string name, out FunctionDef def) => Registry.TryGetValue(name, out def!);

    public static FunctionDef Get(string name) => Registry[name];

    private static readonly Dictionary<string, FunctionDef> Registry =
        new(StringComparer.OrdinalIgnoreCase);

    private static void Add(string name, int min, int max,
        Func<FunctionArgs, ColumnKind> bind,
        Action<EmitContext, IReadOnlyList<ExprNode>>? emit = null)
        => Registry[name] = new FunctionDef(name, min, max, bind,
            emit ?? ((ctx, args) => EmitPlain(ctx, name, args)));

    static ExprFunctions()
    {
        Add("UPPER", 1, 1, a => { a.Require(0, "text", ColumnKind.Text); return ColumnKind.Text; });
        Add("LOWER", 1, 1, a => { a.Require(0, "text", ColumnKind.Text); return ColumnKind.Text; });
        Add("TRIM", 1, 1, a => { a.Require(0, "text", ColumnKind.Text); return ColumnKind.Text; });

        Add("LENGTH", 1, 1,
            a => { a.Require(0, "text", ColumnKind.Text); return ColumnKind.Number; },
            (ctx, args) => EmitPlain(ctx, ctx.Dialect == ReportDialect.SqlServer ? "LEN" : "LENGTH", args));

        Add("SUBSTR", 2, 3,
            a =>
            {
                a.Require(0, "text", ColumnKind.Text);
                a.Require(1, "a number", ColumnKind.Number);
                if (a.Args.Count == 3) a.Require(2, "a number", ColumnKind.Number);
                return ColumnKind.Text;
            },
            EmitSubstr);

        Add("CONCAT", 2, 8,
            a =>
            {
                for (var i = 0; i < a.Args.Count; i++)
                    a.Require(i, "text or a number (dates go through TO_STRING)", ColumnKind.Text, ColumnKind.Number);
                return ColumnKind.Text;
            },
            (ctx, args) => EmitConcat(ctx, args));

        Add("ROUND", 1, 2,
            a =>
            {
                a.Require(0, "a number", ColumnKind.Number);
                if (a.Args.Count == 2) a.Require(1, "a number", ColumnKind.Number);
                return ColumnKind.Number;
            },
            EmitRound);

        Add("ABS", 1, 1, a => { a.Require(0, "a number", ColumnKind.Number); return ColumnKind.Number; });

        Add("COALESCE", 2, 8,
            a =>
            {
                // Result is the arguments' common kind; NULL literals join any kind.
                ColumnKind? kind = null;
                for (var i = 0; i < a.Args.Count; i++)
                {
                    if (a.Args[i] is NullLit) continue;
                    if (kind is null) { kind = a.Args[i].Kind; continue; }
                    if (a.Args[i].Kind != kind)
                        throw new ExprError(
                            $"COALESCE arguments must all be the same type (argument {i + 1} is {a.Args[i].Kind.ToString().ToLowerInvariant()}, expected {kind.Value.ToString().ToLowerInvariant()})");
                }
                return kind ?? throw new ExprError("COALESCE cannot infer a type (every argument is NULL)");
            });

        Add("YEAR", 1, 1, DatePartBind("YEAR"), (ctx, args) => EmitDatePart(ctx, "YEAR", "%Y", args));
        Add("MONTH", 1, 1, DatePartBind("MONTH"), (ctx, args) => EmitDatePart(ctx, "MONTH", "%m", args));
        Add("DAY", 1, 1, DatePartBind("DAY"), (ctx, args) => EmitDatePart(ctx, "DAY", "%d", args));

        Add("NOW", 0, 0, _ => ColumnKind.Date, EmitNow);

        Add("TO_DATE", 1, 1,
            a =>
            {
                a.Require(0, "text or a date", ColumnKind.Text, ColumnKind.Date);
                // Literals are checkable right here; column contents are the ISO data
                // contract (invalid rows become a provider error or NULL at runtime).
                if (a.Args[0] is StringLit s && !DateTime.TryParseExact(s.Value, "yyyy-MM-dd",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                    throw new ExprError($"TO_DATE text must be ISO YYYY-MM-DD (got '{s.Value}')");
                return ColumnKind.Date;
            },
            EmitToDate);

        Add("DATE_TRUNC", 2, 2,
            a =>
            {
                TruncUnit(a.Args[0]);
                a.Require(1, "a date — convert text with TO_DATE first", ColumnKind.Date);
                return ColumnKind.Date;
            },
            EmitDateTrunc);

        Add("TO_STRING", 1, 2,
            a =>
            {
                a.Require(0, "a date", ColumnKind.Date);
                if (a.Args.Count == 2) ParseDateFormat(FormatLiteral(a.Args[1]));
                return ColumnKind.Text;
            },
            EmitToString);
    }

    private static Func<FunctionArgs, ColumnKind> DatePartBind(string name) => a =>
    {
        // Text allowed: SQLite date columns discover as text (ISO strings).
        a.Require(0, "a date (or ISO date text)", ColumnKind.Date, ColumnKind.Text);
        return ColumnKind.Number;
    };

    // --- emitters ------------------------------------------------------------

    internal static void EmitPlain(EmitContext ctx, string name, IReadOnlyList<ExprNode> args)
    {
        ctx.Append(name).Append('(');
        for (var i = 0; i < args.Count; i++)
        {
            if (i > 0) ctx.Append(", ");
            ctx.Visit(args[i]);
        }
        ctx.Append(')');
    }

    /// <summary>Concatenation treats NULL as empty on all three dialects (CONCAT on SqlServer/Sqlite; Oracle's native || already does).</summary>
    internal static void EmitConcat(EmitContext ctx, IReadOnlyList<ExprNode> args)
    {
        if (ctx.Dialect == ReportDialect.Oracle)
        {
            // Oracle CONCAT is two-arg only; native || already treats NULL as empty.
            ctx.Append('(');
            for (var i = 0; i < args.Count; i++)
            {
                if (i > 0) ctx.Append(" || ");
                ctx.Visit(args[i]);
            }
            ctx.Append(')');
            return;
        }

        // Variadic CONCAT treats NULL as empty on SqlServer and SQLite (3.44+).
        EmitPlain(ctx, "CONCAT", args);
    }

    private static void EmitRound(EmitContext ctx, IReadOnlyList<ExprNode> args)
    {
        // Postgres two-arg ROUND is exactly round(numeric, integer): a numeric-typed
        // precision parameter resolves to no function at all, and a double-precision
        // first argument fares no better. Cast both into the one signature that exists.
        if (ctx.Dialect == ReportDialect.Postgres && args.Count == 2)
        {
            ctx.Append("ROUND(CAST(");
            ctx.Visit(args[0]);
            ctx.Append(" AS NUMERIC), CAST(");
            ctx.Visit(args[1]);
            ctx.Append(" AS INT))");
            return;
        }

        EmitPlain(ctx, "ROUND", args);
    }

    private static void EmitSubstr(EmitContext ctx, IReadOnlyList<ExprNode> args)
    {
        if (ctx.Dialect != ReportDialect.SqlServer)
        {
            EmitPlain(ctx, "SUBSTR", args);
            return;
        }

        // SUBSTRING requires the length argument; "to end of string" is LEN(s).
        ctx.Append("SUBSTRING(");
        ctx.Visit(args[0]);
        ctx.Append(", ");
        ctx.Visit(args[1]);
        ctx.Append(", ");
        if (args.Count == 3)
        {
            ctx.Visit(args[2]);
        }
        else
        {
            ctx.Append("LEN(");
            ctx.Visit(args[0]);
            ctx.Append(')');
        }
        ctx.Append(')');
    }

    // --- the date vocabulary (design: docs/ARCHITECTURE.md §8) ---------------

    private static void EmitNow(EmitContext ctx, IReadOnlyList<ExprNode> args)
        // Session-local where the engine has a session timezone (LOCALTIMESTAMP
        // follows Oracle's session where SYSDATE would silently use the DB host's;
        // Postgres NOW() renders in the session TimeZone); the server's clock where
        // there is no such concept (SQL Server, SQLite).
        => ctx.Append(ctx.Dialect switch
        {
            ReportDialect.SqlServer => "GETDATE()",
            ReportDialect.Oracle => "LOCALTIMESTAMP",
            ReportDialect.Postgres => "NOW()",
            _ => "datetime('now', 'localtime')",
        });

    /// <summary>
    /// A literal NULL that still carries the Date type. A bare NULL loses it:
    /// Oracle types (NULL + 1) as NUMBER, Postgres resolves (NULL + interval) as
    /// INTERVAL and cannot pick a date_trunc overload for an untyped NULL at all —
    /// so every date producer emits its NULL pre-typed. SQLite is dynamically
    /// typed and keeps the plain keyword.
    /// </summary>
    private static void EmitDateNull(EmitContext ctx) => ctx.Append(ctx.Dialect switch
    {
        ReportDialect.SqlServer => "CAST(NULL AS DATETIME2)",
        ReportDialect.Oracle => "CAST(NULL AS DATE)",
        ReportDialect.Postgres => "CAST(NULL AS TIMESTAMP)",
        _ => "NULL",
    });

    /// <summary>The same rule for TO_STRING's text: 30 chars covers every mask this vocabulary can express (max 19).</summary>
    private static void EmitTextNull(EmitContext ctx) => ctx.Append(ctx.Dialect switch
    {
        ReportDialect.SqlServer => "CAST(NULL AS NVARCHAR(30))",
        ReportDialect.Oracle => "CAST(NULL AS VARCHAR2(30))",
        ReportDialect.Postgres => "CAST(NULL AS TEXT)",
        _ => "NULL",
    });

    private static void EmitToDate(EmitContext ctx, IReadOnlyList<ExprNode> args)
    {
        var arg = args[0];
        if (arg is NullLit)
        {
            EmitDateNull(ctx);
            return;
        }

        if (arg.Kind == ColumnKind.Date)
        {
            // Identity conversion — except on SQLite, where TO_DATE still
            // canonicalizes: Date values there are ISO text, and datetime()
            // normalizes date-only text to the full 'YYYY-MM-DD HH:MM:SS' form
            // every date producer emits.
            if (ctx.Dialect == ReportDialect.Sqlite)
            {
                ctx.Append("datetime(");
                ctx.Visit(arg);
                ctx.Append(')');
                return;
            }
            ctx.Visit(arg);
            return;
        }

        switch (ctx.Dialect)
        {
            case ReportDialect.SqlServer:
                // DATETIME2 parses ISO yyyy-MM-dd as y-m-d under every session
                // language; legacy DATETIME would not.
                ctx.Append("CAST(");
                ctx.Visit(arg);
                ctx.Append(" AS DATETIME2)");
                break;

            case ReportDialect.Oracle or ReportDialect.Postgres:
                ctx.Append("TO_DATE(");
                ctx.Visit(arg);
                ctx.Append(", 'YYYY-MM-DD')");
                break;

            default: // Sqlite: midnight-canonical text; invalid text becomes NULL
                ctx.Append("datetime(");
                ctx.Visit(arg);
                ctx.Append(')');
                break;
        }
    }

    private static void EmitDateTrunc(EmitContext ctx, IReadOnlyList<ExprNode> args)
    {
        // The unit argument is a validated literal consumed at compile time — it
        // selects the SQL we write and never reaches the database as data.
        var unit = TruncUnit(args[0]);
        var date = args[1];
        if (date is NullLit)
        {
            EmitDateNull(ctx);
            return;
        }

        switch (ctx.Dialect)
        {
            case ReportDialect.SqlServer:
                // DATE/DATEFROMPARTS, cast back into the datetime2 family — the
                // classic DATEADD(DATEDIFF(…, 0, …)) idiom pivots on the integer
                // epoch, which is legacy datetime and dies before 1753, while
                // TO_DATE accepts ISO years back to 0001. (Visiting the operand
                // twice duplicates its bindings; positional bindings make that
                // correct, same as the two-arg SUBSTR expansion.)
                switch (unit)
                {
                    case "DAY":
                        ctx.Append("CAST(CAST(");
                        ctx.Visit(date);
                        ctx.Append(" AS DATE) AS DATETIME2)");
                        break;
                    case "MONTH":
                        ctx.Append("CAST(DATEFROMPARTS(YEAR(");
                        ctx.Visit(date);
                        ctx.Append("), MONTH(");
                        ctx.Visit(date);
                        ctx.Append("), 1) AS DATETIME2)");
                        break;
                    default: // YEAR
                        ctx.Append("CAST(DATEFROMPARTS(YEAR(");
                        ctx.Visit(date);
                        ctx.Append("), 1, 1) AS DATETIME2)");
                        break;
                }
                break;

            case ReportDialect.Oracle:
                ctx.Append("TRUNC(");
                ctx.Visit(date);
                ctx.Append(unit switch { "DAY" => ", 'DD')", "MONTH" => ", 'MM')", _ => ", 'YYYY')" });
                break;

            case ReportDialect.Postgres:
                ctx.Append("DATE_TRUNC('").Append(unit.ToLowerInvariant()).Append("', ");
                ctx.Visit(date);
                ctx.Append(')');
                break;

            default: // Sqlite
                ctx.Append("datetime(");
                ctx.Visit(date);
                ctx.Append(", 'start of ").Append(unit.ToLowerInvariant()).Append("')");
                break;
        }
    }

    private static void EmitToString(EmitContext ctx, IReadOnlyList<ExprNode> args)
    {
        var date = args[0];
        if (date is NullLit)
        {
            // NULL in, NULL out — typed, and short-circuited because SQL Server's
            // FORMAT rejects an untyped NULL literal at compile time.
            EmitTextNull(ctx);
            return;
        }

        var parts = args.Count == 2 ? ParseDateFormat(FormatLiteral(args[1])) : DefaultFormat;
        var mask = TranslateFormat(ctx.Dialect, parts);

        switch (ctx.Dialect)
        {
            case ReportDialect.SqlServer:
                // The pinned culture keeps FORMAT deterministic: without it the
                // session language picks digits and calendar (ar-SA would render
                // Um Al-Qura years). en-US is Gregorian with Latin digits; the
                // quoted separators already keep '/' and ':' literal.
                ctx.Append("FORMAT(");
                ctx.Visit(date);
                ctx.Append(", ");
                ctx.AppendBinding(mask);
                ctx.Append(", 'en-US')");
                break;

            case ReportDialect.Oracle or ReportDialect.Postgres:
                ctx.Append("TO_CHAR(");
                ctx.Visit(date);
                ctx.Append(", ");
                ctx.AppendBinding(mask);
                ctx.Append(')');
                break;

            default: // Sqlite: strftime(format, value)
                ctx.Append("strftime(");
                ctx.AppendBinding(mask);
                ctx.Append(", ");
                ctx.Visit(date);
                ctx.Append(')');
                break;
        }
    }

    /// <summary>date ± whole days — a distinct idiom per dialect, so a distinct AST node.</summary>
    internal static void EmitDateAdd(EmitContext ctx, DateAdd node)
    {
        var minus = node.Op == "-";
        switch (ctx.Dialect)
        {
            case ReportDialect.SqlServer:
                // date + int is only legal for legacy datetime; DATEADD covers
                // date, datetime, and datetime2 alike.
                ctx.Append("DATEADD(DAY, ");
                if (minus) ctx.Append("-(");
                ctx.Visit(node.Days);
                if (minus) ctx.Append(')');
                ctx.Append(", ");
                ctx.Visit(node.Date);
                ctx.Append(')');
                break;

            case ReportDialect.Oracle:
                // Native DATE arithmetic: ± n is n days.
                ctx.Append('(');
                ctx.Visit(node.Date);
                ctx.Append(minus ? " - " : " + ");
                ctx.Visit(node.Days);
                ctx.Append(')');
                break;

            case ReportDialect.Postgres:
                // Only the date type has a ± integer operator; n * INTERVAL '1 day'
                // works uniformly for date, timestamp, and timestamptz.
                ctx.Append('(');
                ctx.Visit(node.Date);
                ctx.Append(minus ? " - (" : " + (");
                ctx.Visit(node.Days);
                ctx.Append(" * INTERVAL '1 day'))");
                break;

            default: // Sqlite: text dates move via modifiers — numeric + would add years
                ctx.Append("datetime(");
                ctx.Visit(node.Date);
                ctx.Append(", (");
                if (minus) ctx.Append("-(");
                ctx.Visit(node.Days);
                if (minus) ctx.Append(')');
                ctx.Append(") || ' days')");
                break;
        }
    }

    private static string TruncUnit(ExprNode arg)
    {
        if (arg is StringLit s)
        {
            var unit = s.Value.ToUpperInvariant();
            if (unit is "DAY" or "MONTH" or "YEAR") return unit;
        }
        throw new ExprError("DATE_TRUNC unit must be the literal 'DAY', 'MONTH', or 'YEAR'");
    }

    private static string FormatLiteral(ExprNode arg)
        => arg is StringLit s
            ? s.Value
            : throw new ExprError("TO_STRING format must be a string literal like 'YYYY-MM-DD'");

    private static readonly string[] FormatTokens = ["HH24", "YYYY", "MM", "DD", "MI", "SS"];

    private static readonly List<string> DefaultFormat = ["YYYY", "-", "MM", "-", "DD"];

    /// <summary>
    /// Validate a TO_STRING format into tokens and single-character separators.
    /// The vocabulary is ours and portable — masks are translated per dialect,
    /// never passed through as native format syntax.
    /// </summary>
    private static List<string> ParseDateFormat(string format)
    {
        if (format.Length == 0)
            throw new ExprError("TO_STRING format cannot be empty");

        var upper = format.ToUpperInvariant();
        var parts = new List<string>();
        var i = 0;
        while (i < upper.Length)
        {
            var token = FormatTokens.FirstOrDefault(t =>
                i + t.Length <= upper.Length && string.CompareOrdinal(upper, i, t, 0, t.Length) == 0);
            if (token is not null)
            {
                parts.Add(token);
                i += token.Length;
                continue;
            }
            if (upper[i] is ' ' or '-' or '/' or ':' or 'T')
            {
                parts.Add(upper[i].ToString());
                i++;
                continue;
            }
            throw new ExprError(
                $"TO_STRING format is invalid at character {i + 1} — tokens are YYYY, MM, DD, HH24, MI, SS, separated by space, '-', '/', ':', or 'T'");
        }
        return parts;
    }

    private static string TranslateFormat(ReportDialect dialect, List<string> parts)
    {
        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            switch (dialect)
            {
                case ReportDialect.SqlServer:
                    // .NET custom format; separators are quoted so '/' and ':' stay
                    // literal characters instead of culture-dependent placeholders.
                    sb.Append(part switch
                    {
                        "YYYY" => "yyyy",
                        "MM" => "MM",
                        "DD" => "dd",
                        "HH24" => "HH",
                        "MI" => "mm",
                        "SS" => "ss",
                        _ => $"'{part}'",
                    });
                    break;

                case ReportDialect.Oracle or ReportDialect.Postgres:
                    // TO_CHAR masks share our token names; a literal T must be
                    // double-quoted or both engines try to read it as a pattern.
                    sb.Append(part == "T" ? "\"T\"" : part);
                    break;

                default: // Sqlite strftime
                    sb.Append(part switch
                    {
                        "YYYY" => "%Y",
                        "MM" => "%m",
                        "DD" => "%d",
                        "HH24" => "%H",
                        "MI" => "%M",
                        "SS" => "%S",
                        _ => part,
                    });
                    break;
            }
        }
        return sb.ToString();
    }

    private static void EmitDatePart(EmitContext ctx, string part, string strftime, IReadOnlyList<ExprNode> args)
    {
        // The binder admits ISO date *text* (SQLite date columns discover as text), but
        // EXTRACT is strictly typed on Oracle and Postgres — a text argument needs an
        // explicit conversion there. SQL Server converts ISO text implicitly (yyyy-MM-dd
        // is language-neutral) and SQLite's strftime takes text natively. The format
        // strings are ours, not client data.
        var arg = args[0];
        var textual = arg.Kind == ColumnKind.Text || arg is NullLit;

        switch (ctx.Dialect)
        {
            case ReportDialect.SqlServer:
                EmitPlain(ctx, part, args);
                break;

            case ReportDialect.Oracle:
                ctx.Append("EXTRACT(").Append(part).Append(" FROM ");
                if (textual)
                {
                    // Date-part extraction only needs the date: the first 10 chars of
                    // any ISO string, parsed with an explicit mask (never NLS-dependent).
                    ctx.Append("TO_DATE(SUBSTR(");
                    ctx.Visit(arg);
                    ctx.Append(", 1, 10), 'YYYY-MM-DD')");
                }
                else
                {
                    ctx.Visit(arg);
                }
                ctx.Append(')');
                break;

            case ReportDialect.Postgres:
                ctx.Append("EXTRACT(").Append(part).Append(" FROM ");
                if (textual)
                {
                    // ISO text, date-only or full timestamp, casts cleanly regardless of DateStyle.
                    ctx.Append("CAST(");
                    ctx.Visit(arg);
                    ctx.Append(" AS TIMESTAMP)");
                }
                else
                {
                    ctx.Visit(arg);
                }
                ctx.Append(')');
                break;

            case ReportDialect.Sqlite:
                ctx.Append("CAST(strftime('").Append(strftime).Append("', ");
                ctx.Visit(arg);
                ctx.Append(") AS INTEGER)");
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(ctx), ctx.Dialect, null);
        }
    }
}
