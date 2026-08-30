using InteractiveReport.Core.Composition;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Expressions;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Schema;
using InteractiveReport.Core.Validation;
using static InteractiveReport.Core.Tests.TestFixtures;

namespace InteractiveReport.Core.Tests;

/// <summary>
/// The trust boundary, adversarially: hostile text in any client-controlled slot must
/// end up as a binding, an ignored[] entry, or a clean validation error — never in SQL.
/// </summary>
public class SqlSafetyTests
{
    private const string Hostile = "'; DROP TABLE ORDERS;--";

    private static (string Sql, ICollection<object?> Bindings) Compile(ReportState state)
    {
        var result = CompileDocument(state);
        return (result.Sql, result.Bindings);
    }

    [Fact]
    public void Hostile_filter_value_becomes_a_binding_never_sql()
    {
        var (sql, bindings) = Compile(Doc(source: new StageLayer
        {
            Filters = [Filter($"CUSTOMER = {TextLiteral(Hostile)}")],
        }));

        Assert.DoesNotContain("DROP", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Hostile, bindings.Cast<object>());
    }

    [Fact]
    public void Hostile_search_value_becomes_a_lowered_like_binding()
    {
        var (sql, bindings) = Compile(Doc(search: "%' OR '1'='1"));

        Assert.DoesNotContain("OR '1'='1", sql);
        Assert.Contains(bindings, b => b is string s && s.Contains("%' or '1'='1"));
    }

    [Fact]
    public void Hostile_in_list_values_all_become_bindings()
    {
        var (sql, bindings) = Compile(Doc(source: new StageLayer
        {
            Filters = [Filter($"IN_LIST(STATUS, {TextLiteral(Hostile)}, 'SHIPPED')")],
        }));

        Assert.DoesNotContain("DROP", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, bindings.Count(b => b is string s && (s == Hostile || s == "SHIPPED")));
    }

    [Fact]
    public void Hostile_column_names_never_reach_sql()
    {
        // Expression identifiers are parsed, while structural column/sort names
        // are matched against the discovered schema.
        Assert.Throws<ReportValidationException>(() => CompileDocument(Doc(source: new StageLayer
        {
            Filters = [new FilterRule { Expr = "AMOUNT\" OR 1=1 -- = 1" }],
        })));

        var compiled = CompileDocument(Doc(source: new StageLayer
        {
            Columns = ["ORDER_ID", "CUSTOMER]; DROP TABLE ORDERS;--"],
            Sorts = [new SortRule { Col = "1; DELETE FROM ORDERS" }],
        }));

        Assert.DoesNotContain("DROP", compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE", compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, compiled.Ignored.Count);
    }

    [Theory]
    [InlineData("1); DROP TABLE ORDERS; --")]
    [InlineData("UPPER(CUSTOMER) || (SELECT PASSWORD FROM USERS)")]
    [InlineData("AMOUNT; DELETE FROM ORDERERS")]
    [InlineData("0x1f UNION SELECT * FROM SECRETS")]
    [InlineData("CASE WHEN 1=1 THEN (SELECT PASSWORD FROM USERS) ELSE 0 END")]
    [InlineData("CASE WHEN AMOUNT > 0 THEN 1 END)); DROP TABLE ORDERS; --")]
    [InlineData("1 = 1; DROP TABLE ORDERS")]
    [InlineData("CASE WHEN EXISTS(SELECT 1 FROM USERS) THEN 1 ELSE 0 END")]
    public void Hostile_expressions_die_in_the_parser(string expr)
    {
        var schema = OrdersSchema.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var (ast, error) = ExprParser.Parse(expr, schema);

        Assert.Null(ast);
        Assert.NotNull(error);
    }

    [Fact]
    public void Deeply_nested_expression_is_a_clean_error_not_a_stack_overflow()
    {
        var schema = OrdersSchema.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var expr = new string('(', 200) + "1" + new string(')', 200);

        var (ast, error) = ExprParser.Parse(expr, schema);

        Assert.Null(ast);
        Assert.Contains("nesting exceeds", error);
    }

    private static string TextLiteral(string value)
        => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private static CanonicalCompileResult CompileDocument(ReportState document)
    {
        var definition = OrdersDefinition(ReportDialect.Sqlite);
        var schema = ReportSchema.Create(definition.Name, OrdersSchema);
        var compiler = new ComposableTableCompiler(
            definition,
            document,
            schema,
            new DateTime(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc),
            (_, _, _, _, _) => Task.FromResult(new List<PivotGroup>()));
        var target = document.Tables is { Count: > 0 }
            ? document.ActiveTable!
            : "definition";
        var plan = compiler.CompleteForTarget(
            compiler.Compile(target, default).GetAwaiter().GetResult());
        var query = DialectSupport.GetCompiler(ReportDialect.Sqlite)
            .Compile(plan.ExecutionBundle.MainRows.Query);
        return new CanonicalCompileResult(
            query.Sql,
            query.NamedBindings.Values,
            plan.Ignored);
    }

    private sealed record CanonicalCompileResult(
        string Sql,
        ICollection<object?> Bindings,
        IReadOnlyList<IgnoredItem> Ignored);
}
