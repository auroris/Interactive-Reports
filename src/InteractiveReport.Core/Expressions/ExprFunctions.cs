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
                    a.Require(i, "text, number, or date", ColumnKind.Text, ColumnKind.Number, ColumnKind.Date);
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

    private static void EmitDatePart(EmitContext ctx, string part, string strftime, IReadOnlyList<ExprNode> args)
    {
        switch (ctx.Dialect)
        {
            case ReportDialect.SqlServer:
                EmitPlain(ctx, part, args);
                break;

            case ReportDialect.Oracle or ReportDialect.Postgres:
                ctx.Append("EXTRACT(").Append(part).Append(" FROM ");
                ctx.Visit(args[0]);
                ctx.Append(')');
                break;

            case ReportDialect.Sqlite:
                ctx.Append("CAST(strftime('").Append(strftime).Append("', ");
                ctx.Visit(args[0]);
                ctx.Append(") AS INTEGER)");
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(ctx), ctx.Dialect, null);
        }
    }
}
