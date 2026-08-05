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
}
