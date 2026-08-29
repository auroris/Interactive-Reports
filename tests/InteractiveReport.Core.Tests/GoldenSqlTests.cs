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

    private static ReportState CoreState => Doc(
        source: new StageLayer
        {
            Filters = [Filter("STATUS = 'SHIPPED'")],
            Sorts = [new SortRule { Col = "ORDER_DATE", Dir = SortDir.Desc }],
            Columns = ["ORDER_ID", "CUSTOMER", "AMOUNT"],
        },
        page: new PageRequest { Index = 2, Size = 25 });

    [Fact]
    public void Sqlite_page_query()
    {
        var (page, _) = Compile(ReportDialect.Sqlite, CoreState);

        Assert.Equal(
            "SELECT \"ORDER_ID\", \"CUSTOMER\", \"AMOUNT\" FROM (SELECT ORDER_ID, CUSTOMER, REGION, STATUS, AMOUNT, ORDER_DATE, NOTES FROM ORDERS) ir_base WHERE (\"STATUS\" = @p0) ORDER BY \"ORDER_DATE\" DESC LIMIT @p1 OFFSET @p2",
            page.Sql);
        Assert.Equal(["SHIPPED", 25, 25L], page.NamedBindings.Values.ToArray());
    }

    [Fact]
    public void SqlServer_page_query()
    {
        var (page, _) = Compile(ReportDialect.SqlServer, CoreState);

        Assert.Equal(
            "SELECT [ORDER_ID], [CUSTOMER], [AMOUNT] FROM (SELECT ORDER_ID, CUSTOMER, REGION, STATUS, AMOUNT, ORDER_DATE, NOTES FROM ORDERS) ir_base WHERE ([STATUS] = @p0) ORDER BY [ORDER_DATE] DESC OFFSET @p1 ROWS FETCH NEXT @p2 ROWS ONLY",
            page.Sql);
    }

    [Fact]
    public void Oracle_page_query()
    {
        var (page, _) = Compile(ReportDialect.Oracle, CoreState);

        Assert.Equal(
            "SELECT \"ORDER_ID\", \"CUSTOMER\", \"AMOUNT\" FROM (SELECT ORDER_ID, CUSTOMER, REGION, STATUS, AMOUNT, ORDER_DATE, NOTES FROM ORDERS) ir_base WHERE (\"STATUS\" = :p0) ORDER BY \"ORDER_DATE\" DESC OFFSET :p1 ROWS FETCH NEXT :p2 ROWS ONLY",
            page.Sql);
    }

    [Fact]
    public void Postgres_page_query()
    {
        var (page, _) = Compile(ReportDialect.Postgres, CoreState);

        Assert.Equal(
            "SELECT \"ORDER_ID\", \"CUSTOMER\", \"AMOUNT\" FROM (SELECT ORDER_ID, CUSTOMER, REGION, STATUS, AMOUNT, ORDER_DATE, NOTES FROM ORDERS) ir_base WHERE (\"STATUS\" = @p0) ORDER BY \"ORDER_DATE\" DESC LIMIT @p1 OFFSET @p2",
            page.Sql);
        Assert.Equal(["SHIPPED", 25, 25L], page.NamedBindings.Values.ToArray());
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
    public void All_rows_omits_limit_and_offset_when_max_rows_is_unlimited()
    {
        var state = Doc(
            source: new StageLayer { Sorts = [new SortRule { Col = "ORDER_ID" }] },
            page: new PageRequest { Index = 7, Size = 0 });
        var def = OrdersDefinition(ReportDialect.Sqlite);
        def.MaxRows = 0;
        var validated = StateValidator.Validate(def, state, OrdersSchema);
        var composed = QueryComposer.Compose(def, validated);
        var page = DialectSupport.GetCompiler(ReportDialect.Sqlite).Compile(composed.Page);

        Assert.DoesNotContain("LIMIT", page.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OFFSET", page.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(page.NamedBindings);
    }

    [Fact]
    public void Grid_query_projects_hidden_renderer_sources_without_display_metadata()
    {
        var state = Doc(source: new StageLayer
        {
            Columns = ["CUSTOMER"],
            Formats = new()
            {
                ["CUSTOMER"] = new ColumnFormat
                {
                    DisplayAs = "link",
                    UrlColumn = "NOTES",
                    TextColumn = "STATUS",
                },
            },
        });

        var (page, _) = Compile(ReportDialect.Sqlite, state);

        Assert.StartsWith("SELECT \"CUSTOMER\", \"NOTES\", \"STATUS\"", page.Sql);
    }

    [Fact]
    public void Grid_query_projects_edit_link_template_columns_without_display_metadata()
    {
        var def = OrdersDefinition(ReportDialect.Sqlite);
        def.EditLink = new ReportEditLink { UrlTemplate = "/orders/{ORDER_ID}/edit" };
        var validated = StateValidator.Validate(
            def,
            Doc(source: new StageLayer { Columns = ["CUSTOMER"] }),
            OrdersSchema);
        var composed = QueryComposer.Compose(def, validated);

        var page = DialectSupport.GetCompiler(ReportDialect.Sqlite).Compile(composed.Page);

        Assert.StartsWith("SELECT \"CUSTOMER\", \"ORDER_ID\"", page.Sql);
    }

    [Fact]
    public void Contains_is_case_insensitive_with_lowered_binding()
    {
        var (page, _) = Compile(ReportDialect.Sqlite, Doc(source: new StageLayer
        {
            Filters = [Filter("CONTAINS(CUSTOMER, 'ACME')")],
        }));

        Assert.Contains("LOWER(\"CUSTOMER\") LIKE LOWER", page.Sql);
        Assert.Equal(["%", "ACME", "%"], page.NamedBindings.Values.Take(3));
    }

    [Fact]
    public void Blank_on_text_is_null_or_empty_outside_oracle()
    {
        var (sqlitePage, _) = Compile(ReportDialect.Sqlite, Doc(source: new StageLayer
        {
            Filters = [Filter("NOTES IS NULL OR NOTES = ''")],
        }));

        Assert.Contains("(\"NOTES\" IS NULL)", sqlitePage.Sql);
        Assert.Contains("(\"NOTES\" = @p0)", sqlitePage.Sql);
    }

    [Fact]
    public void Explicit_empty_string_branch_is_preserved_on_oracle()
    {
        var (oraclePage, _) = Compile(ReportDialect.Oracle, Doc(source: new StageLayer
        {
            Filters = [Filter("NOTES IS NULL OR NOTES = ''")],
        }));

        Assert.Contains("\"NOTES\" IS NULL", oraclePage.Sql);
        Assert.Contains("= :p", oraclePage.Sql);
    }

    [Fact]
    public void In_expands_to_bindings()
    {
        var (page, _) = Compile(ReportDialect.Sqlite, Doc(source: new StageLayer
        {
            Filters = [Filter("IN_LIST(STATUS, 'NEW', 'PENDING')")],
        }));

        Assert.Contains("\"STATUS\" IN (@p0, @p1)", page.Sql);
        Assert.Equal(2, page.NamedBindings.Count(kv => kv.Value is "NEW" or "PENDING"));
    }

    [Fact]
    public void Between_binds_both_bounds()
    {
        var (page, _) = Compile(ReportDialect.Sqlite, Doc(source: new StageLayer
        {
            Filters = [Filter("AMOUNT BETWEEN 100 AND 500")],
        }));

        Assert.Contains("\"AMOUNT\" BETWEEN @p0 AND @p1", page.Sql);
    }

    [Fact]
    public void Search_ors_across_text_columns_in_one_group()
    {
        var (page, _) = Compile(ReportDialect.Sqlite, Doc(search: "acme"));

        // Text columns: CUSTOMER, REGION, STATUS, NOTES — one parenthesized OR group.
        Assert.Contains("(LOWER(\"CUSTOMER\") like @p0 OR LOWER(\"REGION\") like @p1 OR LOWER(\"STATUS\") like @p2 OR LOWER(\"NOTES\") like @p3)", page.Sql);
    }

    [Fact]
    public void Filters_and_search_compose_with_and()
    {
        var (page, _) = Compile(ReportDialect.Sqlite, Doc(
            source: new StageLayer { Filters = [Filter("AMOUNT > 1000")] },
            search: "acme"));

        Assert.Contains("(\"AMOUNT\" > @p0) AND (LOWER(", page.Sql);
    }

    [Fact]
    public void Aggregate_query_computes_over_filtered_set_without_paging()
    {
        var (_, _, aggregates, _) = CompileAll(ReportDialect.Sqlite, Doc(
            source: new StageLayer
            {
                Filters = [Filter("STATUS = 'SHIPPED'")],
                Sorts = [new SortRule { Col = "ORDER_DATE", Dir = SortDir.Desc }],
                Aggregates =
                [
                    new AggregateRule { Col = "AMOUNT", Fn = AggregateFn.Sum },
                    new AggregateRule { Col = "AMOUNT", Fn = AggregateFn.Avg },
                    new AggregateRule { Col = "CUSTOMER", Fn = AggregateFn.CountDistinct },
                ],
            },
            page: new PageRequest { Index = 2, Size = 25 }));

        Assert.Equal(
            "SELECT SUM(\"AMOUNT\") AS \"a0\", AVG(\"AMOUNT\") AS \"a1\", COUNT(DISTINCT \"CUSTOMER\") AS \"a2\" FROM (SELECT ORDER_ID, CUSTOMER, REGION, STATUS, AMOUNT, ORDER_DATE, NOTES FROM ORDERS) ir_base WHERE (\"STATUS\" = @p0)",
            aggregates!.Sql);
    }

    [Fact]
    public void SqlServer_avg_gets_float_cast_against_integer_truncation()
    {
        var (_, _, aggregates, _) = CompileAll(ReportDialect.SqlServer, Doc(source: new StageLayer
        {
            Aggregates = [new AggregateRule { Col = "AMOUNT", Fn = AggregateFn.Avg }],
        }));

        Assert.Contains("AVG(CAST([AMOUNT] AS FLOAT)) AS [a0]", aggregates!.Sql);
    }

    [Theory]
    [InlineData(ReportDialect.Sqlite)]
    [InlineData(ReportDialect.SqlServer)]
    [InlineData(ReportDialect.Oracle)]
    [InlineData(ReportDialect.Postgres)]
    public void Median_uses_the_portable_ranked_aggregate_shape(ReportDialect dialect)
    {
        var (_, _, aggregates, _) = CompileAll(dialect, Doc(source: new StageLayer
        {
            Aggregates =
            [
                new AggregateRule { Col = "AMOUNT", Fn = AggregateFn.Sum },
                new AggregateRule { Col = "AMOUNT", Fn = AggregateFn.Median },
            ],
        }));

        Assert.Contains("ROW_NUMBER() OVER", aggregates!.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("COUNT(", aggregates.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("__ir_median_rank_1", aggregates.Sql);
        Assert.Contains("__ir_median_count_1", aggregates.Sql);
        Assert.Contains("SUM(", aggregates.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AVG(", aggregates.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AS", aggregates.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Paged_break_query_fetches_one_boundary_row_without_changing_the_offset()
    {
        var (page, _, _, _) = CompileAll(ReportDialect.Sqlite, Doc(
            source: new StageLayer { Breaks = ["STATUS"] },
            page: new PageRequest { Index = 2, Size = 2 }));

        Assert.EndsWith("LIMIT @p0 OFFSET @p1", page.Sql);
        Assert.Equal([3, 2L], page.NamedBindings.Values.ToArray());
    }

    [Fact]
    public void Break_totals_group_the_filtered_set_with_row_counts()
    {
        var (_, _, _, breakTotals) = CompileAll(ReportDialect.Sqlite, Doc(source: new StageLayer
        {
            Filters = [Filter("AMOUNT > 1000")],
            Breaks = ["REGION"],
            Aggregates = [new AggregateRule { Col = "AMOUNT", Fn = AggregateFn.Sum }],
        }));

        Assert.Equal(
            "SELECT \"REGION\", COUNT(*) AS \"__count\", SUM(\"AMOUNT\") AS \"a0\" FROM (SELECT ORDER_ID, CUSTOMER, REGION, STATUS, AMOUNT, ORDER_DATE, NOTES FROM ORDERS) ir_base WHERE (\"AMOUNT\" > @p0) GROUP BY \"REGION\" ORDER BY \"REGION\"",
            breakTotals!.Sql);
    }

    [Fact]
    public void Breaks_sort_first_and_user_direction_on_break_column_wins()
    {
        var (page, _, _, _) = CompileAll(ReportDialect.Sqlite, Doc(source: new StageLayer
        {
            Breaks = ["REGION"],
            Sorts =
            [
                new SortRule { Col = "REGION", Dir = SortDir.Desc },
                new SortRule { Col = "AMOUNT", Dir = SortDir.Asc },
            ],
        }));

        Assert.Contains("ORDER BY \"REGION\" DESC, \"AMOUNT\"", page.Sql);
    }

    [Theory]
    [InlineData(ReportDialect.Sqlite, "ORDER BY \"NOTES\" DESC NULLS FIRST")]
    [InlineData(ReportDialect.Postgres, "ORDER BY \"NOTES\" DESC NULLS FIRST")]
    [InlineData(ReportDialect.Oracle, "ORDER BY \"NOTES\" DESC NULLS FIRST")]
    [InlineData(ReportDialect.SqlServer, "ORDER BY CASE WHEN [NOTES] IS NULL THEN 0 ELSE 1 END, [NOTES] DESC")]
    public void Explicit_null_placement_compiles_portably(ReportDialect dialect, string expected)
    {
        var (page, _) = Compile(dialect, Doc(source: new StageLayer
        {
            Sorts =
            [
                new SortRule
                {
                    Col = "NOTES",
                    Dir = SortDir.Desc,
                    Nulls = NullPlacement.First,
                },
            ],
        }));

        Assert.Contains(expected, page.Sql);
    }

    [Fact]
    public void Group_stage_dimension_sort_keeps_explicit_null_placement()
    {
        var def = OrdersDefinition(ReportDialect.Sqlite);
        var validated = StateValidator.Validate(def, Doc(tail:
        [
            Group(
                by: ["NOTES"],
                layer: new StageLayer { Sorts = [new SortRule { Col = "NOTES", Nulls = NullPlacement.Last }] }),
        ]), OrdersSchema);

        var (page, _) = QueryComposer.ComposeGroupStage(def, validated);
        var sql = DialectSupport.GetCompiler(ReportDialect.Sqlite).Compile(page).Sql;

        Assert.Contains("ORDER BY \"NOTES\" ASC NULLS LAST", sql);
    }

    [Fact]
    public void Computed_columns_get_a_second_wrap_and_become_ordinary_columns()
    {
        var (page, _, _, _) = CompileAll(ReportDialect.Sqlite, Doc(
            source: new StageLayer
            {
                Computed = [new ComputedColumn { Id = "c1", Label = "With Tax", Expr = "ROUND(AMOUNT * 1.0825, 2)" }],
                Columns = ["ORDER_ID", "c1"],
                Filters = [Filter("c1 > 1000")],
                Sorts = [new SortRule { Col = "c1", Dir = SortDir.Desc }],
            },
            page: new PageRequest { Index = 1, Size = 10 }));

        Assert.Equal(
            "SELECT \"ORDER_ID\", \"c1\" FROM (SELECT ir_base.*, ROUND((\"AMOUNT\" * @p0), @p1) AS \"c1\" FROM (SELECT ORDER_ID, CUSTOMER, REGION, STATUS, AMOUNT, ORDER_DATE, NOTES FROM ORDERS) ir_base) AS \"ir_calc\" WHERE (\"c1\" > @p2) ORDER BY \"c1\" DESC LIMIT @p3",
            page.Sql);
        Assert.Equal([1.0825m, 2m, 1000m, 10], page.NamedBindings.Values.ToArray());
    }

    [Fact]
    public void Oracle_second_wrap_alias_has_no_AS_keyword()
    {
        var (page, _, _, _) = CompileAll(ReportDialect.Oracle, Doc(source: new StageLayer
        {
            Computed = [new ComputedColumn { Id = "c1", Expr = "AMOUNT * 2" }],
        }));

        Assert.Contains(") \"ir_calc\"", page.Sql);
        Assert.DoesNotContain("AS \"ir_calc\"", page.Sql);
    }

    [Fact]
    public void Aggregate_on_computed_column_rides_the_wrap()
    {
        var (_, _, aggregates, _) = CompileAll(ReportDialect.Sqlite, Doc(source: new StageLayer
        {
            Computed = [new ComputedColumn { Id = "c1", Expr = "AMOUNT * 2" }],
            Aggregates = [new AggregateRule { Col = "c1", Fn = AggregateFn.Sum }],
        }));

        Assert.Contains("SUM(\"c1\") AS \"a0\"", aggregates!.Sql);
        Assert.Contains("AS \"ir_calc\"", aggregates.Sql);
    }

    [Fact]
    public void Group_stage_pages_groups_and_counts_them()
    {
        var def = OrdersDefinition(ReportDialect.Sqlite);
        var validated = StateValidator.Validate(def, Doc(
            tail: [Group(by: ["REGION"], values: [Metric("m1", "AMOUNT", AggregateFn.Sum)])],
            page: new PageRequest { Index = 1, Size = 10 }), OrdersSchema);
        var (page, count) = QueryComposer.ComposeGroupStage(def, validated);
        var compiler = DialectSupport.GetCompiler(ReportDialect.Sqlite);

        Assert.Equal(
            "SELECT \"REGION\", \"__count\", \"m1\" FROM (SELECT \"REGION\", COUNT(*) AS \"__count\", SUM(\"AMOUNT\") AS \"m1\" FROM (SELECT ORDER_ID, CUSTOMER, REGION, STATUS, AMOUNT, ORDER_DATE, NOTES FROM ORDERS) ir_base GROUP BY \"REGION\") AS \"ir_stage\" ORDER BY \"REGION\" LIMIT @p0",
            compiler.Compile(page).Sql);

        var countSql = compiler.Compile(count).Sql;
        Assert.StartsWith("SELECT COUNT(*)", countSql);
        Assert.Contains("GROUP BY \"REGION\"", countSql);
        Assert.Contains("\"ir_groups\"", countSql);
    }

    [Fact]
    public void Group_stage_orders_by_layer_metric_sort_then_remaining_dims()
    {
        var def = OrdersDefinition(ReportDialect.Sqlite);
        var validated = StateValidator.Validate(def, Doc(
            tail:
            [
                Group(
                    by: ["REGION", "STATUS"],
                    values: [Metric("m1", "AMOUNT", AggregateFn.Sum)],
                    layer: new StageLayer { Sorts = [new SortRule { Col = "m1", Dir = SortDir.Desc }] }),
            ],
            page: new PageRequest { Index = 1, Size = 10 }), OrdersSchema);
        var (page, _) = QueryComposer.ComposeGroupStage(def, validated);

        Assert.Equal(
            "SELECT \"REGION\", \"STATUS\", \"__count\", \"m1\" FROM (SELECT \"REGION\", \"STATUS\", COUNT(*) AS \"__count\", SUM(\"AMOUNT\") AS \"m1\" FROM (SELECT ORDER_ID, CUSTOMER, REGION, STATUS, AMOUNT, ORDER_DATE, NOTES FROM ORDERS) ir_base GROUP BY \"REGION\", \"STATUS\") AS \"ir_stage\" ORDER BY \"m1\" DESC, \"REGION\", \"STATUS\" LIMIT @p0",
            DialectSupport.GetCompiler(ReportDialect.Sqlite).Compile(page).Sql);
    }

    [Theory]
    [InlineData(ReportDialect.Sqlite, "\"REGION\"", "\"STATUS\"", "\"m1\"", "\"a0\"")]
    [InlineData(ReportDialect.SqlServer, "[REGION]", "[STATUS]", "[m1]", "[a0]")]
    [InlineData(ReportDialect.Oracle, "\"REGION\"", "\"STATUS\"", "\"m1\"", "\"a0\"")]
    [InlineData(ReportDialect.Postgres, "\"REGION\"", "\"STATUS\"", "\"m1\"", "\"a0\"")]
    public void Group_breaks_and_footer_aggregates_wrap_the_completed_stage_portably(
        ReportDialect dialect,
        string region,
        string status,
        string metric,
        string aggregateAlias)
    {
        var def = OrdersDefinition(dialect);
        var validated = StateValidator.Validate(def, Doc(
            tail:
            [
                Group(
                    by: ["REGION", "STATUS"],
                    values: [Metric("m1", "AMOUNT", AggregateFn.Sum)],
                    layer: new StageLayer
                    {
                        Breaks = ["REGION"],
                        Sorts = [new SortRule { Col = "STATUS", Dir = SortDir.Desc }],
                        Aggregates = [new AggregateRule { Col = "m1", Fn = AggregateFn.Sum }],
                    }),
            ],
            page: new PageRequest { Index = 1, Size = 10 }), OrdersSchema);
        var queries = QueryComposer.ComposeGroupStageQueries(def, validated);
        var compiler = DialectSupport.GetCompiler(dialect);
        var page = compiler.Compile(queries.Page).Sql;
        var footer = compiler.Compile(queries.Aggregates!).Sql;
        var breaks = compiler.Compile(queries.BreakTotals!).Sql;

        Assert.Contains($"ORDER BY {region}, {status} DESC", page);
        Assert.Contains($"SUM({metric}) AS {aggregateAlias}", footer);
        Assert.Contains($"GROUP BY {region}", breaks);
        Assert.Contains($"ORDER BY {region}", breaks);
        Assert.Contains("ir_groups", footer);
        Assert.Contains("ir_groups", breaks);
    }

    [Fact]
    public void Group_layer_computed_column_wraps_the_grouped_query_as_ir_stage()
    {
        var def = OrdersDefinition(ReportDialect.Sqlite);
        var validated = StateValidator.Validate(def, Doc(
            tail:
            [
                Group(
                    by: ["REGION"],
                    values: [Metric("m1", "AMOUNT", AggregateFn.Sum)],
                    layer: new StageLayer
                    {
                        Computed = [new ComputedColumn { Id = "c2", Label = "Per Row", Expr = "ROUND(m1 / __count, 2)" }],
                    }),
            ],
            page: new PageRequest { Index = 1, Size = 10 }), OrdersSchema);
        var (page, _) = QueryComposer.ComposeGroupStage(def, validated);

        Assert.Equal(
            "SELECT \"REGION\", \"__count\", \"m1\", \"c2\" FROM (SELECT ir_stage.*, ROUND(((1.0 * \"m1\") / \"__count\"), @p0) AS \"c2\" FROM (SELECT \"REGION\", COUNT(*) AS \"__count\", SUM(\"AMOUNT\") AS \"m1\" FROM (SELECT ORDER_ID, CUSTOMER, REGION, STATUS, AMOUNT, ORDER_DATE, NOTES FROM ORDERS) ir_base GROUP BY \"REGION\") AS \"ir_stage\") AS \"ir_stage_calc\" ORDER BY \"REGION\" LIMIT @p1",
            DialectSupport.GetCompiler(ReportDialect.Sqlite).Compile(page).Sql);
    }

    [Fact]
    public void Group_layer_highlight_on_computed_adds_the_ir_stage_calc_level()
    {
        var def = OrdersDefinition(ReportDialect.Sqlite);
        var validated = StateValidator.Validate(def, Doc(
            tail:
            [
                Group(
                    by: ["REGION"],
                    values: [Metric("m1", "AMOUNT", AggregateFn.Sum)],
                    layer: new StageLayer
                    {
                        Computed = [new ComputedColumn { Id = "c2", Expr = "ROUND(m1 / __count, 2)" }],
                        Highlights =
                        [
                            new HighlightRule
                            {
                                Id = "h1", Scope = "row", Expr = "c2 > 1000",
                                Style = new HighlightStyle { Bg = "#fee2e2" },
                            },
                        ],
                    }),
            ],
            page: new PageRequest { Index = 1, Size = 10 }), OrdersSchema);
        var (page, _) = QueryComposer.ComposeGroupStage(def, validated);

        Assert.Equal(
            "SELECT \"REGION\", \"__count\", \"m1\", \"c2\", CASE WHEN (\"c2\" > @p0) THEN 1 ELSE 0 END AS \"__ir_highlight_0\" FROM (SELECT ir_stage.*, ROUND(((1.0 * \"m1\") / \"__count\"), @p1) AS \"c2\" FROM (SELECT \"REGION\", COUNT(*) AS \"__count\", SUM(\"AMOUNT\") AS \"m1\" FROM (SELECT ORDER_ID, CUSTOMER, REGION, STATUS, AMOUNT, ORDER_DATE, NOTES FROM ORDERS) ir_base GROUP BY \"REGION\") AS \"ir_stage\") AS \"ir_stage_calc\" ORDER BY \"REGION\" LIMIT @p2",
            DialectSupport.GetCompiler(ReportDialect.Sqlite).Compile(page).Sql);
    }

    [Fact]
    public void Oracle_stage_wrap_aliases_have_no_AS_keyword()
    {
        var def = OrdersDefinition(ReportDialect.Oracle);
        var validated = StateValidator.Validate(def, Doc(
            tail:
            [
                Group(
                    by: ["REGION"],
                    values: [Metric("m1", "AMOUNT", AggregateFn.Sum)],
                    layer: new StageLayer
                    {
                        Computed = [new ComputedColumn { Id = "c2", Expr = "ROUND(m1 / __count, 2)" }],
                        Highlights =
                        [
                            new HighlightRule
                            {
                                Id = "h1", Scope = "row", Expr = "c2 > 1000",
                                Style = new HighlightStyle { Bg = "#fee2e2" },
                            },
                        ],
                    }),
            ]), OrdersSchema);
        var (page, _) = QueryComposer.ComposeGroupStage(def, validated);
        var sql = DialectSupport.GetCompiler(ReportDialect.Oracle).Compile(page).Sql;

        Assert.Contains(") \"ir_stage\"", sql);
        Assert.Contains(") \"ir_stage_calc\"", sql);
        Assert.DoesNotContain("AS \"ir_stage\"", sql);
        Assert.DoesNotContain("AS \"ir_stage_calc\"", sql);
    }

    [Fact]
    public void All_groups_omits_limit_and_offset_when_max_rows_is_unlimited()
    {
        var def = OrdersDefinition(ReportDialect.Sqlite);
        def.MaxRows = 0;
        var validated = StateValidator.Validate(def, Doc(
            tail: [Group(by: ["REGION"])],
            page: new PageRequest { Index = 7, Size = 0 }), OrdersSchema);
        var (page, _) = QueryComposer.ComposeGroupStage(def, validated);

        var sql = DialectSupport.GetCompiler(ReportDialect.Sqlite).Compile(page).Sql;
        Assert.DoesNotContain("LIMIT", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OFFSET", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Pivot_source_groups_all_dims_ordered_and_capped()
    {
        var def = OrdersDefinition(ReportDialect.Sqlite);
        var validated = StateValidator.Validate(def, Doc(tail:
        [
            Pivot(rows: ["REGION"], cols: ["STATUS"], values: [Metric("m1", "AMOUNT", AggregateFn.Sum)]),
        ]), OrdersSchema);
        var source = QueryComposer.ComposePivotSource(def, validated, 10_000);

        Assert.Equal(
            "SELECT \"REGION\", \"STATUS\", COUNT(*) AS \"__count\", SUM(\"AMOUNT\") AS \"m1\" FROM (SELECT ORDER_ID, CUSTOMER, REGION, STATUS, AMOUNT, ORDER_DATE, NOTES FROM ORDERS) ir_base GROUP BY \"REGION\", \"STATUS\" ORDER BY \"REGION\", \"STATUS\" LIMIT @p0",
            DialectSupport.GetCompiler(ReportDialect.Sqlite).Compile(source).Sql);
    }

    [Fact]
    public void Pivot_totals_reaggregate_by_column_dimensions()
    {
        var def = OrdersDefinition(ReportDialect.Sqlite);
        var validated = StateValidator.Validate(def, Doc(tail:
        [
            Pivot(rows: ["CUSTOMER"], cols: ["STATUS"], values: [Metric("m1", "AMOUNT", AggregateFn.Sum)], totals: true),
        ]), OrdersSchema);

        var totals = QueryComposer.ComposePivotTotals(def, validated);

        Assert.Equal(
            "SELECT \"STATUS\", COUNT(*) AS \"__count\", SUM(\"AMOUNT\") AS \"m1\" FROM (SELECT ORDER_ID, CUSTOMER, REGION, STATUS, AMOUNT, ORDER_DATE, NOTES FROM ORDERS) ir_base GROUP BY \"STATUS\" ORDER BY \"STATUS\"",
            DialectSupport.GetCompiler(ReportDialect.Sqlite).Compile(totals).Sql);
    }

    private static SqlResult CompileChart(ReportDialect dialect, ReportState state, int maxPoints = 1000)
    {
        var def = OrdersDefinition(dialect);
        var validated = StateValidator.Validate(def, state, OrdersSchema);
        var query = QueryComposer.ComposeChartView(def, validated, maxPoints);
        return DialectSupport.GetCompiler(dialect).Compile(query);
    }

    [Fact]
    public void Chart_grouped_query_orders_by_the_metric_with_label_tiebreak()
    {
        var sql = CompileChart(ReportDialect.Sqlite, Doc(
            source: new StageLayer { Filters = [Filter("STATUS <> 'CANCELLED'")] },
            tail:
            [
                ChartStage(shape =>
                {
                    shape.Type = "bar";
                    shape.Label = "STATUS";
                    shape.Value = "AMOUNT";
                    shape.Fn = AggregateFn.Sum;
                    shape.Sort = new ChartSortSpec { By = "value", Dir = SortDir.Desc };
                }),
            ]));

        Assert.Equal(
            "SELECT \"STATUS\", COUNT(*) AS \"__count\", SUM(\"AMOUNT\") AS \"m0\" FROM (SELECT ORDER_ID, CUSTOMER, REGION, STATUS, AMOUNT, ORDER_DATE, NOTES FROM ORDERS) ir_base WHERE (\"STATUS\" <> @p0) GROUP BY \"STATUS\" ORDER BY \"m0\" DESC, \"STATUS\" LIMIT @p1",
            sql.Sql);
        Assert.Equal(["CANCELLED", 1001], sql.NamedBindings.Values.ToArray());
    }

    [Fact]
    public void Chart_count_alone_groups_on_the_row_count()
    {
        var sql = CompileChart(ReportDialect.Sqlite, Doc(tail:
        [
            ChartStage(shape =>
            {
                shape.Type = "pie";
                shape.Label = "STATUS";
                shape.Fn = AggregateFn.Count;
            }),
        ]));

        Assert.Equal(
            "SELECT \"STATUS\", COUNT(*) AS \"__count\" FROM (SELECT ORDER_ID, CUSTOMER, REGION, STATUS, AMOUNT, ORDER_DATE, NOTES FROM ORDERS) ir_base GROUP BY \"STATUS\" ORDER BY \"STATUS\" LIMIT @p0",
            sql.Sql);
    }

    [Fact]
    public void Chart_count_value_sort_orders_by_the_count_alias()
    {
        var sql = CompileChart(ReportDialect.Sqlite, Doc(tail:
        [
            ChartStage(shape =>
            {
                shape.Type = "bar";
                shape.Label = "STATUS";
                shape.Fn = AggregateFn.Count;
                shape.Sort = new ChartSortSpec { By = "value", Dir = SortDir.Desc };
            }),
        ]));

        Assert.Contains("ORDER BY \"__count\" DESC, \"STATUS\"", sql.Sql);
    }

    [Fact]
    public void Chart_without_fn_selects_raw_label_value_pairs()
    {
        var sql = CompileChart(ReportDialect.Sqlite, Doc(tail:
        [
            ChartStage(shape =>
            {
                shape.Type = "line";
                shape.Label = "ORDER_DATE";
                shape.Value = "AMOUNT";
            }),
        ]));

        Assert.Equal(
            "SELECT \"ORDER_DATE\", \"AMOUNT\" FROM (SELECT ORDER_ID, CUSTOMER, REGION, STATUS, AMOUNT, ORDER_DATE, NOTES FROM ORDERS) ir_base ORDER BY \"ORDER_DATE\" LIMIT @p0",
            sql.Sql);
    }

    [Fact]
    public void SqlServer_and_oracle_chart_queries_group_and_cap()
    {
        static ReportState ChartState() => Doc(tail:
        [
            ChartStage(shape =>
            {
                shape.Type = "bar";
                shape.Label = "STATUS";
                shape.Value = "AMOUNT";
                shape.Fn = AggregateFn.Avg;
                shape.Sort = new ChartSortSpec { By = "value", Dir = SortDir.Desc };
            }),
        ]);

        var sqlServer = CompileChart(ReportDialect.SqlServer, ChartState()).Sql;
        Assert.Contains("AVG(CAST([AMOUNT] AS FLOAT)) AS [m0]", sqlServer);
        Assert.Contains("GROUP BY [STATUS]", sqlServer);
        Assert.Contains("ORDER BY [m0] DESC, [STATUS]", sqlServer);

        var oracle = CompileChart(ReportDialect.Oracle, ChartState()).Sql;
        Assert.Contains("AVG(\"AMOUNT\") AS \"m0\"", oracle);
        Assert.Contains("GROUP BY \"STATUS\"", oracle);
        Assert.Contains("ORDER BY \"m0\" DESC, \"STATUS\"", oracle);
    }

    [Fact]
    public void SqlServer_paging_without_sort_still_compiles_valid_sql()
    {
        var (page, _) = Compile(ReportDialect.SqlServer, Doc(
            page: new PageRequest { Index = 1, Size = 10 }));

        // SQL Server OFFSET requires ORDER BY; the compiler must inject a constant order.
        Assert.Contains("ORDER BY", page.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OFFSET", page.Sql, StringComparison.OrdinalIgnoreCase);
    }
}
