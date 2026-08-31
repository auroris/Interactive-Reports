using System.Globalization;
using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Expressions;

/// <summary>
/// One entry in the portable function registry, carrying
/// arity, argument rules, result-kind inference, and the per-dialect emitter.
/// Adding a function is adding a row here — no enum, no switches to grow.
/// </summary>
/// <param name="Name">The canonical function name.</param>
/// <param name="MinArgs">The minimum accepted argument count.</param>
/// <param name="MaxArgs">The maximum accepted argument count.</param>
/// <param name="Bind">The callback that validates bound arguments and infers the result kind.</param>
/// <param name="Emit">The callback that emits dialect-specific SQL.</param>
internal sealed record FunctionDef(
    string Name,
    int MinArgs,
    int MaxArgs,
    Func<FunctionArgs, ColumnKind> Bind,
    Action<EmitContext, IReadOnlyList<ExprNode>> Emit);

/// <summary>Provides bound, typed arguments to a function's binding rule.</summary>
/// <param name="name">The canonical function name used in diagnostics.</param>
/// <param name="args">The bound call arguments.</param>
internal readonly struct FunctionArgs(string name, IReadOnlyList<ExprNode> args)
{
    /// <summary>Gets the canonical function name.</summary>
    public string Name { get; } = name;
    /// <summary>Gets the bound arguments in call order.</summary>
    public IReadOnlyList<ExprNode> Args { get; } = args;

    /// <summary>
    /// Requires one argument to have an allowed kind. NULL literals satisfy every requirement.
    /// </summary>
    /// <param name="index">The zero-based argument index.</param>
    /// <param name="what">The human-readable subject included in the validation message.</param>
    /// <param name="kinds">The allowed column kinds against which to validate the value.</param>
    /// <exception cref="ExprError">Thrown when the non-null argument has no allowed kind.</exception>
    public void Require(int index, string what, params ColumnKind[] kinds)
    {
        var arg = Args[index];
        if (arg is NullLit) return;
        if (!kinds.Contains(arg.Kind))
            throw new ExprError($"{Name} argument {index + 1} must be {what}");
    }
}

/// <summary>Owns the closed portable function vocabulary and its binding and emission behavior.</summary>
internal static class ExprFunctions
{
    /// <summary>Gets registered function names in case-insensitive alphabetical order.</summary>
    public static IReadOnlyList<string> Names { get; private set; }

    /// <summary>
    /// Attempts to resolve a portable expression function by its case-insensitive name.
    /// </summary>
    /// <param name="name">The case-insensitive function name.</param>
    /// <param name="def">Receives the function definition when registered.</param>
    /// <returns><see langword="true"/> when the function is registered and was returned; otherwise, <see langword="false"/>.</returns>
    public static bool TryGet(string name, out FunctionDef def) => Registry.TryGetValue(name, out def!);

    /// <summary>
    /// Returns a registered expression function by name.
    /// </summary>
    /// <param name="name">The case-insensitive registered function name.</param>
    /// <returns>The resolved function definition.</returns>
    public static FunctionDef Get(string name) => Registry[name];

    private static readonly Dictionary<string, FunctionDef> Registry =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers an expression function under its case-insensitive name.
    /// </summary>
    /// <param name="name">The canonical function name.</param>
    /// <param name="min">The minimum accepted argument count.</param>
    /// <param name="max">The maximum accepted argument count.</param>
    /// <param name="bind">The callback that validates and binds function arguments.</param>
    /// <param name="emit">The optional dialect emitter; omission uses a plain function call.</param>
    /// <remarks>Replaces any existing entry with the same case-insensitive name.</remarks>
    private static void Add(string name, int min, int max,
        Func<FunctionArgs, ColumnKind> bind,
        Action<EmitContext, IReadOnlyList<ExprNode>>? emit = null)
        => Registry[name] = new FunctionDef(name, min, max, bind,
            emit ?? ((ctx, args) => ExprFunctionEmitter.EmitPlain(ctx, name, args)));

    /// <summary>
    /// Populates the portable expression-function registry and publishes its sorted name list.
    /// </summary>
    static ExprFunctions()
    {
        Add("UPPER", 1, 1, a => { a.Require(0, "text", ColumnKind.Text); return ColumnKind.Text; });
        Add("LOWER", 1, 1, a => { a.Require(0, "text", ColumnKind.Text); return ColumnKind.Text; });
        Add("TRIM", 1, 1, a => { a.Require(0, "text", ColumnKind.Text); return ColumnKind.Text; });

        Add("LENGTH", 1, 1,
            a => { a.Require(0, "text", ColumnKind.Text); return ColumnKind.Number; },
            (ctx, args) => ExprFunctionEmitter.EmitPlain(
                ctx,
                ctx.Dialect == ReportDialect.SqlServer ? "LEN" : "LENGTH",
                args));

        Add("SUBSTR", 2, 3,
            a =>
            {
                a.Require(0, "text", ColumnKind.Text);
                a.Require(1, "a number", ColumnKind.Number);
                if (a.Args.Count == 3) a.Require(2, "a number", ColumnKind.Number);
                return ColumnKind.Text;
            },
            ExprFunctionEmitter.EmitSubstr);

        Add("CONCAT", 2, 8,
            a =>
            {
                for (var i = 0; i < a.Args.Count; i++)
                    a.Require(i, "text or a number (dates go through TO_STRING)", ColumnKind.Text, ColumnKind.Number);
                return ColumnKind.Text;
            },
            ExprFunctionEmitter.EmitConcat);

        Add("ROUND", 1, 2,
            a =>
            {
                a.Require(0, "a number", ColumnKind.Number);
                if (a.Args.Count == 2) a.Require(1, "a number", ColumnKind.Number);
                return ColumnKind.Number;
            },
            ExprFunctionEmitter.EmitRound);

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

        Add("CONTAINS", 2, 2, TextPredicateBind("CONTAINS"),
            (ctx, args) => ExprFunctionEmitter.EmitTextMatch(ctx, args, leadingWildcard: true, trailingWildcard: true));
        Add("STARTS_WITH", 2, 2, TextPredicateBind("STARTS_WITH"),
            (ctx, args) => ExprFunctionEmitter.EmitTextMatch(ctx, args, leadingWildcard: false, trailingWildcard: true));
        Add("ENDS_WITH", 2, 2, TextPredicateBind("ENDS_WITH"),
            (ctx, args) => ExprFunctionEmitter.EmitTextMatch(ctx, args, leadingWildcard: true, trailingWildcard: false));
        Add("IN_LIST", 2, 1001,
            a =>
            {
                if (a.Args[0] is NullLit)
                    throw new ExprError("IN_LIST cannot infer a type from a NULL first argument");
                for (var i = 1; i < a.Args.Count; i++)
                    a.Require(i, $"the same type as argument 1 ({a.Args[0].Kind.ToString().ToLowerInvariant()})", a.Args[0].Kind);
                return ColumnKind.Bool;
            },
            ExprFunctionEmitter.EmitInList);

        Add("YEAR", 1, 1, DatePartBind("YEAR"),
            (ctx, args) => ExprFunctionEmitter.EmitDatePart(ctx, "YEAR", "%Y", args));
        Add("MONTH", 1, 1, DatePartBind("MONTH"),
            (ctx, args) => ExprFunctionEmitter.EmitDatePart(ctx, "MONTH", "%m", args));
        Add("DAY", 1, 1, DatePartBind("DAY"),
            (ctx, args) => ExprFunctionEmitter.EmitDatePart(ctx, "DAY", "%d", args));

        Add("NOW", 0, 0, _ => ColumnKind.Date, ExprFunctionEmitter.EmitNow);

        Add("TO_DATE", 1, 1,
            a =>
            {
                a.Require(0, "text or a date", ColumnKind.Text, ColumnKind.Date);
                // Provider constraint: literals are checkable right here; column contents are
                // the ISO data contract (invalid rows become a provider error or NULL at
                // runtime).
                if (a.Args[0] is StringLit s && !DateTime.TryParseExact(s.Value, "yyyy-MM-dd",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                    throw new ExprError($"TO_DATE text must be ISO YYYY-MM-DD (got '{s.Value}')");
                return ColumnKind.Date;
            },
            ExprFunctionEmitter.EmitToDate);

        Add("DATE_TRUNC", 2, 2,
            a =>
            {
                ExprDateRules.TruncUnit(a.Args[0]);
                a.Require(1, "a date — convert text with TO_DATE first", ColumnKind.Date);
                return ColumnKind.Date;
            },
            ExprFunctionEmitter.EmitDateTrunc);

        Add("TO_STRING", 1, 2,
            a =>
            {
                a.Require(0, "a date", ColumnKind.Date);
                if (a.Args.Count == 2)
                    ExprDateRules.ParseDateFormat(ExprDateRules.FormatLiteral(a.Args[1]));
                return ColumnKind.Text;
            },
            ExprFunctionEmitter.EmitToString);

        Names = Array.AsReadOnly(Registry.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    /// <summary>
    /// Binds a date-part function call to its typed expression contract.
    /// </summary>
    /// <param name="name">The canonical function name used in diagnostics.</param>
    /// <returns>A binder that accepts one date or ISO date-text argument and returns a number kind.</returns>
    private static Func<FunctionArgs, ColumnKind> DatePartBind(string name) => a =>
    {
        // Provider constraint: text allowed: SQLite date columns discover as text (ISO
        // strings).
        a.Require(0, "a date (or ISO date text)", ColumnKind.Date, ColumnKind.Text);
        return ColumnKind.Number;
    };

    /// <summary>
    /// Binds a text predicate call to its typed expression contract.
    /// </summary>
    /// <param name="name">The canonical function name used in diagnostics.</param>
    /// <returns>A binder that requires two text arguments and returns a boolean kind.</returns>
    private static Func<FunctionArgs, ColumnKind> TextPredicateBind(string name) => a =>
    {
        a.Require(0, "text", ColumnKind.Text);
        a.Require(1, "text", ColumnKind.Text);
        return ColumnKind.Bool;
    };

}
