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
