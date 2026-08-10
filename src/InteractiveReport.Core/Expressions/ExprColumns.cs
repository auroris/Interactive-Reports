namespace InteractiveReport.Core.Expressions;

/// <summary>
/// Collects the column names a bound expression references. Used to decide whether a
/// group-layer computed column can participate in a query whose grouping drops some
/// of its inputs (spread totals re-aggregate by column dimensions only).
/// </summary>
public static class ExprColumns
{
    public static IReadOnlySet<string> Collect(ExprNode node)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Collect(node, names);
        return names;
    }

    private static void Collect(ExprNode node, HashSet<string> into)
    {
        switch (node)
        {
            case ColumnRef reference:
                into.Add(reference.Column.Name);
                break;
            case UnaryMinus unary:
                Collect(unary.Operand, into);
                break;
            case NotOp not:
                Collect(not.Operand, into);
                break;
            case NullTest test:
                Collect(test.Operand, into);
                break;
            case BinaryOp binary:
                Collect(binary.Left, into);
                Collect(binary.Right, into);
                break;
            case Comparison comparison:
                Collect(comparison.Left, into);
                Collect(comparison.Right, into);
                break;
            case LogicalOp logical:
                Collect(logical.Left, into);
                Collect(logical.Right, into);
                break;
            case Between between:
                Collect(between.Operand, into);
                Collect(between.Lower, into);
                Collect(between.Upper, into);
                break;
            case DateAdd dateAdd:
                Collect(dateAdd.Date, into);
                Collect(dateAdd.Days, into);
                break;
            case CaseWhen caseWhen:
                if (caseWhen.Operand is { } operand) Collect(operand, into);
                foreach (var branch in caseWhen.Branches)
                {
                    Collect(branch.When, into);
                    Collect(branch.Then, into);
                }
                if (caseWhen.Else is { } elseNode) Collect(elseNode, into);
                break;
            case FuncCall call:
                foreach (var argument in call.Args) Collect(argument, into);
                break;
        }
    }
}
