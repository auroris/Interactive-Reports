using InteractiveReport.Core.Composition;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Validation;
using static InteractiveReport.Core.Tests.TestFixtures;

namespace InteractiveReport.Core.Tests;

/// <summary>
/// The full operator × dialect matrix: every FilterOp's WHERE fragment locked for
/// SqlServer, Oracle, and Sqlite. When the live-dialect battery runs against real
/// engines, these are the shapes it is executing.
/// </summary>
public class OperatorMatrixTests
{
    private static string PageSql(ReportDialect dialect, FilterRule filter)
    {
        var def = OrdersDefinition(dialect);
        var validated = StateValidator.Validate(def, new ReportState { Filters = [filter] }, OrdersSchema);
        var composed = QueryComposer.Compose(def, validated);
        return DialectSupport.GetCompiler(dialect).Compile(composed.Page).Sql;
    }

    // Comparison operators on a number column.
    [Theory]
    [InlineData(ReportDialect.Sqlite, FilterOp.Eq, "\"AMOUNT\" = @p0")]
    [InlineData(ReportDialect.Sqlite, FilterOp.Ne, "\"AMOUNT\" <> @p0")]
    [InlineData(ReportDialect.Sqlite, FilterOp.Lt, "\"AMOUNT\" < @p0")]
    [InlineData(ReportDialect.Sqlite, FilterOp.Le, "\"AMOUNT\" <= @p0")]
    [InlineData(ReportDialect.Sqlite, FilterOp.Gt, "\"AMOUNT\" > @p0")]
    [InlineData(ReportDialect.Sqlite, FilterOp.Ge, "\"AMOUNT\" >= @p0")]
    [InlineData(ReportDialect.SqlServer, FilterOp.Eq, "[AMOUNT] = @p0")]
    [InlineData(ReportDialect.SqlServer, FilterOp.Ne, "[AMOUNT] <> @p0")]
    [InlineData(ReportDialect.SqlServer, FilterOp.Lt, "[AMOUNT] < @p0")]
    [InlineData(ReportDialect.SqlServer, FilterOp.Le, "[AMOUNT] <= @p0")]
    [InlineData(ReportDialect.SqlServer, FilterOp.Gt, "[AMOUNT] > @p0")]
    [InlineData(ReportDialect.SqlServer, FilterOp.Ge, "[AMOUNT] >= @p0")]
    [InlineData(ReportDialect.Oracle, FilterOp.Eq, "\"AMOUNT\" = :p0")]
    [InlineData(ReportDialect.Oracle, FilterOp.Ne, "\"AMOUNT\" <> :p0")]
    [InlineData(ReportDialect.Oracle, FilterOp.Lt, "\"AMOUNT\" < :p0")]
    [InlineData(ReportDialect.Oracle, FilterOp.Le, "\"AMOUNT\" <= :p0")]
    [InlineData(ReportDialect.Oracle, FilterOp.Gt, "\"AMOUNT\" > :p0")]
    [InlineData(ReportDialect.Oracle, FilterOp.Ge, "\"AMOUNT\" >= :p0")]
    public void Comparisons(ReportDialect dialect, FilterOp op, string expected)
    {
        Assert.Contains(expected, PageSql(dialect, Filter("AMOUNT", op, 1000)));
    }

    [Theory]
    [InlineData(ReportDialect.Sqlite, "\"AMOUNT\" BETWEEN @p0 AND @p1")]
    [InlineData(ReportDialect.SqlServer, "[AMOUNT] BETWEEN @p0 AND @p1")]
    [InlineData(ReportDialect.Oracle, "\"AMOUNT\" BETWEEN :p0 AND :p1")]
    public void Between(ReportDialect dialect, string expected)
    {
        Assert.Contains(expected, PageSql(dialect, Filter("AMOUNT", FilterOp.Between, new[] { 1, 2 })));
    }

    [Theory]
    [InlineData(ReportDialect.Sqlite, FilterOp.In, "\"STATUS\" IN (@p0, @p1)")]
    [InlineData(ReportDialect.Sqlite, FilterOp.Nin, "\"STATUS\" NOT IN (@p0, @p1)")]
    [InlineData(ReportDialect.SqlServer, FilterOp.In, "[STATUS] IN (@p0, @p1)")]
    [InlineData(ReportDialect.SqlServer, FilterOp.Nin, "[STATUS] NOT IN (@p0, @p1)")]
    [InlineData(ReportDialect.Oracle, FilterOp.In, "\"STATUS\" IN (:p0, :p1)")]
    [InlineData(ReportDialect.Oracle, FilterOp.Nin, "\"STATUS\" NOT IN (:p0, :p1)")]
    public void In_lists(ReportDialect dialect, FilterOp op, string expected)
    {
        Assert.Contains(expected, PageSql(dialect, Filter("STATUS", op, new[] { "NEW", "SHIPPED" })));
    }

    // Text matching is case-insensitive by operator definition: LOWER(col) + lowered binding.
    [Theory]
    [InlineData(ReportDialect.Sqlite, FilterOp.Contains, "LOWER(\"CUSTOMER\") like @p0")]
    [InlineData(ReportDialect.Sqlite, FilterOp.Starts, "LOWER(\"CUSTOMER\") like @p0")]
    [InlineData(ReportDialect.Sqlite, FilterOp.Ends, "LOWER(\"CUSTOMER\") like @p0")]
    [InlineData(ReportDialect.SqlServer, FilterOp.Contains, "LOWER([CUSTOMER]) like @p0")]
    [InlineData(ReportDialect.SqlServer, FilterOp.Starts, "LOWER([CUSTOMER]) like @p0")]
    [InlineData(ReportDialect.SqlServer, FilterOp.Ends, "LOWER([CUSTOMER]) like @p0")]
    [InlineData(ReportDialect.Oracle, FilterOp.Contains, "LOWER(\"CUSTOMER\") like :p0")]
    [InlineData(ReportDialect.Oracle, FilterOp.Starts, "LOWER(\"CUSTOMER\") like :p0")]
    [InlineData(ReportDialect.Oracle, FilterOp.Ends, "LOWER(\"CUSTOMER\") like :p0")]
    public void Text_matching(ReportDialect dialect, FilterOp op, string expected)
    {
        Assert.Contains(expected, PageSql(dialect, Filter("CUSTOMER", op, "ACME")));
    }

    [Fact]
    public void Text_match_bindings_carry_the_wildcard_pattern()
    {
        var def = OrdersDefinition(ReportDialect.Sqlite);
        string BindingFor(FilterOp op)
        {
            var validated = StateValidator.Validate(def, new ReportState { Filters = [Filter("CUSTOMER", op, "ACME")] }, OrdersSchema);
            var compiled = DialectSupport.GetCompiler(ReportDialect.Sqlite).Compile(QueryComposer.Compose(def, validated).Page);
            return (string)compiled.NamedBindings.Values.First()!;
        }

        Assert.Equal("%acme%", BindingFor(FilterOp.Contains));
        Assert.Equal("acme%", BindingFor(FilterOp.Starts));
        Assert.Equal("%acme", BindingFor(FilterOp.Ends));
    }

    [Theory]
    [InlineData(ReportDialect.Sqlite, FilterOp.Ncontains)]
    [InlineData(ReportDialect.SqlServer, FilterOp.Ncontains)]
    [InlineData(ReportDialect.Oracle, FilterOp.Ncontains)]
    public void Negated_text_matching_negates_the_like(ReportDialect dialect, FilterOp op)
    {
        var sql = PageSql(dialect, Filter("CUSTOMER", op, "ACME"));
        Assert.Contains("NOT", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("like", sql, StringComparison.OrdinalIgnoreCase);
    }

    // Blank semantics: Oracle's '' IS NULL collapses text-blank to a pure NULL test.
    [Theory]
    [InlineData(ReportDialect.Sqlite, FilterOp.Blank, "(\"NOTES\" IS NULL OR \"NOTES\" = @p0)")]
    [InlineData(ReportDialect.Sqlite, FilterOp.Nblank, "(\"NOTES\" IS NOT NULL AND \"NOTES\" <> @p0)")]
    [InlineData(ReportDialect.SqlServer, FilterOp.Blank, "([NOTES] IS NULL OR [NOTES] = @p0)")]
    [InlineData(ReportDialect.SqlServer, FilterOp.Nblank, "([NOTES] IS NOT NULL AND [NOTES] <> @p0)")]
    [InlineData(ReportDialect.Oracle, FilterOp.Blank, "\"NOTES\" IS NULL")]
    [InlineData(ReportDialect.Oracle, FilterOp.Nblank, "\"NOTES\" IS NOT NULL")]
    public void Blank_semantics(ReportDialect dialect, FilterOp op, string expected)
    {
        var sql = PageSql(dialect, Filter("NOTES", op));
        Assert.Contains(expected, sql);
        if (dialect == ReportDialect.Oracle)
        {
            // No empty-string comparison binding on Oracle — '' IS NULL there.
            Assert.DoesNotContain("= :p", sql);
            Assert.DoesNotContain("<> :p", sql);
        }
    }

    // Blank on a NUMBER column is a pure NULL test everywhere — '' is a text concept.
    [Theory]
    [InlineData(ReportDialect.Sqlite)]
    [InlineData(ReportDialect.SqlServer)]
    [InlineData(ReportDialect.Oracle)]
    public void Blank_on_non_text_is_pure_null_test(ReportDialect dialect)
    {
        var sql = PageSql(dialect, Filter("AMOUNT", FilterOp.Blank));
        Assert.Contains("IS NULL", sql);
        Assert.DoesNotContain(" OR ", sql);
    }
}
