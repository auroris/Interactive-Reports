using InteractiveReport.Core.Composition;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Validation;
using static InteractiveReport.Core.Tests.TestFixtures;

namespace InteractiveReport.Core.Tests;

/// <summary>The row-condition expression vocabulary compiled across every dialect.</summary>
public class OperatorMatrixTests
{
    [Theory]
    [InlineData(ReportDialect.Sqlite, "(\"AMOUNT\" >= @p0)")]
    [InlineData(ReportDialect.SqlServer, "([AMOUNT] >= @p0)")]
    [InlineData(ReportDialect.Oracle, "(\"AMOUNT\" >= :p0)")]
    [InlineData(ReportDialect.Postgres, "(\"AMOUNT\" >= @p0)")]
    public void Comparisons_are_bound_predicates(ReportDialect dialect, string expected)
    {
        var compiled = Compile(dialect, "AMOUNT >= 1000");

        Assert.Contains(expected, compiled.Sql);
        Assert.Contains(1000m, compiled.NamedBindings.Values);
    }

    [Theory]
    [InlineData(ReportDialect.Sqlite, "\"STATUS\" IN (@p0, @p1)")]
    [InlineData(ReportDialect.SqlServer, "[STATUS] IN (@p0, @p1)")]
    [InlineData(ReportDialect.Oracle, "\"STATUS\" IN (:p0, :p1)")]
    [InlineData(ReportDialect.Postgres, "\"STATUS\" IN (@p0, @p1)")]
    public void In_list_is_a_typed_condition_function(ReportDialect dialect, string expected)
    {
        var compiled = Compile(dialect, "IN_LIST(STATUS, 'NEW', 'SHIPPED')");

        Assert.Contains(expected, compiled.Sql);
        Assert.Contains("NEW", compiled.NamedBindings.Values);
        Assert.Contains("SHIPPED", compiled.NamedBindings.Values);
    }

    [Theory]
    [InlineData(ReportDialect.Sqlite)]
    [InlineData(ReportDialect.SqlServer)]
    [InlineData(ReportDialect.Oracle)]
    [InlineData(ReportDialect.Postgres)]
    public void Text_predicates_are_case_insensitive_and_bind_wildcards(ReportDialect dialect)
    {
        var compiled = Compile(dialect, "CONTAINS(CUSTOMER, 'Acme')");

        Assert.Contains("LOWER", compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LIKE", compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["%", "Acme", "%"], compiled.NamedBindings.Values.Take(3));
    }

    [Theory]
    [InlineData(ReportDialect.Sqlite)]
    [InlineData(ReportDialect.SqlServer)]
    [InlineData(ReportDialect.Oracle)]
    [InlineData(ReportDialect.Postgres)]
    public void Logical_and_null_conditions_compose(ReportDialect dialect)
    {
        var compiled = Compile(dialect, "NOTES IS NULL OR (AMOUNT BETWEEN 100 AND 500 AND STATUS <> 'CANCELLED')");

        Assert.Contains("IS NULL", compiled.Sql);
        Assert.Contains("BETWEEN", compiled.Sql);
        Assert.Contains(" AND ", compiled.Sql);
        Assert.Contains(" OR ", compiled.Sql);
    }

    private static SqlKata.SqlResult Compile(ReportDialect dialect, string expression)
    {
        var definition = OrdersDefinition(dialect);
        var state = StateValidator.Validate(
            definition,
            Doc(source: new StageLayer { Filters = [new FilterRule { Expr = expression }] }),
            OrdersSchema);
        return DialectSupport.GetCompiler(dialect).Compile(QueryComposer.Compose(definition, state).Page);
    }
}
