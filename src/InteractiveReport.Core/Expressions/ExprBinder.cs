using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Expressions;

/// <summary>
/// Stage 2 of the portable expression pipeline: binds an untyped syntax tree against the discovered schema and
/// function registry, producing the typed AST. Every rejection is a message about
/// the client's own input, carrying the source position where it helps.
///
/// The type discipline in one paragraph: values are number/text/date (plus Other
/// for odd or provider-untyped values); conditions (Bool) arise from comparisons, BETWEEN,
/// IS [NOT] NULL, AND/OR/NOT, and are consumed by searched-CASE WHENs and by
/// NOT/AND/OR — nowhere else. NULL is a value of every type; its type comes from context
/// (COALESCE/CASE unification), and an expression that is nothing but NULL cannot
/// be typed and is rejected. Comparing against NULL with '=' is rejected with a
/// pointer to IS NULL — SQL would silently yield no matches, and silence is the
/// one thing a validation layer must never emit. A provider-untyped source column
/// likewise takes its type from comparison context; a concrete Other value does not.
/// </summary>
internal static class ExprBinder
{
    /// <summary>
    /// Recursively binds names, functions, operators, and CASE branches into a typed AST.
    /// </summary>
    /// <param name="syntax">The parsed syntax tree to bind.</param>
    /// <param name="schema">Case-insensitive columns visible at this transformation stage.</param>
    /// <returns>The typed AST node corresponding to <paramref name="syntax"/>.</returns>
    /// <exception cref="ExprError">Thrown when names, arity, operands, or result types violate the portable contract.</exception>
    /// <exception cref="InvalidOperationException">Thrown when no binder exists for the syntax-node type.</exception>
    public static ExprNode Bind(SyntaxNode syntax, IReadOnlyDictionary<string, ColumnModel> schema)
        => syntax switch
        {
            NumberSyntax n => new NumberLit(n.Value),
            StringSyntax s => new StringLit(s.Value),
            NullSyntax => new NullLit(),
            NameSyntax name => BindName(name, schema),
            CallSyntax call => BindCall(call, schema),
            UnarySyntax u => BindUnary(u, schema),
            BinarySyntax b => BindBinary(b, schema),
            NullTestSyntax t => BindNullTest(t, schema),
            BetweenSyntax bt => BindBetween(bt, schema),
            CaseSyntax c => BindCase(c, schema),
            _ => throw new InvalidOperationException($"unhandled syntax node {syntax.GetType().Name}"),
        };

    /// <summary>
    /// Resolves a name as a column visible in the current schema.
    /// </summary>
    /// <param name="name">The parsed name and source position.</param>
    /// <param name="schema">The columns visible at this stage.</param>
    /// <returns>A column-reference node retaining discovered metadata.</returns>
    /// <exception cref="ExprError">Thrown when no column matches.</exception>
    private static ExprNode BindName(NameSyntax name, IReadOnlyDictionary<string, ColumnModel> schema)
        => schema.TryGetValue(name.Name, out var column)
            ? new ColumnRef(column)
            : throw new ExprError($"unknown column '{name.Name}' at this transformation stage");

    /// <summary>
    /// Resolves a function, validates arity and argument categories, and infers its result kind.
    /// </summary>
    /// <param name="call">The parsed function-call syntax to bind.</param>
    /// <param name="schema">The columns visible to nested argument expressions.</param>
    /// <returns>A typed function-call node using the registry's canonical function name.</returns>
    /// <exception cref="ExprError">Thrown for an unknown function, invalid arity, condition argument, or function-specific type error.</exception>
    private static ExprNode BindCall(CallSyntax call, IReadOnlyDictionary<string, ColumnModel> schema)
    {
        if (!ExprFunctions.TryGet(call.Name, out var fn))
            throw new ExprError($"unknown function '{call.Name}' at position {call.Pos + 1}");

        if (call.Args.Count < fn.MinArgs || call.Args.Count > fn.MaxArgs)
            throw new ExprError(fn.MinArgs == fn.MaxArgs
                ? $"{fn.Name} takes {fn.MinArgs} argument{(fn.MinArgs == 1 ? "" : "s")}, got {call.Args.Count}"
                : $"{fn.Name} takes {fn.MinArgs}–{fn.MaxArgs} arguments, got {call.Args.Count}");

        var args = call.Args.Select(a => Bind(a, schema)).ToList();
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i].Kind == ColumnKind.Bool)
                throw new ExprError($"{fn.Name} argument {i + 1} cannot be a condition");
        }

        var resultKind = fn.Bind(new FunctionArgs(fn.Name, args));
        return new FuncCall(fn.Name, args, resultKind);
    }

    /// <summary>
    /// Binds logical NOT or numeric negation and validates its operand category.
    /// </summary>
    /// <param name="u">The parsed unary-expression syntax to bind.</param>
    /// <param name="schema">The columns visible to the operand.</param>
    /// <returns>A typed NOT or unary-minus node.</returns>
    /// <exception cref="ExprError">Thrown when NOT receives a value or negation receives a non-number.</exception>
    private static ExprNode BindUnary(UnarySyntax u, IReadOnlyDictionary<string, ColumnModel> schema)
    {
        var operand = Bind(u.Operand, schema);
        if (u.Op == "NOT")
        {
            if (operand.Kind != ColumnKind.Bool)
                throw new ExprError($"NOT requires a condition at position {u.Pos + 1}");
            return new NotOp(operand);
        }

        if (!NumberContextAccepts(operand))
            throw new ExprError("unary '-' requires a number operand");
        return new UnaryMinus(operand);
    }

    /// <summary>
    /// Determines whether a node can participate in numeric context. NULL joins arithmetic as a value of the
    /// needed type.
    /// </summary>
    /// <param name="node">The bound operand to classify.</param>
    /// <returns><see langword="true"/> when the node is numeric or null-compatible with numeric context; otherwise, <see langword="false"/>.</returns>
    private static bool NumberContextAccepts(ExprNode node)
        => node is NullLit || node.Kind == ColumnKind.Number;

    /// <summary>
    /// Binds logical, comparison, concatenation, date, or numeric binary syntax.
    /// </summary>
    /// <param name="b">The parsed operator and operands.</param>
    /// <param name="schema">The columns visible to both operands.</param>
    /// <returns>The operator-specific typed AST node.</returns>
    /// <exception cref="ExprError">Thrown when operand categories or comparison types are incompatible.</exception>
    private static ExprNode BindBinary(BinarySyntax b, IReadOnlyDictionary<string, ColumnModel> schema)
    {
        var left = Bind(b.Left, schema);
        var right = Bind(b.Right, schema);

        switch (b.Op)
        {
            case "AND" or "OR":
                if (left.Kind != ColumnKind.Bool || right.Kind != ColumnKind.Bool)
                    throw new ExprError($"{b.Op} requires conditions on both sides at position {b.Pos + 1}");
                return new LogicalOp(b.Op, left, right);

            case "=" or "<>" or "<" or "<=" or ">" or ">=":
                return BindComparison(b, left, right);

            case "||":
                RequireConcatable(left, "left of '||'");
                RequireConcatable(right, "right of '||'");
                return new BinaryOp("||", left, right);

            case "+" or "-" when left.Kind == ColumnKind.Date || right.Kind == ColumnKind.Date:
                return BindDateArithmetic(b, left, right);

            default:
                if (!NumberContextAccepts(left) || !NumberContextAccepts(right))
                    throw new ExprError(
                        $"operator '{b.Op}' requires number operands (got {KindName(left)} and {KindName(right)})");
                return new BinaryOp(b.Op, left, right);
        }
    }

    /// <summary>
    /// Binds date ± days using whole calendar days only. The date stands on the left; the offset
    /// is a number whose integrality can be established — anything provably fractional or merely unprovable
    /// is rejected rather than silently truncated per dialect.
    /// </summary>
    /// <param name="b">The parsed plus/minus syntax and source position.</param>
    /// <param name="left">The bound date operand.</param>
    /// <param name="right">The bound day-count operand.</param>
    /// <returns>A typed date-add node.</returns>
    /// <exception cref="ExprError">Thrown for date/date operations, a right-side date, nonnumeric offset, or an offset not provably integral.</exception>
    private static ExprNode BindDateArithmetic(BinarySyntax b, ExprNode left, ExprNode right)
    {
        if (left.Kind == ColumnKind.Date && right.Kind == ColumnKind.Date)
            throw new ExprError(b.Op == "-"
                ? $"date - date is not supported at position {b.Pos + 1}"
                : $"two dates cannot be added at position {b.Pos + 1}");
        if (right.Kind == ColumnKind.Date)
            throw new ExprError(
                $"the date goes on the left of '{b.Op}' (number {b.Op} date is not supported) at position {b.Pos + 1}");
        if (!NumberContextAccepts(right))
            throw new ExprError(
                $"date {b.Op} offset must be a number of whole days (got {KindName(right)}) at position {b.Pos + 1}");

        RequireWholeDays(right, b.Pos);
        return new DateAdd(b.Op, left, right);
    }

    /// <summary>
    /// Establishes that a day-offset expression is whole: integer literals, integer-typed
    /// columns, integer-valued functions, and +/-/* combinations of those. Division — and anything else
    /// whose integrality cannot be established — is rejected. NULL passes: a NULL offset yields a NULL date
    /// on every dialect.
    /// </summary>
    /// <param name="node">The bound numeric offset expression to prove integral.</param>
    /// <param name="pos">The zero-based operator position used in diagnostics.</param>
    /// <exception cref="ExprError">Thrown when the value is fractional or integrality cannot be established structurally.</exception>
    private static void RequireWholeDays(ExprNode node, int pos)
    {
        switch (node)
        {
            case NullLit:
                return;
            case NumberLit n:
                if (decimal.Truncate(n.Value) != n.Value)
                    throw new ExprError(
                        $"date offsets are whole calendar days (got {n.Value}) at position {pos + 1}");
                return;
            case ColumnRef c when IsIntegerClrType(c.Column.ClrType):
                return;
            case UnaryMinus u:
                RequireWholeDays(u.Operand, pos);
                return;
            case BinaryOp { Op: "+" or "-" or "*" } b:
                RequireWholeDays(b.Left, pos);
                RequireWholeDays(b.Right, pos);
                return;
            case FuncCall { Name: "YEAR" or "MONTH" or "DAY" or "LENGTH" }:
                return;
            case FuncCall { Name: "ROUND", Args.Count: 1 }:
                return;
            case FuncCall { Name: "ABS" or "COALESCE" } f:
                foreach (var arg in f.Args) RequireWholeDays(arg, pos);
                return;
            case CaseWhen c:
                foreach (var branch in c.Branches) RequireWholeDays(branch.Then, pos);
                if (c.Else is not null) RequireWholeDays(c.Else, pos);
                return;
            default:
                throw new ExprError(
                    "date offsets are whole calendar days — this offset cannot be established as whole "
                    + $"(use an integer literal, an integer column, or wrap it in ROUND(…)) at position {pos + 1}");
        }
    }

    /// <summary>
    /// Determines whether a CLR type is one of the supported integer types.
    /// </summary>
    /// <param name="t">The CLR type to classify into a portable column kind.</param>
    /// <returns><see langword="true"/> when the CLR type represents an integer; otherwise, <see langword="false"/>.</returns>
    private static bool IsIntegerClrType(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        return t == typeof(byte) || t == typeof(sbyte) || t == typeof(short) || t == typeof(ushort)
            || t == typeof(int) || t == typeof(uint) || t == typeof(long) || t == typeof(ulong);
    }

    /// <summary>
    /// Binds a comparison after rejecting conditions, NULL equality, and incompatible value kinds.
    /// </summary>
    /// <param name="b">The parsed comparison operator and source position.</param>
    /// <param name="left">The bound left value.</param>
    /// <param name="right">The bound right value.</param>
    /// <returns>A typed comparison, with provider-unknown columns contextualized when possible.</returns>
    /// <exception cref="ExprError">Thrown for condition operands, NULL equality, or incompatible types.</exception>
    private static ExprNode BindComparison(BinarySyntax b, ExprNode left, ExprNode right)
    {
        if (left.Kind == ColumnKind.Bool || right.Kind == ColumnKind.Bool)
            throw new ExprError($"'{b.Op}' cannot compare conditions (chained comparisons are not supported) at position {b.Pos + 1}");
        if (left is NullLit || right is NullLit)
            throw new ExprError($"'{b.Op} NULL' never matches — use IS NULL or IS NOT NULL at position {b.Pos + 1}");

        if (!TryContextualizeComparableValues(
                [left, right],
                out var operands,
                out _,
                out _))
            throw new ExprError(
                $"'{b.Op}' compares values of the same type (got {KindName(left)} and {KindName(right)}) at position {b.Pos + 1}");

        return new Comparison(b.Op, operands[0], operands[1]);
    }

    /// <summary>
    /// Binds an inclusive BETWEEN predicate and unifies the value and bound types.
    /// </summary>
    /// <param name="b">The parsed value, lower bound, upper bound, and source position.</param>
    /// <param name="schema">The columns visible to all three expressions.</param>
    /// <returns>A typed BETWEEN node, with provider-unknown columns contextualized when possible.</returns>
    /// <exception cref="ExprError">Thrown for conditions, NULL values, or incompatible types.</exception>
    private static ExprNode BindBetween(BetweenSyntax b, IReadOnlyDictionary<string, ColumnModel> schema)
    {
        var operand = Bind(b.Operand, schema);
        var lower = Bind(b.Lower, schema);
        var upper = Bind(b.Upper, schema);

        if (operand.Kind == ColumnKind.Bool || lower.Kind == ColumnKind.Bool || upper.Kind == ColumnKind.Bool)
            throw new ExprError($"BETWEEN cannot compare conditions at position {b.Pos + 1}");
        if (operand is NullLit || lower is NullLit || upper is NullLit)
            throw new ExprError(
                $"'BETWEEN NULL' never matches — use IS NULL or IS NOT NULL at position {b.Pos + 1}");
        if (!TryContextualizeComparableValues(
                [operand, lower, upper],
                out var operands,
                out _,
                out _))
            throw new ExprError(
                $"BETWEEN needs the value and both bounds to share one type (got {KindName(operand)}, {KindName(lower)}, and {KindName(upper)}) at position {b.Pos + 1}");

        return new Between(operands[0], operands[1], operands[2]);
    }

    /// <summary>
    /// Binds IS NULL or IS NOT NULL over a value expression.
    /// </summary>
    /// <param name="t">The parsed operand, negation flag, and source position.</param>
    /// <param name="schema">The columns visible to the operand.</param>
    /// <returns>A typed null-test predicate.</returns>
    /// <exception cref="ExprError">Thrown when the operand is a condition or the literal NULL itself.</exception>
    private static ExprNode BindNullTest(NullTestSyntax t, IReadOnlyDictionary<string, ColumnModel> schema)
    {
        var operand = Bind(t.Operand, schema);
        if (operand.Kind == ColumnKind.Bool)
            throw new ExprError($"IS NULL tests a value, not a condition, at position {t.Pos + 1}");
        if (operand is NullLit)
            throw new ExprError($"IS NULL tests a column or expression, not the NULL literal, at position {t.Pos + 1}");
        return new NullTest(operand, t.Negated);
    }

    /// <summary>
    /// Binds simple or searched CASE syntax and unifies comparison and result types.
    /// </summary>
    /// <param name="c">The parsed CASE operand, branches, optional else, and source position.</param>
    /// <param name="schema">The columns visible to every CASE expression.</param>
    /// <returns>A typed CASE node whose result kind is shared by all non-null branches.</returns>
    /// <exception cref="ExprError">Thrown for invalid WHEN categories, incompatible comparison/results, or an all-NULL result.</exception>
    private static ExprNode BindCase(CaseSyntax c, IReadOnlyDictionary<string, ColumnModel> schema)
    {
        var operand = c.Operand is null ? null : Bind(c.Operand, schema);
        if (operand is NullLit)
            throw new ExprError($"CASE NULL never matches any WHEN — use a searched CASE at position {c.Pos + 1}");
        if (operand?.Kind == ColumnKind.Bool)
            throw new ExprError($"simple CASE compares values; write the condition inside WHEN instead, at position {c.Pos + 1}");

        var branches = new List<CaseBranch>(c.Whens.Count);
        foreach (var clause in c.Whens)
        {
            var when = Bind(clause.When, schema);
            if (operand is null)
            {
                // Searched CASE: WHENs are conditions.
                if (when.Kind != ColumnKind.Bool)
                    throw new ExprError(
                        $"CASE WHEN needs a condition — a comparison, IS NULL, or AND/OR (got {KindName(when)})");
            }
            else
            {
                // Simple CASE: WHENs are values compared to the operand with SQL equality.
                if (when is NullLit)
                    throw new ExprError("WHEN NULL never matches in a simple CASE — use a searched CASE with IS NULL");
                if (when.Kind == ColumnKind.Bool)
                    throw new ExprError("simple CASE WHEN takes a value, not a condition");
                if (!IsProviderUnknown(operand)
                    && !IsProviderUnknown(when)
                    && when.Kind != operand.Kind)
                    throw new ExprError(
                        $"CASE WHEN value must match the CASE operand's type (got {KindName(when)}, expected {KindName(operand)})");
            }
            branches.Add(new CaseBranch(when, Bind(clause.Then, schema)));
        }

        if (operand is not null)
        {
            var values = new ExprNode[branches.Count + 1];
            values[0] = operand;
            for (var index = 0; index < branches.Count; index++)
                values[index + 1] = branches[index].When;

            if (!TryContextualizeComparableValues(
                    values,
                    out var contextualValues,
                    out var expectedKind,
                    out var mismatch))
                throw new ExprError(
                    $"CASE WHEN value must match the CASE operand's type (got {KindName(mismatch!)}, expected {expectedKind.ToString().ToLowerInvariant()})");

            operand = contextualValues[0];
            for (var index = 0; index < branches.Count; index++)
                branches[index] = branches[index] with { When = contextualValues[index + 1] };
        }

        var elseNode = c.Else is null ? null : Bind(c.Else, schema);

        // Result type: unify THEN/ELSE branches, letting NULLs join any type.
        ColumnKind? resultKind = null;
        var branchIndex = 0;
        foreach (var result in branches.Select(br => br.Then).Concat(elseNode is null ? [] : [elseNode]))
        {
            branchIndex++;
            if (result is NullLit) continue;
            if (result.Kind == ColumnKind.Bool)
                throw new ExprError(
                    "a CASE branch cannot return a condition — wrap it in CASE WHEN <condition> THEN 1 ELSE 0 END");
            if (resultKind is null) { resultKind = result.Kind; continue; }
            if (result.Kind != resultKind)
                throw new ExprError(
                    $"CASE branches must all return the same type (branch {branchIndex} is {result.Kind.ToString().ToLowerInvariant()}, expected {resultKind.Value.ToString().ToLowerInvariant()})");
        }
        if (resultKind is null)
            throw new ExprError("CASE cannot infer its result type (every branch is NULL)");

        return new CaseWhen(operand, branches, elseNode, resultKind.Value);
    }

    /// <summary>
    /// Tries to unify values that SQL will compare. A provider-unknown source
    /// column is a type variable, not a concrete Other value: the first concrete operand gives it comparison
    /// context. Concrete non-portable Other values remain strict and therefore cannot be compared to text,
    /// numbers, or dates accidentally.
    /// </summary>
    /// <param name="values">The non-empty bound values participating in one comparison family.</param>
    /// <param name="contextualValues">Receives copies in which provider-unknown columns assume the concrete anchor kind.</param>
    /// <param name="expectedKind">The concrete column kind required by contextual binding.</param>
    /// <param name="mismatch">Receives the first concrete value that disagrees with the anchor kind.</param>
    /// <returns><see langword="true"/> when the values can share one comparison type; otherwise, <see langword="false"/>.</returns>
    private static bool TryContextualizeComparableValues(
        IReadOnlyList<ExprNode> values,
        out ExprNode[] contextualValues,
        out ColumnKind expectedKind,
        out ExprNode? mismatch)
    {
        var anchor = values.FirstOrDefault(value => !IsProviderUnknown(value));
        if (anchor is null)
        {
            contextualValues = values.ToArray();
            expectedKind = values[0].Kind;
            mismatch = null;
            return true;
        }

        var anchorKind = anchor.Kind;
        expectedKind = anchorKind;
        mismatch = values.FirstOrDefault(value =>
            !IsProviderUnknown(value) && value.Kind != anchorKind);
        if (mismatch is not null)
        {
            contextualValues = values.ToArray();
            return false;
        }

        contextualValues = values
            .Select(value => IsProviderUnknown(value)
                ? ((ColumnRef)value) with { AssumedKind = anchorKind }
                : value)
            .ToArray();
        return true;
    }

    /// <summary>
    /// Determines whether a provider value lacks enough type information for binding.
    /// </summary>
    /// <param name="node">The bound value to inspect.</param>
    /// <returns><see langword="true"/> for an uncontextualized column whose provider type was not recognized; otherwise, <see langword="false"/>.</returns>
    private static bool IsProviderUnknown(ExprNode node)
        => node is ColumnRef { AssumedKind: null, Column.HasKnownType: false };

    /// <summary>
    /// Requires a value that can be concatenated portably without session-dependent conversion.
    /// </summary>
    /// <param name="node">The bound concatenation operand.</param>
    /// <param name="where">The expression location included in type-validation diagnostics.</param>
    /// <exception cref="ExprError">Thrown for dates, conditions, Boolean values, or other unsupported kinds.</exception>
    private static void RequireConcatable(ExprNode node, string where)
    {
        if (node is NullLit) return; // NULL concatenates as empty on every supported dialect.
        if (node.Kind == ColumnKind.Date)
            // Implicit date-to-text follows engine settings (session language, NLS_DATE_FORMAT,
            // DateStyle) — the one place they would leak into output, so conversion stays
            // explicit.
            throw new ExprError(
                $"{where}: convert the date with TO_STRING(…) first — a bare date renders engine-dependent text");
        if (node.Kind is not (ColumnKind.Text or ColumnKind.Number))
            throw new ExprError($"{where}: cannot concatenate a {KindName(node)} value");
    }

    /// <summary>
    /// Returns the diagnostic name of a bound expression kind.
    /// </summary>
    /// <param name="node">The bound expression whose kind should be named.</param>
    /// <returns>The canonical column-kind name.</returns>
    private static string KindName(ExprNode node) => node switch
    {
        NullLit => "null",
        { Kind: ColumnKind.Bool } => "condition",
        _ => node.Kind.ToString().ToLowerInvariant(),
    };
}
