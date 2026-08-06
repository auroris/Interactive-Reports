using InteractiveReport.Core.Expressions;
using InteractiveReport.Core.Model;
using static InteractiveReport.Core.Tests.TestFixtures;

namespace InteractiveReport.Core.Tests;

/// <summary>
/// Fragment goldens: AST → per-dialect SQL with [bracket] identifiers and '?' bindings.
/// SqlKata translates the brackets at compile time; these lock what we hand it.
/// </summary>
public class ExprEmitterTests
{
    private static readonly IReadOnlyDictionary<string, ColumnModel> Schema =
        OrdersSchema.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

    private static (string Sql, IReadOnlyList<object> Bindings) Emit(string expr, ReportDialect dialect)
    {
        var (ast, error) = ExprParser.Parse(expr, Schema);
        Assert.Null(error);
        return ExprEmitter.Emit(ast!, dialect);
    }

    [Fact]
    public void Literals_become_positional_bindings_never_inline_text()
    {
        var (sql, bindings) = Emit("ROUND(AMOUNT * 1.0825, 2)", ReportDialect.Sqlite);

        Assert.Equal("ROUND(([AMOUNT] * ?), ?)", sql);
        Assert.Equal([1.0825m, 2m], bindings);
    }

    [Fact]
    public void Concat_is_variadic_concat_except_oracle_native_pipes()
    {
        Assert.Equal("CONCAT(UPPER([CUSTOMER]), ?)", Emit("UPPER(CUSTOMER) || '!'", ReportDialect.SqlServer).Sql);
        Assert.Equal("CONCAT(UPPER([CUSTOMER]), ?)", Emit("UPPER(CUSTOMER) || '!'", ReportDialect.Sqlite).Sql);
        Assert.Equal("(UPPER([CUSTOMER]) || ?)", Emit("UPPER(CUSTOMER) || '!'", ReportDialect.Oracle).Sql);
    }

    [Fact]
    public void Two_arg_substr_becomes_to_end_substring_on_sqlserver()
    {
        Assert.Equal("SUBSTRING([CUSTOMER], ?, LEN([CUSTOMER]))", Emit("SUBSTR(CUSTOMER, 2)", ReportDialect.SqlServer).Sql);
        Assert.Equal("SUBSTR([CUSTOMER], ?)", Emit("SUBSTR(CUSTOMER, 2)", ReportDialect.Oracle).Sql);
        Assert.Equal("SUBSTR([CUSTOMER], ?)", Emit("SUBSTR(CUSTOMER, 2)", ReportDialect.Sqlite).Sql);
    }

    [Fact]
    public void Length_maps_to_len_only_on_sqlserver()
    {
        Assert.Equal("LEN([CUSTOMER])", Emit("LENGTH(CUSTOMER)", ReportDialect.SqlServer).Sql);
        Assert.Equal("LENGTH([CUSTOMER])", Emit("LENGTH(CUSTOMER)", ReportDialect.Oracle).Sql);
    }

    [Fact]
    public void Date_parts_use_native_idioms_per_dialect()
    {
        Assert.Equal("YEAR([ORDER_DATE])", Emit("YEAR(ORDER_DATE)", ReportDialect.SqlServer).Sql);
        Assert.Equal("EXTRACT(YEAR FROM [ORDER_DATE])", Emit("YEAR(ORDER_DATE)", ReportDialect.Oracle).Sql);
        Assert.Equal("CAST(strftime('%Y', [ORDER_DATE]) AS INTEGER)", Emit("YEAR(ORDER_DATE)", ReportDialect.Sqlite).Sql);
        Assert.Equal("CAST(strftime('%m', [ORDER_DATE]) AS INTEGER)", Emit("MONTH(ORDER_DATE)", ReportDialect.Sqlite).Sql);
    }

    [Fact]
    public void Every_binary_is_parenthesized_and_unary_minus_wraps()
    {
        Assert.Equal("([AMOUNT] + (? * ?))", Emit("AMOUNT + 2 * 3", ReportDialect.Sqlite).Sql);
        Assert.Equal("((-[AMOUNT]) / ?)", Emit("-AMOUNT / 2", ReportDialect.Sqlite).Sql);
    }

    [Fact]
    public void Coalesce_passes_through_on_all_dialects()
    {
        foreach (var d in new[] { ReportDialect.SqlServer, ReportDialect.Oracle, ReportDialect.Sqlite })
            Assert.Equal("COALESCE([NOTES], ?)", Emit("COALESCE(NOTES, 'n/a')", d).Sql);
    }

    // --- CASE, conditions, NULL: the portable core emits identically everywhere ---

    private static readonly ReportDialect[] AllDialects =
        [ReportDialect.SqlServer, ReportDialect.Oracle, ReportDialect.Sqlite];

    [Fact]
    public void Searched_case_emits_identically_on_all_dialects()
    {
        foreach (var d in AllDialects)
        {
            var (sql, bindings) = Emit("CASE WHEN AMOUNT > 1000 THEN 'big' ELSE 'small' END", d);

            Assert.Equal("CASE WHEN ([AMOUNT] > ?) THEN ? ELSE ? END", sql);
            Assert.Equal([1000m, "big", "small"], bindings);
        }
    }

    [Fact]
    public void Simple_case_emits_the_operand_form()
    {
        foreach (var d in AllDialects)
            Assert.Equal("CASE [STATUS] WHEN ? THEN ? ELSE ? END",
                Emit("CASE STATUS WHEN 'S' THEN 1 ELSE 0 END", d).Sql);
    }

    [Fact]
    public void Conditions_parenthesize_and_null_literal_is_the_keyword_not_a_binding()
    {
        var (sql, bindings) = Emit(
            "CASE WHEN NOT (NOTES IS NULL) AND AMOUNT >= 10 THEN NULL ELSE AMOUNT END", ReportDialect.Sqlite);

        Assert.Equal("CASE WHEN ((NOT ([NOTES] IS NULL)) AND ([AMOUNT] >= ?)) THEN NULL ELSE [AMOUNT] END", sql);
        Assert.Equal([10m], bindings);
    }

    [Fact]
    public void Bang_not_equal_emits_angle_brackets()
    {
        Assert.Equal("CASE WHEN ([STATUS] <> ?) THEN ? ELSE ? END",
            Emit("CASE WHEN STATUS != 'X' THEN 1 ELSE 0 END", ReportDialect.Sqlite).Sql);
    }

    [Fact]
    public void Case_without_else_omits_the_clause()
    {
        Assert.Equal("CASE WHEN ([NOTES] IS NOT NULL) THEN UPPER([NOTES]) END",
            Emit("CASE WHEN NOTES IS NOT NULL THEN UPPER(NOTES) END", ReportDialect.Oracle).Sql);
    }

    [Fact]
    public void Dialect_functions_keep_their_idioms_inside_case_conditions()
    {
        Assert.Equal("CASE WHEN (LEN([CUSTOMER]) > ?) THEN ? ELSE ? END",
            Emit("CASE WHEN LENGTH(CUSTOMER) > 5 THEN 1 ELSE 0 END", ReportDialect.SqlServer).Sql);
        Assert.Equal("CASE WHEN (LENGTH([CUSTOMER]) > ?) THEN ? ELSE ? END",
            Emit("CASE WHEN LENGTH(CUSTOMER) > 5 THEN 1 ELSE 0 END", ReportDialect.Oracle).Sql);
    }

    [Fact]
    public void Boolean_valued_columns_lower_to_explicit_predicates_in_condition_position()
    {
        // T-SQL has no boolean expressions: "WHEN [FLAG]" is invalid there, so a
        // bool-valued operand in condition position becomes an explicit "= 1" test.
        var schema = OrdersSchema.Append(Col("IS_PRIORITY", typeof(bool)))
            .ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

        (string Sql, IReadOnlyList<object> Bindings) EmitBool(string expr, ReportDialect dialect)
        {
            var (ast, error) = ExprParser.Parse(expr, schema);
            Assert.Null(error);
            return ExprEmitter.Emit(ast!, dialect);
        }

        foreach (var d in AllDialects)
        {
            Assert.Equal("CASE WHEN ([IS_PRIORITY] = 1) THEN ? ELSE ? END",
                EmitBool("CASE WHEN IS_PRIORITY THEN 1 ELSE 0 END", d).Sql);
            Assert.Equal("CASE WHEN ((NOT ([IS_PRIORITY] = 1)) AND ([AMOUNT] > ?)) THEN ? ELSE ? END",
                EmitBool("CASE WHEN NOT IS_PRIORITY AND AMOUNT > 5 THEN 1 ELSE 0 END", d).Sql);
        }
    }
}
