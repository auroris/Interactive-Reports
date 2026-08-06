using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Expressions;

/// <summary>
/// Stage 2 output: the typed AST. Only the binder constructs these — client text
/// never reaches SQL; only this tree does, via ExprEmitter. Column references
/// resolve against the BASE schema only: computed columns cannot reference each
/// other (no dependency ordering in v1).
///
/// ColumnKind.Bool is internal to the tree: conditions exist inside CASE/NOT/AND/OR
/// and never escape as a computed column's result (SQL Server has no scalar
/// boolean; the portable subset therefore doesn't either).
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

/// <summary>The NULL literal. Its kind is contextual and never matters to emission.</summary>
public sealed record NullLit : ExprNode
{
    public override ColumnKind Kind => ColumnKind.Other;
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

/// <summary>Op is one of = &lt;&gt; &lt; &lt;= &gt; &gt;= (!= is normalized to &lt;&gt; by the lexer).</summary>
public sealed record Comparison(string Op, ExprNode Left, ExprNode Right) : ExprNode
{
    public override ColumnKind Kind => ColumnKind.Bool;
}

/// <summary>
/// x BETWEEN lower AND upper — inclusive at both boundaries, bounds emitted as
/// written (reversed bounds are not reordered). All three operands share one kind.
/// </summary>
public sealed record Between(ExprNode Operand, ExprNode Lower, ExprNode Upper) : ExprNode
{
    public override ColumnKind Kind => ColumnKind.Bool;
}

/// <summary>
/// date + days / date - days, whole calendar days only (Op is + or -). The binder
/// admits Days only when its integrality is established; a separate node because
/// every dialect has its own date-arithmetic idiom.
/// </summary>
public sealed record DateAdd(string Op, ExprNode Date, ExprNode Days) : ExprNode
{
    public override ColumnKind Kind => ColumnKind.Date;
}

/// <summary>Op is AND or OR.</summary>
public sealed record LogicalOp(string Op, ExprNode Left, ExprNode Right) : ExprNode
{
    public override ColumnKind Kind => ColumnKind.Bool;
}

public sealed record NotOp(ExprNode Operand) : ExprNode
{
    public override ColumnKind Kind => ColumnKind.Bool;
}

public sealed record NullTest(ExprNode Operand, bool Negated) : ExprNode
{
    public override ColumnKind Kind => ColumnKind.Bool;
}

public sealed record CaseBranch(ExprNode When, ExprNode Then);

/// <summary>
/// Operand null = searched CASE (branch Whens are conditions); non-null = simple
/// CASE (branch Whens are values compared to the operand — note SQL equality:
/// WHEN NULL never matches, which is why the binder rejects it). ResultKind is
/// inferred by unifying the THEN/ELSE branches, ignoring NULLs.
/// </summary>
public sealed record CaseWhen(
    ExprNode? Operand,
    IReadOnlyList<CaseBranch> Branches,
    ExprNode? Else,
    ColumnKind ResultKind) : ExprNode
{
    public override ColumnKind Kind => ResultKind;
}

/// <summary>Name is the canonical registry key (see ExprFunctions); ResultKind was inferred by the binder.</summary>
public sealed record FuncCall(string Name, IReadOnlyList<ExprNode> Args, ColumnKind ResultKind) : ExprNode
{
    public override ColumnKind Kind => ResultKind;
}
