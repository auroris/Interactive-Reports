using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Expressions;

/// <summary>
/// Typed AST for the computed-column expression language. Only the parser constructs
/// these — client text never reaches SQL; only this tree does, via ExprEmitter.
/// Column references resolve against the BASE schema only: computed columns cannot
/// reference each other (no dependency ordering in v1).
/// </summary>
public abstract record ExprNode
{
    public abstract ColumnKind Kind { get; }
}

public sealed record NumberLit(decimal Value) : ExprNode
{
    public override ColumnKind Kind => ColumnKind.Number;
}

public sealed record StringLit(string Value) : ExprNode
{
    public override ColumnKind Kind => ColumnKind.Text;
}

public sealed record ColumnRef(ColumnModel Column) : ExprNode
{
    public override ColumnKind Kind => Column.Kind;
}

public sealed record UnaryMinus(ExprNode Operand) : ExprNode
{
    public override ColumnKind Kind => ColumnKind.Number;
}

/// <summary>Op is one of + - * / (numeric, Kind=Number) or || (concat, Kind=Text).</summary>
public sealed record BinaryOp(string Op, ExprNode Left, ExprNode Right) : ExprNode
{
    public override ColumnKind Kind => Op == "||" ? ColumnKind.Text : ColumnKind.Number;
}

public sealed record FuncCall(ExprFn Fn, IReadOnlyList<ExprNode> Args, ColumnKind ResultKind) : ExprNode
{
    public override ColumnKind Kind => ResultKind;
}

public enum ExprFn
{
    Upper,
    Lower,
    Trim,
    Length,
    Substr,
    Concat,
    Round,
    Abs,
    Coalesce,
    Year,
    Month,
    Day,
}
