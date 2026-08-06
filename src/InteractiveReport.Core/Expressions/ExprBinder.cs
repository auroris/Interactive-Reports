using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Expressions;

/// <summary>
/// Stage 2: bind the untyped syntax tree against the discovered schema and the
/// function registry, producing the typed AST. Every rejection is a message about
/// the client's own input, carrying the source position where it helps.
///
/// The type discipline in one paragraph: values are number/text/date (plus Other
/// for odd provider types); conditions (Bool) arise from comparisons, IS [NOT]
/// NULL, AND/OR/NOT, and are consumed by searched-CASE WHENs and by NOT/AND/OR —
/// nowhere else. NULL is a value of every type; its type comes from context
/// (COALESCE/CASE unification), and an expression that is nothing but NULL cannot
/// be typed and is rejected. Comparing against NULL with '=' is rejected with a
/// pointer to IS NULL — SQL would silently yield no matches, and silence is the
/// one thing a validation layer must never emit.
/// </summary>
internal static class ExprBinder
{
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
            CaseSyntax c => BindCase(c, schema),
            _ => throw new InvalidOperationException($"unhandled syntax node {syntax.GetType().Name}"),
        };

    private static ExprNode BindName(NameSyntax name, IReadOnlyDictionary<string, ColumnModel> schema)
        => schema.TryGetValue(name.Name, out var column)
            ? new ColumnRef(column)
            : throw new ExprError($"unknown column '{name.Name}' (computed columns cannot reference other computed columns)");

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

    private static ExprNode BindUnary(UnarySyntax u, IReadOnlyDictionary<string, ColumnModel> schema)
    {
        var operand = Bind(u.Operand, schema);
        if (u.Op == "NOT")
        {
            if (operand.Kind != ColumnKind.Bool)
                throw new ExprError($"NOT requires a condition at position {u.Pos + 1}");
            return new NotOp(operand);
        }

        if (operand.Kind != ColumnKind.Number)
            throw new ExprError("unary '-' requires a number operand");
        return new UnaryMinus(operand);
    }

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

            default:
                if (left.Kind != ColumnKind.Number || right.Kind != ColumnKind.Number)
                    throw new ExprError(
                        $"operator '{b.Op}' requires number operands (got {KindName(left)} and {KindName(right)})");
                return new BinaryOp(b.Op, left, right);
        }
    }

    private static ExprNode BindComparison(BinarySyntax b, ExprNode left, ExprNode right)
    {
        if (left.Kind == ColumnKind.Bool || right.Kind == ColumnKind.Bool)
            throw new ExprError($"'{b.Op}' cannot compare conditions (chained comparisons are not supported) at position {b.Pos + 1}");
        if (left is NullLit || right is NullLit)
            throw new ExprError($"'{b.Op} NULL' never matches — use IS NULL or IS NOT NULL at position {b.Pos + 1}");

        if (left.Kind != right.Kind)
            throw new ExprError(
                $"'{b.Op}' compares values of the same type (got {KindName(left)} and {KindName(right)}) at position {b.Pos + 1}");

        return new Comparison(b.Op, left, right);
    }

    private static ExprNode BindNullTest(NullTestSyntax t, IReadOnlyDictionary<string, ColumnModel> schema)
    {
        var operand = Bind(t.Operand, schema);
        if (operand.Kind == ColumnKind.Bool)
            throw new ExprError($"IS NULL tests a value, not a condition, at position {t.Pos + 1}");
        if (operand is NullLit)
            throw new ExprError($"IS NULL tests a column or expression, not the NULL literal, at position {t.Pos + 1}");
        return new NullTest(operand, t.Negated);
    }

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
                if (when.Kind != operand.Kind)
                    throw new ExprError(
                        $"CASE WHEN value must match the CASE operand's type (got {KindName(when)}, expected {KindName(operand)})");
            }
            branches.Add(new CaseBranch(when, Bind(clause.Then, schema)));
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

    private static void RequireConcatable(ExprNode node, string where)
    {
        if (node is NullLit) return; // NULL concatenates as empty on every dialect
        if (node.Kind is not (ColumnKind.Text or ColumnKind.Number or ColumnKind.Date))
            throw new ExprError($"{where}: cannot concatenate a {KindName(node)} value");
    }

    private static string KindName(ExprNode node)
        => node.Kind == ColumnKind.Bool ? "condition" : node.Kind.ToString().ToLowerInvariant();
}
