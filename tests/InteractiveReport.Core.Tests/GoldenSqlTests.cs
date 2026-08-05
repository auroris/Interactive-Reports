using InteractiveReport.Core.Composition;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Validation;
using SqlKata;
using static InteractiveReport.Core.Tests.TestFixtures;

namespace InteractiveReport.Core.Tests;

/// <summary>
/// Golden tests: (state document → compiled SQL) locked per dialect so drift is loud.
/// The exact strings are SqlKata 4.x output — if a SqlKata upgrade changes them,
/// that's precisely the alarm these tests exist to raise.
/// </summary>
public class GoldenSqlTests
{
    private static (SqlResult Page, SqlResult Count) Compile(ReportDialect dialect, ReportState state)
    {
        var (page, count, _, _) = CompileAll(dialect, state);
        return (page, count);
    }

    private static (SqlResult Page, SqlResult Count, SqlResult? Aggregates, SqlResult? BreakTotals) CompileAll(
        ReportDialect dialect, ReportState state)
    {
        var def = OrdersDefinition(dialect);
        var validated = StateValidator.Validate(def, state, OrdersSchema);
        var composed = QueryComposer.Compose(def, validated);
        var compiler = DialectSupport.GetCompiler(dialect);
        return (
            compiler.Compile(composed.Page),
            compiler.Compile(composed.Count),
            composed.Aggregates is null ? null : compiler.Compile(composed.Aggregates),
            composed.BreakTotals is null ? null : compiler.Compile(composed.BreakTotals));
    }

    private static readonly ReportState CoreState = new()
    {
        Filters = [Filter("STATUS", FilterOp.Eq, "SHIPPED")],
        Sorts = [new SortRule { Col = "ORDER_DATE", Dir = SortDir.Desc }],
        Columns = ["ORDER_ID", "CUSTOMER", "AMOUNT"],
        Page = new PageRequest { Index = 2, Size = 25 },
    };

    [Fact]
    public void Sqlite_page_query()
    {
        var (page, _) = Compile(ReportDialect.Sqlite, CoreState);

        Assert.Equal(
            "SELECT \"ORDER_ID\", \"CUSTOMER\", \"AMOUNT\" FROM (SELECT ORDER_ID, CUSTOMER, REGION, STATUS, AMOUNT, ORDER_DATE, NOTES FROM ORDERS) ir_base WHERE \"STATUS\" = @p0 ORDER BY \"ORDER_DATE\" DESC LIMIT @p1 OFFSET @p2",
            page.Sql);
        Assert.Equal(["SHIPPED", 25, 25L], page.NamedBindings.Values.ToArray());
    }

    [Fact]
    public void SqlServer_page_query()
    {
        var (page, _) = Compile(ReportDialect.SqlServer, CoreState);

        Assert.Equal(
            "SELECT [ORDER_ID], [CUSTOMER], [AMOUNT] FROM (SELECT ORDER_ID, CUSTOMER, REGION, STATUS, AMOUNT, ORDER_DATE, NOTES FROM ORDERS) ir_base WHERE [STATUS] = @p0 ORDER BY [ORDER_DATE] DESC OFFSET @p1 ROWS FETCH NEXT @p2 ROWS ONLY",
            page.Sql);
    }

    [Fact]
    public void Oracle_page_query()
    {
        var (page, _) = Compile(ReportDialect.Oracle, CoreState);

        Assert.Equal(
            "SELECT \"ORDER_ID\", \"CUSTOMER\", \"AMOUNT\" FROM (SELECT ORDER_ID, CUSTOMER, REGION, STATUS, AMOUNT, ORDER_DATE, NOTES FROM ORDERS) ir_base WHERE \"STATUS\" = :p0 ORDER BY \"ORDER_DATE\" DESC OFFSET :p1 ROWS FETCH NEXT :p2 ROWS ONLY",
            page.Sql);
    }

    [Fact]
    public void Count_query_has_no_order_by_and_counts_star()
    {
        var (_, count) = Compile(ReportDialect.Sqlite, CoreState);

        Assert.Contains("COUNT(*)", count.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ORDER BY", count.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"STATUS\" = @p0", count.Sql);
    }

    [Fact]
    public void Contains_is_case_insensitive_with_lowered_binding()
    {
        var (page, _) = Compile(ReportDialect.Sqlite, new ReportState
        {
            Filters = [Filter("CUSTOMER", FilterOp.Contains, "ACME")],
        });

        Assert.Contains("LOWER(\"CUSTOMER\") like @p0", page.Sql);
        Assert.Contains("%acme%", page.NamedBindings.Values.Select(v => v?.ToString()));
    }

    [Fact]
    public void Blank_on_text_is_null_or_empty_outside_oracle()
    {
        var (sqlitePage, _) = Compile(ReportDialect.Sqlite, new ReportState
        {
            Filters = [Filter("NOTES", FilterOp.Blank)],
        });

        Assert.Contains("(\"NOTES\" IS NULL OR \"NOTES\" = @p0)", sqlitePage.Sql);
    }

    [Fact]
    public void Blank_on_text_is_pure_null_test_on_oracle()
    {
        var (oraclePage, _) = Compile(ReportDialect.Oracle, new ReportState
        {
            Filters = [Filter("NOTES", FilterOp.Blank)],
        });

        Assert.Contains("\"NOTES\" IS NULL", oraclePage.Sql);
        Assert.DoesNotContain("= :p", oraclePage.Sql);
    }

    [Fact]
    public void In_expands_to_bindings()
    {
        var (page, _) = Compile(ReportDialect.Sqlite, new ReportState
        {
            Filters = [Filter("STATUS", FilterOp.In, new[] { "NEW", "PENDING" })],
        });

        Assert.Contains("\"STATUS\" IN (@p0, @p1)", page.Sql);
        Assert.Equal(2, page.NamedBindings.Count(kv => kv.Value is "NEW" or "PENDING"));
    }

    [Fact]
    public void Between_binds_both_bounds()
    {
        var (page, _) = Compile(ReportDialect.Sqlite, new ReportState
        {
            Filters = [Filter("AMOUNT", FilterOp.Between, new[] { 100, 500 })],
        });

        Assert.Contains("\"AMOUNT\" BETWEEN @p0 AND @p1", page.Sql);
    }

    [Fact]
    public void Search_ors_across_text_columns_in_one_group()
    {
        var (page, _) = Compile(ReportDialect.Sqlite, new ReportState { Search = "acme" });

        // Text columns: CUSTOMER, REGION, STATUS, NOTES — one parenthesized OR group.
        Assert.Contains("(LOWER(\"CUSTOMER\") like @p0 OR LOWER(\"REGION\") like @p1 OR LOWER(\"STATUS\") like @p2 OR LOWER(\"NOTES\") like @p3)", page.Sql);
    }

    [Fact]
    public void Filters_and_search_compose_with_and()
    {
        var (page, _) = Compile(ReportDialect.Sqlite, new ReportState
        {
            Filters = [Filter("AMOUNT", FilterOp.Gt, 1000)],
            Search = "acme",
        });

        Assert.Contains("\"AMOUNT\" > @p0 AND (LOWER(", page.Sql);
    }

    [Fact]
    public void Aggregate_query_computes_over_filtered_set_without_paging()
    {
        var (_, _, aggregates, _) = CompileAll(ReportDialect.Sqlite, new ReportState
        {
            Filters = [Filter("STATUS", FilterOp.Eq, "SHIPPED")],
            Sorts = [new SortRule { Col = "ORDER_DATE", Dir = SortDir.Desc }],
            Page = new PageRequest { Index = 2, Size = 25 },
            Aggregates =
            [
                new AggregateRule { Col = "AMOUNT", Fn = AggregateFn.Sum },
                new AggregateRule { Col = "AMOUNT", Fn = AggregateFn.Avg },
                new AggregateRule { Col = "CUSTOMER", Fn = AggregateFn.CountDistinct },
            ],
        });

        Assert.Equal(
            "SELECT SUM(\"AMOUNT\") AS \"a0\", AVG(\"AMOUNT\") AS \"a1\", COUNT(DISTINCT \"CUSTOMER\") AS \"a2\" FROM (SELECT ORDER_ID, CUSTOMER, REGION, STATUS, AMOUNT, ORDER_DATE, NOTES FROM ORDERS) ir_base WHERE \"STATUS\" = @p0",
            aggregates!.Sql);
    }

    [Fact]
    public void SqlServer_avg_gets_float_cast_against_integer_truncation()
    {
        var (_, _, aggregates, _) = CompileAll(ReportDialect.SqlServer, new ReportState
        {
            Aggregates = [new AggregateRule { Col = "AMOUNT", Fn = AggregateFn.Avg }],
        });

        Assert.Contains("AVG(CAST([AMOUNT] AS FLOAT)) AS [a0]", aggregates!.Sql);
    }

    [Fact]
    public void Break_totals_group_the_filtered_set_with_row_counts()
    {
        var (_, _, _, breakTotals) = CompileAll(ReportDialect.Sqlite, new ReportState
        {
            Filters = [Filter("AMOUNT", FilterOp.Gt, 1000)],
            Breaks = ["REGION"],
            Aggregates = [new AggregateRule { Col = "AMOUNT", Fn = AggregateFn.Sum }],
        });

        Assert.Equal(
            "SELECT \"REGION\", COUNT(*) AS \"__rows\", SUM(\"AMOUNT\") AS \"a0\" FROM (SELECT ORDER_ID, CUSTOMER, REGION, STATUS, AMOUNT, ORDER_DATE, NOTES FROM ORDERS) ir_base WHERE \"AMOUNT\" > @p0 GROUP BY \"REGION\" ORDER BY \"REGION\"",
            breakTotals!.Sql);
    }

    [Fact]
    public void Breaks_sort_first_and_user_direction_on_break_column_wins()
    {
        var (page, _, _, _) = CompileAll(ReportDialect.Sqlite, new ReportState
        {
            Breaks = ["REGION"],
            Sorts =
            [
                new SortRule { Col = "REGION", Dir = SortDir.Desc },
                new SortRule { Col = "AMOUNT", Dir = SortDir.Asc },
            ],
        });

        Assert.Contains("ORDER BY \"REGION\" DESC, \"AMOUNT\"", page.Sql);
    }

    [Fact]
    public void Computed_columns_get_a_second_wrap_and_become_ordinary_columns()
    {
        var (page, _, _, _) = CompileAll(ReportDialect.Sqlite, new ReportState
        {
            Computed = [new ComputedColumn { Id = "c1", Label = "With Tax", Expr = "ROUND(AMOUNT * 1.0825, 2)" }],
            Columns = ["ORDER_ID", "c1"],
            Filters = [Filter("c1", FilterOp.Gt, 1000)],
            Sorts = [new SortRule { Col = "c1", Dir = SortDir.Desc }],
            Page = new PageRequest { Index = 1, Size = 10 },
        });

        Assert.Equal(
            "SELECT \"ORDER_ID\", \"c1\" FROM (SELECT \"ir_base\".*, ROUND((\"AMOUNT\" * @p0), @p1) AS \"c1\" FROM (SELECT ORDER_ID, CUSTOMER, REGION, STATUS, AMOUNT, ORDER_DATE, NOTES FROM ORDERS) ir_base) AS \"ir_calc\" WHERE \"c1\" > @p2 ORDER BY \"c1\" DESC LIMIT @p3",
            page.Sql);
        Assert.Equal([1.0825m, 2m, 1000m, 10], page.NamedBindings.Values.ToArray());
    }

    [Fact]
    public void Oracle_second_wrap_alias_has_no_AS_keyword()
    {
        var (page, _, _, _) = CompileAll(ReportDialect.Oracle, new ReportState
        {
            Computed = [new ComputedColumn { Id = "c1", Expr = "AMOUNT * 2" }],
        });

        Assert.Contains(") \"ir_calc\"", page.Sql);
        Assert.DoesNotContain("AS \"ir_calc\"", page.Sql);
    }

    [Fact]
    public void Aggregate_on_computed_column_rides_the_wrap()
    {
        var (_, _, aggregates, _) = CompileAll(ReportDialect.Sqlite, new ReportState
        {
            Computed = [new ComputedColumn { Id = "c1", Expr = "AMOUNT * 2" }],
            Aggregates = [new AggregateRule { Col = "c1", Fn = AggregateFn.Sum }],
        });

        Assert.Contains("SUM(\"c1\") AS \"a0\"", aggregates!.Sql);
        Assert.Contains("AS \"ir_calc\"", aggregates.Sql);
    }

    [Fact]
    public void SqlServer_paging_without_sort_still_compiles_valid_sql()
    {
        var (page, _) = Compile(ReportDialect.SqlServer, new ReportState
        {
            Page = new PageRequest { Index = 1, Size = 10 },
        });

        // SQL Server OFFSET requires ORDER BY; the compiler must inject a constant order.
        Assert.Contains("ORDER BY", page.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OFFSET", page.Sql, StringComparison.OrdinalIgnoreCase);
    }
}
