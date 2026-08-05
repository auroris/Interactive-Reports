using InteractiveReport.Core.Expressions;
using InteractiveReport.Core.Model;
using static InteractiveReport.Core.Tests.TestFixtures;

namespace InteractiveReport.Core.Tests;

public class ExprParserTests
{
    private static readonly IReadOnlyDictionary<string, ColumnModel> Schema =
        OrdersSchema.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

    private static ExprNode Parse(string expr)
    {
        var (ast, error) = ExprParser.Parse(expr, Schema);
        Assert.Null(error);
        return ast!;
    }

    private static string Error(string expr)
    {
        var (ast, error) = ExprParser.Parse(expr, Schema);
        Assert.Null(ast);
        return error!;
    }

    [Fact]
    public void Multiplication_binds_tighter_than_addition()
    {
        var ast = Assert.IsType<BinaryOp>(Parse("1 + 2 * 3"));

        Assert.Equal("+", ast.Op);
        var right = Assert.IsType<BinaryOp>(ast.Right);
        Assert.Equal("*", right.Op);
    }

    [Fact]
    public void Parentheses_override_precedence()
    {
        var ast = Assert.IsType<BinaryOp>(Parse("(1 + 2) * 3"));

        Assert.Equal("*", ast.Op);
        Assert.Equal("+", Assert.IsType<BinaryOp>(ast.Left).Op);
    }

    [Fact]
    public void Doubled_quote_escapes_inside_strings()
    {
        var lit = Assert.IsType<StringLit>(Parse("'it''s'"));
        Assert.Equal("it's", lit.Value);
    }

    [Fact]
    public void Column_resolution_is_case_insensitive_and_typed()
    {
        var col = Assert.IsType<ColumnRef>(Parse("amount"));
        Assert.Equal("AMOUNT", col.Column.Name);
        Assert.Equal(ColumnKind.Number, col.Kind);
    }

    [Fact]
    public void Concat_operator_yields_text()
    {
        var ast = Parse("UPPER(CUSTOMER) || '!' || AMOUNT");
        Assert.Equal(ColumnKind.Text, ast.Kind);
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("'abc", "unterminated string")]
    [InlineData("NO_SUCH_COL", "unknown column 'NO_SUCH_COL'")]
    [InlineData("FOO(1)", "unknown column 'FOO'")]
    [InlineData("AMOUNT +", "unexpected end")]
    [InlineData("1 | 2", "single '|'")]
    [InlineData("ROUND(1, 2, 3)", "ROUND takes 1–2 arguments")]
    [InlineData("UPPER(AMOUNT)", "argument 1 must be text")]
    [InlineData("AMOUNT + CUSTOMER", "'+' requires number operands")]
    [InlineData("COALESCE(NOTES, 5)", "same type")]
    [InlineData("-CUSTOMER", "unary '-' requires a number")]
    [InlineData("AMOUNT ? 2", "unexpected character '?'")]
    public void Errors_are_precise(string expr, string expectedFragment)
    {
        Assert.Contains(expectedFragment, Error(expr));
    }

    [Fact]
    public void Year_accepts_dates_and_text_but_not_numbers()
    {
        Assert.Equal(ColumnKind.Number, Parse("YEAR(ORDER_DATE)").Kind);
        Assert.Equal(ColumnKind.Number, Parse("YEAR(NOTES)").Kind);   // SQLite date-as-text
        Assert.Contains("must be a date", Error("YEAR(AMOUNT)"));
    }

    [Fact]
    public void Coalesce_takes_the_common_kind()
    {
        Assert.Equal(ColumnKind.Text, Parse("COALESCE(NOTES, 'n/a')").Kind);
        Assert.Equal(ColumnKind.Number, Parse("COALESCE(AMOUNT, 0)").Kind);
    }

    [Fact]
    public void Substr_arity_two_and_three_both_parse()
    {
        Assert.Equal(ColumnKind.Text, Parse("SUBSTR(CUSTOMER, 2)").Kind);
        Assert.Equal(ColumnKind.Text, Parse("SUBSTR(CUSTOMER, 2, 3)").Kind);
    }

    [Fact]
    public void Function_names_are_case_insensitive()
    {
        Assert.Equal(ColumnKind.Number, Parse("round(abs(AMOUNT), 2)").Kind);
    }
}
