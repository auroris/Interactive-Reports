using InteractiveReport.Core.Composition;
using InteractiveReport.Core.Expressions;
using InteractiveReport.Core.Model;
using SqlKata;

namespace InteractiveReport.Core.Tests;

public sealed class Oracle11gCompilerTests
{
    [Fact]
    public void Oracle11g_compiler_uses_legacy_rownum_pagination()
    {
        var compiler11g = DialectSupport.GetCompiler(ReportDialect.Oracle11g);
        var compilerModern = DialectSupport.GetCompiler(ReportDialect.Oracle);

        var query = new Query("ORDERS")
            .Select("ID", "CUSTOMER")
            .OrderBy("ID")
            .Offset(20)
            .Limit(10);

        var result11g = compiler11g.Compile(query);
        var resultModern = compilerModern.Compile(query);

        Assert.Contains("ROWNUM", result11g.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OFFSET", result11g.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FETCH NEXT", result11g.Sql, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("OFFSET", resultModern.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FETCH NEXT", resultModern.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ROWNUM", resultModern.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Oracle11g_first_page_uses_the_plain_rownum_window()
    {
        var result = DialectSupport.GetCompiler(ReportDialect.Oracle11g).Compile(
            new Query("ORDERS").Select("ID").OrderBy("ID").Limit(10));

        Assert.Equal(
            "SELECT * FROM (SELECT \"ID\" FROM \"ORDERS\" ORDER BY \"ID\") WHERE ROWNUM <= :p0",
            result.Sql);
        Assert.Equal([10L], result.NamedBindings.Values.Select(Convert.ToInt64));
    }

    [Fact]
    public void Oracle11g_later_pages_re_project_the_query_columns_around_the_rownum_window()
    {
        // SqlKata's own legacy window returns ROWNUM as an extra "row_num" column through
        // SELECT *, so a page carried one more column than its projection. The outer SELECT
        // must name exactly the query's output columns: plain, qualified, aliased, and raw
        // items with bracket-marker or quoted aliases alike.
        var query = new Query("ORDERS")
            .Select("ID", "T.CUSTOMER", "AMOUNT as TOTAL")
            .SelectRaw("CASE WHEN [AMOUNT] > ? THEN 1 ELSE 0 END AS [__ir_highlight_0]", 5000)
            .SelectRaw("UPPER([NOTES]) AS \"we\"\"ird\"")
            .OrderBy("ID")
            .Offset(20)
            .Limit(10);

        var result = DialectSupport.GetCompiler(ReportDialect.Oracle11g).Compile(query);

        Assert.StartsWith(
            "SELECT \"ID\", \"CUSTOMER\", \"TOTAL\", \"__ir_highlight_0\", \"we\"\"ird\" FROM ("
            + "SELECT \"results_wrapper\".*, ROWNUM \"row_num\" FROM (SELECT \"ID\", \"T\".\"CUSTOMER\", ",
            result.Sql);
        Assert.Contains("CASE WHEN \"AMOUNT\" > :p0 THEN 1 ELSE 0 END AS \"__ir_highlight_0\", UPPER(\"NOTES\") AS \"we\"\"ird\" FROM \"ORDERS\" ORDER BY \"ID\"", result.Sql);
        Assert.EndsWith(") \"results_wrapper\" WHERE ROWNUM <= :p1) WHERE \"row_num\" > :p2", result.Sql);
        Assert.Equal([5000L, 30L, 20L], result.NamedBindings.Values.Select(Convert.ToInt64));
    }

    [Fact]
    public void Oracle11g_windows_nest_without_widening_either_level()
    {
        var inner = new Query("ORDERS").Select("ID", "CUSTOMER").OrderBy("ID").Limit(3);
        var query = new Query().From(inner.As("ir_rel_0")).Select("ID").OrderBy("ID").Offset(1).Limit(1);

        var result = DialectSupport.GetCompiler(ReportDialect.Oracle11g).Compile(query);

        Assert.StartsWith(
            "SELECT \"ID\" FROM (SELECT \"results_wrapper\".*, ROWNUM \"row_num\" FROM (SELECT \"ID\" FROM (",
            result.Sql);
        Assert.Contains(
            "(SELECT * FROM (SELECT \"ID\", \"CUSTOMER\" FROM \"ORDERS\" ORDER BY \"ID\") WHERE ROWNUM <= :p0)",
            result.Sql);
        Assert.EndsWith(") \"results_wrapper\" WHERE ROWNUM <= :p1) WHERE \"row_num\" > :p2", result.Sql);
        Assert.Equal([3L, 2L, 1L], result.NamedBindings.Values.Select(Convert.ToInt64));
    }

    [Fact]
    public void Oracle11g_offset_without_a_nameable_projection_fails_instead_of_widening()
    {
        var compiler = DialectSupport.GetCompiler(ReportDialect.Oracle11g);

        var star = Assert.Throws<InvalidOperationException>(() => compiler.Compile(
            new Query("ORDERS").OrderBy("ID").Offset(5).Limit(5)));
        Assert.Contains("explicit projection", star.Message);

        var unaliased = Assert.Throws<InvalidOperationException>(() => compiler.Compile(
            new Query("ORDERS").SelectRaw("COUNT(*)").Offset(5).Limit(5)));
        Assert.Contains("AS alias", unaliased.Message);
    }

    [Fact]
    public void Oracle11g_aggregate_expressions_match_oracle_behavior()
    {
        Assert.Equal("COUNT(\"AMOUNT\")", DialectSupport.AggregateExpression(ReportDialect.Oracle11g, AggregateFn.Count, "\"AMOUNT\""));
        Assert.Equal("COUNT(DISTINCT \"AMOUNT\")", DialectSupport.AggregateExpression(ReportDialect.Oracle11g, AggregateFn.CountDistinct, "\"AMOUNT\""));
        Assert.Equal("SUM(\"AMOUNT\")", DialectSupport.AggregateExpression(ReportDialect.Oracle11g, AggregateFn.Sum, "\"AMOUNT\""));
        Assert.Equal("MIN(\"AMOUNT\")", DialectSupport.AggregateExpression(ReportDialect.Oracle11g, AggregateFn.Min, "\"AMOUNT\""));
        Assert.Equal("MAX(\"AMOUNT\")", DialectSupport.AggregateExpression(ReportDialect.Oracle11g, AggregateFn.Max, "\"AMOUNT\""));
        Assert.Equal("AVG(\"AMOUNT\")", DialectSupport.AggregateExpression(ReportDialect.Oracle11g, AggregateFn.Avg, "\"AMOUNT\""));
    }

    [Fact]
    public void Oracle11g_date_format_translation_uses_oracle_vocabulary()
    {
        var parts = ExprDateRules.ParseDateFormat("YYYY-MM-DD HH24:MI:SS");
        var translated = ExprDateRules.TranslateFormat(ReportDialect.Oracle11g, parts);
        Assert.Equal("YYYY-MM-DD HH24:MI:SS", translated);
    }
}
