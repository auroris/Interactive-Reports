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
    [InlineData("FOO(1)", "unknown function 'FOO'")]
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

    // --- CASE, conditions, NULL ---------------------------------------------

    [Fact]
    public void Searched_case_infers_its_result_type_from_branches()
    {
        var ast = Assert.IsType<CaseWhen>(Parse("CASE WHEN AMOUNT > 1000 THEN 1 ELSE 0 END"));

        Assert.Null(ast.Operand);
        Assert.Equal(ColumnKind.Number, ast.Kind);
        var when = Assert.IsType<Comparison>(Assert.Single(ast.Branches).When);
        Assert.Equal(">", when.Op);
    }

    [Fact]
    public void Simple_case_compares_the_operand_and_yields_branch_type()
    {
        var ast = Assert.IsType<CaseWhen>(Parse("CASE STATUS WHEN 'SHIPPED' THEN 'done' ELSE 'open' END"));

        Assert.IsType<ColumnRef>(ast.Operand);
        Assert.Equal(ColumnKind.Text, ast.Kind);
    }

    [Fact]
    public void Case_without_else_still_types_from_then_branches()
    {
        Assert.Equal(ColumnKind.Number, Parse("CASE WHEN NOTES IS NULL THEN 0 END").Kind);
    }

    [Fact]
    public void Null_branches_join_any_type()
    {
        Assert.Equal(ColumnKind.Text, Parse("CASE WHEN AMOUNT > 5 THEN NULL ELSE 'x' END").Kind);
        Assert.Equal(ColumnKind.Text, Parse("COALESCE(NOTES, NULL, 'n/a')").Kind);
    }

    [Fact]
    public void Boolean_operators_follow_sql_precedence()
    {
        // a AND b OR c parses as (a AND b) OR c; NOT binds tighter than AND.
        var ast = Assert.IsType<CaseWhen>(
            Parse("CASE WHEN AMOUNT > 100 AND AMOUNT < 200 OR NOT STATUS = 'X' THEN 1 END"));

        var or = Assert.IsType<LogicalOp>(ast.Branches[0].When);
        Assert.Equal("OR", or.Op);
        Assert.Equal("AND", Assert.IsType<LogicalOp>(or.Left).Op);
        Assert.IsType<Comparison>(Assert.IsType<NotOp>(or.Right).Operand);
    }

    [Fact]
    public void Not_equal_spellings_normalize_to_angle_brackets()
    {
        var bang = Assert.IsType<CaseWhen>(Parse("CASE WHEN STATUS != 'X' THEN 1 END"));
        var angle = Assert.IsType<CaseWhen>(Parse("CASE WHEN STATUS <> 'X' THEN 1 END"));

        Assert.Equal("<>", Assert.IsType<Comparison>(bang.Branches[0].When).Op);
        Assert.Equal("<>", Assert.IsType<Comparison>(angle.Branches[0].When).Op);
    }

    [Fact]
    public void Is_not_null_parses_as_a_negated_null_test()
    {
        var ast = Assert.IsType<CaseWhen>(Parse("CASE WHEN NOTES IS NOT NULL THEN 1 ELSE 0 END"));

        var test = Assert.IsType<NullTest>(ast.Branches[0].When);
        Assert.True(test.Negated);
    }

    [Fact]
    public void Keywords_are_case_insensitive()
    {
        Assert.Equal(ColumnKind.Number,
            Parse("case when amount > 1 and notes is null then 1 else 0 end").Kind);
    }

    [Theory]
    [InlineData("AMOUNT > 1000", "wrap it in CASE WHEN")]
    [InlineData("NULL", "cannot be just NULL")]
    [InlineData("CASE WHEN AMOUNT THEN 1 END", "needs a condition")]
    [InlineData("CASE WHEN AMOUNT > 1 THEN 1 ELSE 'x' END", "same type")]
    [InlineData("CASE WHEN AMOUNT > 1 THEN NULL END", "every branch is NULL")]
    [InlineData("CASE WHEN AMOUNT = NULL THEN 1 END", "use IS NULL")]
    [InlineData("CASE WHEN AMOUNT > 1 > 2 THEN 1 END", "chained comparisons")]
    [InlineData("CASE WHEN AMOUNT > 'x' THEN 1 END", "compares values of the same type")]
    [InlineData("CASE STATUS WHEN 5 THEN 1 END", "must match the CASE operand's type")]
    [InlineData("CASE STATUS WHEN NULL THEN 1 END", "use a searched CASE")]
    [InlineData("CASE WHEN AMOUNT > 1 THEN 1", "expected END")]
    [InlineData("CASE WHEN AMOUNT > 1 THEN 1 ELSE 0", "expected END")]
    [InlineData("UPPER(AMOUNT > 1)", "cannot be a condition")]
    [InlineData("(AMOUNT > 1) + 2", "'+' requires number operands (got condition and number)")]
    [InlineData("CASE WHEN NOT AMOUNT THEN 1 END", "NOT requires a condition")]
    [InlineData("CASE WHEN AMOUNT IS 5 THEN 1 END", "expected NULL")]
    public void Condition_and_case_errors_are_precise(string expr, string expectedFragment)
    {
        Assert.Contains(expectedFragment, Error(expr));
    }

    [Theory]
    [InlineData("NULL + 1")]
    [InlineData("1 * NULL")]
    [InlineData("-NULL")]
    public void Null_infers_number_from_arithmetic_context(string expr)
    {
        Assert.Equal(ColumnKind.Number, Parse(expr).Kind);
    }

    [Fact]
    public void Unary_minus_counts_toward_the_nesting_limit()
    {
        var expr = new string('-', 65) + "1";

        Assert.Contains("nesting exceeds", Error(expr));
    }

    // --- dates and BETWEEN (ARCHITECTURE §8 date vocabulary) ------------------

    [Fact]
    public void Date_vocabulary_types_flow()
    {
        Assert.Equal(ColumnKind.Date, Parse("NOW()").Kind);
        Assert.Equal(ColumnKind.Date, Parse("TO_DATE(NOTES)").Kind);            // SQLite date-as-text
        Assert.Equal(ColumnKind.Date, Parse("TO_DATE(ORDER_DATE)").Kind);       // identity conversion
        Assert.Equal(ColumnKind.Date, Parse("DATE_TRUNC('month', NOW())").Kind); // unit is case-insensitive
        Assert.Equal(ColumnKind.Text, Parse("TO_STRING(NOW(), 'YYYY-MM-DD HH24:MI:SS')").Kind);
        Assert.Equal(ColumnKind.Date, Parse("NOW() - 30").Kind);
        Assert.Equal(ColumnKind.Date, Parse("DATE_TRUNC('MONTH', NOW()) - 1").Kind);
    }

    [Fact]
    public void Date_offsets_accept_provably_whole_expressions()
    {
        Assert.Equal(ColumnKind.Date, Parse("NOW() + ORDER_ID").Kind);          // integer-typed column
        Assert.Equal(ColumnKind.Date, Parse("NOW() - ROUND(AMOUNT)").Kind);     // single-arg ROUND is whole
        Assert.Equal(ColumnKind.Date, Parse("NOW() + YEAR(ORDER_DATE) * 2").Kind);
        Assert.Equal(ColumnKind.Date, Parse("NOW() + NULL").Kind);              // NULL offset → NULL date
    }

    [Fact]
    public void Between_keeps_its_own_and_and_yields_a_trailing_logical_and()
    {
        var ast = Assert.IsType<CaseWhen>(
            Parse("CASE WHEN ORDER_DATE BETWEEN NOW() - 30 AND NOW() AND AMOUNT > 0 THEN 1 ELSE 0 END"));

        var and = Assert.IsType<LogicalOp>(ast.Branches[0].When);
        var between = Assert.IsType<Between>(and.Left);
        Assert.IsType<DateAdd>(between.Lower);
        Assert.IsType<Comparison>(and.Right);
    }

    [Fact]
    public void Between_binds_inside_not_and_before_or()
    {
        var ast = Assert.IsType<CaseWhen>(
            Parse("CASE WHEN NOT AMOUNT BETWEEN 1 AND 2 OR STATUS = 'X' THEN 1 ELSE 0 END"));

        var or = Assert.IsType<LogicalOp>(ast.Branches[0].When);
        Assert.IsType<Between>(Assert.IsType<NotOp>(or.Left).Operand);
        Assert.IsType<Comparison>(or.Right);
    }

    [Fact]
    public void Between_works_for_numbers_and_text_too()
    {
        Assert.Equal(ColumnKind.Number,
            Parse("CASE WHEN AMOUNT BETWEEN 1000 AND 8000 THEN 1 ELSE 0 END").Kind);
        Assert.Equal(ColumnKind.Number,
            Parse("CASE WHEN STATUS BETWEEN 'A' AND 'M' THEN 1 ELSE 0 END").Kind);
    }

    [Theory]
    [InlineData("TO_DATE('2026-1-1')", "must be ISO YYYY-MM-DD")]
    [InlineData("TO_DATE('2025-02-29')", "must be ISO YYYY-MM-DD")]
    [InlineData("TO_DATE(AMOUNT)", "must be text or a date")]
    [InlineData("NOW(1)", "NOW takes 0 arguments")]
    [InlineData("DATE_TRUNC('WEEK', NOW())", "'DAY', 'MONTH', or 'YEAR'")]
    [InlineData("DATE_TRUNC(NOTES, NOW())", "'DAY', 'MONTH', or 'YEAR'")]
    [InlineData("DATE_TRUNC('DAY', NOTES)", "convert text with TO_DATE")]
    [InlineData("TO_STRING(NOTES)", "must be a date")]
    [InlineData("TO_STRING(NOW(), 'YYYY-QQ')", "TO_STRING format is invalid")]
    [InlineData("TO_STRING(NOW(), NOTES)", "format must be a string literal")]
    [InlineData("NOW() + 1.5", "whole calendar days (got 1.5)")]
    [InlineData("NOW() + AMOUNT", "cannot be established as whole")]
    [InlineData("NOW() + ORDER_ID / 2", "cannot be established as whole")]
    [InlineData("NOW() - NOW()", "date - date is not supported")]
    [InlineData("NOW() + NOW()", "two dates cannot be added")]
    [InlineData("1 + NOW()", "the date goes on the left")]
    [InlineData("NOW() * 2", "requires number operands")]
    [InlineData("NOW() + NOTES", "must be a number of whole days")]
    [InlineData("ORDER_DATE BETWEEN NOW() AND 1", "share one type")]
    [InlineData("ORDER_DATE BETWEEN NULL AND NOW()", "use IS NULL")]
    [InlineData("AMOUNT BETWEEN 1 AND 2", "wrap it in CASE WHEN")]
    [InlineData("CASE WHEN (AMOUNT > 1) BETWEEN 1 AND 2 THEN 1 END", "BETWEEN cannot compare conditions")]
    [InlineData("CASE WHEN ORDER_DATE BETWEEN NOW() THEN 1 END", "expected AND")]
    [InlineData("NOTES || ORDER_DATE", "convert the date with TO_STRING")]
    [InlineData("CONCAT(NOTES, ORDER_DATE)", "dates go through TO_STRING")]
    public void Date_and_between_errors_are_precise(string expr, string expectedFragment)
    {
        Assert.Contains(expectedFragment, Error(expr));
    }

    [Fact]
    public void Row_conditions_accept_the_full_typed_expression_corpus()
    {
        var expression =
            "ROUND(AMOUNT, 2) >= 1000 AND "
            + "DATE_TRUNC('YEAR', ORDER_DATE) = TO_DATE('2026-01-01') "
            + "AND (CONTAINS(CUSTOMER, 'ACME') OR IN_LIST(STATUS, 'NEW', 'PENDING'))";

        var (ast, error) = ExprParser.ParseCondition(expression, Schema);

        Assert.Null(error);
        Assert.Equal(ColumnKind.Bool, ast!.Kind);
    }

    [Fact]
    public void Row_conditions_reject_value_expressions()
    {
        var (ast, error) = ExprParser.ParseCondition("ROUND(AMOUNT, 2)", Schema);

        Assert.Null(ast);
        Assert.Contains("true/false", error);
    }
}
