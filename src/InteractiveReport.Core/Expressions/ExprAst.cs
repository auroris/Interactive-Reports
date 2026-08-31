using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Expressions;

/// <summary>
/// Stage 2 output of the expression pipeline: the typed AST. Only the binder constructs these;
/// never reaches SQL; only this tree does, via ExprEmitter. Column references
/// resolve against the schema at the current canonical operation. Computed-column
/// dependencies are topologically scheduled, so a later bound tree may reference
/// an earlier computed output.
///
/// ColumnKind.Bool is internal to the tree: conditions exist inside CASE/NOT/AND/OR
/// and never escape as a computed column's result (SQL Server has no scalar
/// boolean; the portable subset therefore doesn't either).
/// </summary>
public abstract record ExprNode
{
    /// <summary>Gets the portable result kind inferred by the binder.</summary>
    public abstract ColumnKind Kind { get; }
}

/// <summary>A decimal numeric literal.</summary>
/// <param name="Value">The parsed literal value.</param>
public sealed record NumberLit(decimal Value) : ExprNode
{
    public override ColumnKind Kind => ColumnKind.Number;
}

/// <summary>A text literal after quote unescaping.</summary>
/// <param name="Value">The parsed literal value.</param>
public sealed record StringLit(string Value) : ExprNode
{
    public override ColumnKind Kind => ColumnKind.Text;
}

/// <summary>The NULL literal. Its kind is contextual and never matters to emission.</summary>
public sealed record NullLit : ExprNode
{
    public override ColumnKind Kind => ColumnKind.Other;
}

/// <summary>
/// A schema-bound column. <see cref="AssumedKind"/> is present only when the provider could not
/// describe a raw source expression and a comparison context supplied its scalar
/// type. The source column remains unchanged, including its logical identity.
/// </summary>
/// <param name="Column">The canonical schema column.</param>
public sealed record ColumnRef(ColumnModel Column) : ExprNode
{
    /// <summary>Gets the contextual kind inferred for a provider-unknown source expression.</summary>
    internal ColumnKind? AssumedKind { get; init; }

    public override ColumnKind Kind => AssumedKind ?? Column.Kind;
}

/// <summary>Numeric negation of one operand.</summary>
/// <param name="Operand">The numeric expression to negate.</param>
public sealed record UnaryMinus(ExprNode Operand) : ExprNode
{
    public override ColumnKind Kind => ColumnKind.Number;
}

/// <summary>Stores <c>+</c>, <c>-</c>, <c>*</c>, or <c>/</c> for numbers, or <c>||</c> for text concatenation.</summary>
/// <param name="Op">The normalized operator token.</param>
/// <param name="Left">The left operand.</param>
/// <param name="Right">The right operand.</param>
public sealed record BinaryOp(string Op, ExprNode Left, ExprNode Right) : ExprNode
{
    public override ColumnKind Kind => Op == "||" ? ColumnKind.Text : ColumnKind.Number;
}

/// <summary>A scalar comparison. <paramref name="Op"/> is one of =, &lt;&gt;, &lt;, &lt;=, &gt;, or &gt;=; the lexer normalizes != to &lt;&gt;.</summary>
/// <param name="Op">The normalized comparison operator.</param>
/// <param name="Left">The left comparand.</param>
/// <param name="Right">The right comparand.</param>
public sealed record Comparison(string Op, ExprNode Left, ExprNode Right) : ExprNode
{
    public override ColumnKind Kind => ColumnKind.Bool;
}

/// <summary>
/// Between: x BETWEEN lower AND upper — inclusive at both boundaries, bounds emitted as
/// written (reversed bounds are not reordered). All three operands share one kind.
/// </summary>
/// <param name="Operand">The value being tested.</param>
/// <param name="Lower">The inclusive lower bound.</param>
/// <param name="Upper">The inclusive upper bound.</param>
public sealed record Between(ExprNode Operand, ExprNode Lower, ExprNode Upper) : ExprNode
{
    public override ColumnKind Kind => ColumnKind.Bool;
}

/// <summary>
/// Date add: date + days / date - days, whole calendar days only (Op is + or -). The binder
/// admits Days only when its integrality is established; a separate node because
/// every dialect has its own date-arithmetic idiom.
/// </summary>
/// <param name="Op">Either <c>+</c> or <c>-</c>.</param>
/// <param name="Date">The date-valued left operand.</param>
/// <param name="Days">The expression proven to represent whole days.</param>
public sealed record DateAdd(string Op, ExprNode Date, ExprNode Days) : ExprNode
{
    public override ColumnKind Kind => ColumnKind.Date;
}

/// <summary>Combines two conditions with AND or OR.</summary>
/// <param name="Op">The canonical AND or OR token.</param>
/// <param name="Left">The left condition.</param>
/// <param name="Right">The right condition.</param>
public sealed record LogicalOp(string Op, ExprNode Left, ExprNode Right) : ExprNode
{
    public override ColumnKind Kind => ColumnKind.Bool;
}

/// <summary>Negates a boolean condition.</summary>
/// <param name="Operand">The condition to negate.</param>
public sealed record NotOp(ExprNode Operand) : ExprNode
{
    public override ColumnKind Kind => ColumnKind.Bool;
}

/// <summary>Tests an expression with IS NULL or IS NOT NULL.</summary>
/// <param name="Operand">The value to test.</param>
/// <param name="Negated">Whether to emit IS NOT NULL instead of IS NULL.</param>
public sealed record NullTest(ExprNode Operand, bool Negated) : ExprNode
{
    public override ColumnKind Kind => ColumnKind.Bool;
}

/// <summary>Contains one WHEN/THEN branch in a simple or searched CASE expression.</summary>
/// <param name="When">The match value or condition.</param>
/// <param name="Then">The value returned when the branch matches.</param>
public sealed record CaseBranch(ExprNode When, ExprNode Then);

/// <summary>
/// Represents CASE. A null operand means searched CASE, whose branch Whens are conditions; a non-null operand means simple
/// CASE (branch Whens are values compared to the operand — note SQL equality:
/// WHEN NULL never matches, which is why the binder rejects it). ResultKind is
/// inferred by unifying the THEN/ELSE branches, ignoring NULLs.
/// </summary>
/// <param name="Operand">The simple-CASE operand, or <see langword="null"/> for searched CASE.</param>
/// <param name="Branches">The ordered WHEN/THEN branches.</param>
/// <param name="Else">The optional ELSE expression.</param>
/// <param name="ResultKind">The result kind inferred from non-null THEN and ELSE values.</param>
public sealed record CaseWhen(
    ExprNode? Operand,
    IReadOnlyList<CaseBranch> Branches,
    ExprNode? Else,
    ColumnKind ResultKind) : ExprNode
{
    public override ColumnKind Kind => ResultKind;
}

/// <summary>A portable function call whose result kind was inferred by the binder.</summary>
/// <param name="Name">The canonical <see cref="ExprFunctions"/> registry key.</param>
/// <param name="Args">The bound arguments in call order.</param>
/// <param name="ResultKind">The function result kind inferred by the binder.</param>
public sealed record FuncCall(string Name, IReadOnlyList<ExprNode> Args, ColumnKind ResultKind) : ExprNode
{
    public override ColumnKind Kind => ResultKind;
}
