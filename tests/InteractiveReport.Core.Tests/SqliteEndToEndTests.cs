using System.Data.Common;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Schema;
using Microsoft.Data.Sqlite;
using static InteractiveReport.Core.Tests.TestFixtures;

namespace InteractiveReport.Core.Tests;

/// <summary>
/// Full engine pass — discover, validate, compose, execute — against a real (in-memory,
/// shared-cache) SQLite database with hand-written rows, so assertions are exact.
/// </summary>
public sealed class SqliteEndToEndTests : IClassFixture<SqliteE2EFixture>
{
    private readonly SqliteE2EFixture _db;
    private readonly ReportExecutor _executor;

    public SqliteEndToEndTests(SqliteE2EFixture db)
    {
        _db = db;
        _executor = new ReportExecutor(db, new SchemaCache());
    }

    private static readonly IReadOnlyDictionary<string, object?> NoParams = new Dictionary<string, object?>();

    private ReportDefinition Definition => new()
    {
        Name = "orders-e2e",
        Connection = "E2E",
        Dialect = ReportDialect.Sqlite,
        Sql = "SELECT ORDER_ID, CUSTOMER, STATUS, AMOUNT, NOTES FROM ORDERS",
    };

    [Fact]
    public async Task Schema_is_discovered_from_the_probe()
    {
        var schema = await _executor.GetSchema(Definition, NoParams);

        Assert.Equal(["ORDER_ID", "CUSTOMER", "STATUS", "AMOUNT", "NOTES"], schema.Select(c => c.Name));
        Assert.Equal(ColumnKind.Number, schema.Single(c => c.Name == "AMOUNT").Kind);
        Assert.Equal(ColumnKind.Text, schema.Single(c => c.Name == "CUSTOMER").Kind);
    }

    [Fact]
    public async Task Friendly_names_never_touch_discovery_or_query_results()
    {
        // columnLabels is configuration the schema endpoint hands to the client;
        // the engine's schema and query metadata stay on server-derived labels,
        // and a state's labels map is opaque display state on this path.
        var def = Definition;
        def.ColumnLabels = new() { ["ORDER_ID"] = "Order #" };

        var schema = await _executor.GetSchema(def, NoParams);
        Assert.Equal("Order Id", schema.Lookup["ORDER_ID"].Label);

        var result = await _executor.Query(def, new ReportState
        {
            Labels = new() { ["CUSTOMER"] = "Client", ["GHOST"] = "Opaque" },
        }, NoParams);

        Assert.Equal("Customer", result.AvailableColumns.Single(c => c.Name == "CUSTOMER").Label);
        Assert.Empty(result.Ignored);
    }

    [Fact]
    public async Task Grid_rows_include_hidden_renderer_sources_but_metadata_and_exports_do_not()
    {
        var state = new ReportState
        {
            Columns = ["CUSTOMER"],
            Formats = new()
            {
                ["CUSTOMER"] = new ColumnFormat
                {
                    DisplayAs = "link",
                    UrlColumn = "NOTES",
                    TextColumn = "CUSTOMER",
                },
            },
        };

        var result = await _executor.Query(Definition, state, NoParams);
        Assert.Equal(["CUSTOMER"], result.Columns.Select(c => c.Name));
        Assert.All(result.Rows, row => Assert.Equal(["CUSTOMER", "NOTES"], row.Keys));

        var export = await _executor.Export(Definition, state, NoParams);
        Assert.Equal(["CUSTOMER"], export.Columns.Select(c => c.Name));
        Assert.All(export.Rows, row => Assert.Equal(["CUSTOMER"], row.Keys));
    }

    [Fact]
    public async Task Hidden_computed_column_can_use_hidden_base_data_and_feed_a_link_renderer()
    {
        var state = new ReportState
        {
            Computed =
            [
                new ComputedColumn
                {
                    Id = "c1",
                    Label = "Order URL",
                    Expr = "'/orders/' || ORDER_ID",
                },
            ],
            Columns = ["CUSTOMER"],
            Sorts = [new SortRule { Col = "ORDER_ID" }],
            Formats = new()
            {
                ["CUSTOMER"] = new ColumnFormat
                {
                    DisplayAs = "link",
                    UrlColumn = "c1",
                    TextColumn = "CUSTOMER",
                },
            },
            Page = new PageRequest { Index = 1, Size = 1 },
        };

        var result = await _executor.Query(Definition, state, NoParams);

        Assert.Equal(["CUSTOMER"], result.Columns.Select(c => c.Name));
        Assert.True(result.AvailableColumns.Single(c => c.Name == "c1").Computed);
        var row = Assert.Single(result.Rows);
        Assert.Equal(["CUSTOMER", "c1"], row.Keys);
        Assert.Equal("/orders/1", row["c1"]);
        Assert.DoesNotContain("ORDER_ID", row.Keys);

        var export = await _executor.Export(Definition, state, NoParams);
        Assert.Equal(["CUSTOMER"], export.Columns.Select(c => c.Name));
        Assert.Equal("<a class=\"ir-cell-link\" href=\"/orders/1\">Acme Corp</a>", export.Rows[0]["CUSTOMER"]);
        Assert.All(export.Rows, exported => Assert.DoesNotContain("c1", exported.Keys));
    }

    [Fact]
    public async Task Grid_export_renderers_emit_browser_like_encoded_html()
    {
        var state = new ReportState
        {
            Computed =
            [
                new ComputedColumn { Id = "c1", Expr = "'/orders/' || ORDER_ID || '?a=1&b=2'" },
                new ComputedColumn { Id = "c2", Expr = "'/images/' || ORDER_ID || '.png?a=1&b=2'" },
                new ComputedColumn { Id = "c3", Expr = "'<Order & Customer>'" },
                new ComputedColumn { Id = "c4", Expr = "'javascript:alert(1)'" },
            ],
            Columns = ["CUSTOMER", "NOTES", "STATUS"],
            Filters = [Filter("ORDER_ID = 1")],
            Formats = new()
            {
                ["CUSTOMER"] = new ColumnFormat
                {
                    DisplayAs = "link",
                    UrlColumn = "c1",
                    TextColumn = "c3",
                },
                ["NOTES"] = new ColumnFormat
                {
                    DisplayAs = "image",
                    UrlColumn = "c2",
                },
                ["STATUS"] = new ColumnFormat
                {
                    DisplayAs = "link",
                    UrlColumn = "c4",
                    TextColumn = "c3",
                },
            },
        };

        var export = await _executor.Export(Definition, state, NoParams);
        var row = Assert.Single(export.Rows);

        Assert.Equal(
            "<a class=\"ir-cell-link\" href=\"/orders/1?a=1&amp;b=2\">&lt;Order &amp; Customer&gt;</a>",
            row["CUSTOMER"]);
        Assert.Equal(
            "<img class=\"ir-cell-image\" src=\"/images/1.png?a=1&amp;b=2\" alt=\"\" loading=\"lazy\" decoding=\"async\">",
            row["NOTES"]);
        Assert.Equal("&lt;Order &amp; Customer&gt;", row["STATUS"]);
        Assert.Equal(["CUSTOMER", "NOTES", "STATUS"], row.Keys);
    }

    [Fact]
    public async Task Grid_export_link_text_uses_the_source_columns_mask()
    {
        var export = await _executor.Export(Definition, new ReportState
        {
            Computed = [new ComputedColumn { Id = "c1", Expr = "'/orders/' || ORDER_ID" }],
            Columns = ["CUSTOMER"],
            Filters = [Filter("ORDER_ID = 1")],
            Formats = new()
            {
                ["CUSTOMER"] = new ColumnFormat
                {
                    DisplayAs = "link",
                    UrlColumn = "c1",
                    TextColumn = "AMOUNT",
                },
                ["AMOUNT"] = new ColumnFormat { Mask = "currency:USD" },
            },
        }, NoParams);

        Assert.Equal(
            "<a class=\"ir-cell-link\" href=\"/orders/1\">$9,000.00</a>",
            Assert.Single(export.Rows)["CUSTOMER"]);
    }

    [Fact]
    public async Task Export_applies_the_documents_labels_because_it_renders_what_the_user_sees()
    {
        var def = Definition;
        def.ColumnLabels = new() { ["ORDER_ID"] = "Order #" };

        // The posted document is the source of truth — this one was never saved
        // server-side. Its labels override the configured mapping wholesale.
        var grid = await _executor.Export(def, new ReportState
        {
            Columns = ["ORDER_ID", "AMOUNT"],
            Labels = new() { ["AMOUNT"] = "Order Total" },
        }, NoParams);

        Assert.Equal(["ORDER_ID", "AMOUNT"], grid.Columns.Select(c => c.Name));
        Assert.Equal(["Order Id", "Order Total"], grid.Columns.Select(c => c.Label));

        // A document with no labels of its own falls back to the configured mapping,
        // and synthetic aggregate columns rebuild their labels from the display name.
        var grouped = await _executor.Export(def, new ReportState
        {
            View = new ViewSpec
            {
                Mode = "groupBy",
                GroupBy = ["STATUS"],
                Values = [new AggregateRule { Col = "ORDER_ID", Fn = AggregateFn.Max }],
            },
        }, NoParams);

        Assert.Equal(["Status", "Count", "max(Order #)"], grouped.Columns.Select(c => c.Label));
    }

    [Fact]
    public async Task Filter_sort_page_end_to_end()
    {
        var result = await _executor.Query(Definition, new ReportState
        {
            Filters = [Filter("STATUS = 'SHIPPED'")],
            Sorts = [new SortRule { Col = "AMOUNT", Dir = SortDir.Desc }],
            Page = new PageRequest { Index = 1, Size = 3 },
        }, NoParams);

        Assert.Equal(5, result.TotalRows);                       // 5 SHIPPED rows seeded
        Assert.Equal(3, result.Rows.Count);                      // but only a page of 3
        var amounts = result.Rows.Select(r => Convert.ToDecimal(r["AMOUNT"])).ToArray();
        Assert.Equal([9000m, 7500m, 5000m], amounts);            // descending, from the top
    }

    [Fact]
    public async Task Second_page_continues_the_ordering()
    {
        var state = new ReportState
        {
            Filters = [Filter("STATUS = 'SHIPPED'")],
            Sorts = [new SortRule { Col = "AMOUNT", Dir = SortDir.Desc }],
            Page = new PageRequest { Index = 2, Size = 3 },
        };

        var result = await _executor.Query(Definition, state, NoParams);

        Assert.Equal(5, result.TotalRows);
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal([3000m, 1500m], result.Rows.Select(r => Convert.ToDecimal(r["AMOUNT"])));
    }

    [Fact]
    public async Task Explicit_null_placement_controls_query_and_export_order()
    {
        var nullsLast = new ReportState
        {
            Sorts =
            [
                new SortRule { Col = "NOTES", Nulls = NullPlacement.Last },
                new SortRule { Col = "ORDER_ID" },
            ],
        };

        var query = await _executor.Query(Definition, nullsLast, NoParams);
        Assert.All(query.Rows.Take(7), row => Assert.NotNull(row["NOTES"]));
        Assert.All(query.Rows.TakeLast(3), row => Assert.Null(row["NOTES"]));

        var export = await _executor.Export(Definition, nullsLast, NoParams);
        Assert.All(export.Rows.Take(7), row => Assert.NotNull(row["NOTES"]));
        Assert.All(export.Rows.TakeLast(3), row => Assert.Null(row["NOTES"]));

        var nullsFirst = await _executor.Query(Definition, new ReportState
        {
            Sorts =
            [
                new SortRule { Col = "NOTES", Dir = SortDir.Desc, Nulls = NullPlacement.First },
                new SortRule { Col = "ORDER_ID" },
            ],
        }, NoParams);
        Assert.All(nullsFirst.Rows.Take(3), row => Assert.Null(row["NOTES"]));
        Assert.All(nullsFirst.Rows.Skip(3), row => Assert.NotNull(row["NOTES"]));
    }

    [Fact]
    public async Task All_bypasses_query_limits_but_export_still_uses_its_independent_cap()
    {
        var def = Definition;
        def.MaxRows = 3;
        def.MaxPageSize = 3;
        def.DefaultPageSize = 3;
        var state = new ReportState
        {
            Sorts = [new SortRule { Col = "ORDER_ID" }],
            Page = new PageRequest { Index = 8, Size = 0 },
        };

        var query = await _executor.Query(def, state, NoParams);

        Assert.Equal(10, query.TotalRows);
        Assert.Equal(10, query.Rows.Count);
        Assert.Equal(1, query.Page.Index);
        Assert.Equal(0, query.Page.Size);

        var export = await _executor.Export(def, state, NoParams);
        Assert.True(export.Truncated);
        Assert.Equal(3, export.Rows.Count);
    }

    [Fact]
    public async Task Search_is_case_insensitive_across_text_columns()
    {
        var result = await _executor.Query(Definition, new ReportState { Search = "ACME" }, NoParams);

        // 'Acme Corp' ×2 and 'acme llc' ×1
        Assert.Equal(3, result.TotalRows);
    }

    [Fact]
    public async Task Blank_matches_null_and_empty_string_on_sqlite()
    {
        var result = await _executor.Query(Definition, new ReportState
        {
            Filters = [Filter("NOTES IS NULL OR NOTES = ''")],
        }, NoParams);

        Assert.Equal(4, result.TotalRows);                       // 3 NULL + 1 ''
    }

    [Fact]
    public async Task Disabled_condition_with_removed_column_remains_loadable()
    {
        var result = await _executor.Query(Definition, new ReportState
        {
            Filters = [new FilterRule { Enabled = false, Expr = "REMOVED_COLUMN = 'x'" }],
        }, NoParams);

        Assert.Equal(10, result.TotalRows);
        Assert.Empty(result.Ignored);
    }

    [Fact]
    public async Task Between_and_in_compose_together()
    {
        var result = await _executor.Query(Definition, new ReportState
        {
            Filters =
            [
                Filter("AMOUNT BETWEEN 1000 AND 8000"),
                Filter("IN_LIST(STATUS, 'SHIPPED', 'PENDING')"),
            ],
        }, NoParams);

        // SHIPPED: 7500, 5000, 3000, 1500 in range; PENDING: 2000 in range
        Assert.Equal(5, result.TotalRows);
    }

    [Fact]
    public async Task Aggregates_cover_the_whole_filtered_set_not_the_page()
    {
        var result = await _executor.Query(Definition, new ReportState
        {
            Filters = [Filter("STATUS = 'SHIPPED'")],
            Page = new PageRequest { Index = 1, Size = 2 },
            Aggregates =
            [
                new AggregateRule { Col = "AMOUNT", Fn = AggregateFn.Sum },
                new AggregateRule { Col = "AMOUNT", Fn = AggregateFn.Avg },
                new AggregateRule { Col = "AMOUNT", Fn = AggregateFn.Median },
                new AggregateRule { Col = "AMOUNT", Fn = AggregateFn.Min },
                new AggregateRule { Col = "AMOUNT", Fn = AggregateFn.Max },
            ],
        }, NoParams);

        Assert.Equal(2, result.Rows.Count);                      // page of 2...
        var amount = result.Aggregates["AMOUNT"];
        Assert.Equal(26000m, Convert.ToDecimal(amount["sum"]));  // ...totals over all 5
        Assert.Equal(5200m, Convert.ToDecimal(amount["avg"]));
        Assert.Equal(5000m, Convert.ToDecimal(amount["median"]));
        Assert.Equal(1500m, Convert.ToDecimal(amount["min"]));
        Assert.Equal(9000m, Convert.ToDecimal(amount["max"]));
    }

    [Fact]
    public async Task Median_ignores_nulls_and_averages_the_middle_pair()
    {
        var result = await _executor.Query(Definition, new ReportState
        {
            Computed =
            [
                new ComputedColumn
                {
                    Id = "c1",
                    Expr = "CASE WHEN ORDER_ID = 1 THEN NULL ELSE AMOUNT END",
                },
            ],
            Filters = [Filter("ORDER_ID <= 5")],
            Aggregates = [new AggregateRule { Col = "c1", Fn = AggregateFn.Median }],
        }, NoParams);

        Assert.Equal(4000m, Convert.ToDecimal(result.Aggregates["c1"]["median"]));
    }

    [Fact]
    public async Task CountDistinct_counts_values_not_rows()
    {
        var result = await _executor.Query(Definition, new ReportState
        {
            Aggregates = [new AggregateRule { Col = "CUSTOMER", Fn = AggregateFn.CountDistinct }],
        }, NoParams);

        // 10 rows, but 'Acme Corp' appears twice → 9 distinct
        Assert.Equal(9L, Convert.ToInt64(result.Aggregates["CUSTOMER"]["countDistinct"]));
    }

    [Fact]
    public async Task Break_totals_group_and_order_like_the_page()
    {
        var result = await _executor.Query(Definition, new ReportState
        {
            Breaks = ["STATUS"],
            Aggregates =
            [
                new AggregateRule { Col = "AMOUNT", Fn = AggregateFn.Sum },
                new AggregateRule { Col = "AMOUNT", Fn = AggregateFn.Median },
            ],
        }, NoParams);

        Assert.Equal(
            ["CANCELLED", "NEW", "PENDING", "SHIPPED"],
            result.BreakTotals.Select(b => (string)b.Key["STATUS"]!));
        Assert.Equal(4, result.BreakTotals.Count);
        Assert.Equal([1L, 1L, 3L, 5L], result.BreakTotals.Select(b => b.Rows));
        Assert.Equal(
            [6000m, 400m, 14800m, 26000m],
            result.BreakTotals.Select(b => Convert.ToDecimal(b.Aggregates["AMOUNT"]["sum"])));
        Assert.Equal(
            [6000m, 400m, 2000m, 5000m],
            result.BreakTotals.Select(b => Convert.ToDecimal(b.Aggregates["AMOUNT"]["median"])));

        // Page rows arrive grouped: STATUS sorts first even with no user sort.
        Assert.Equal("CANCELLED", result.Rows[0]["STATUS"]);
    }

    [Fact]
    public async Task Break_paging_reports_continuation_without_leaking_the_boundary_row()
    {
        var continuing = await _executor.Query(Definition, new ReportState
        {
            Breaks = ["STATUS"],
            Page = new PageRequest { Index = 2, Size = 2 },
        }, NoParams);

        Assert.Equal(2, continuing.Rows.Count);
        Assert.All(continuing.Rows, row => Assert.Equal("PENDING", row["STATUS"]));
        Assert.True(continuing.BreakContinues);

        var final = await _executor.Query(Definition, new ReportState
        {
            Breaks = ["STATUS"],
            Page = new PageRequest { Index = 5, Size = 2 },
        }, NoParams);

        Assert.Equal(2, final.Rows.Count);
        Assert.All(final.Rows, row => Assert.Equal("SHIPPED", row["STATUS"]));
        Assert.False(final.BreakContinues);
    }

    [Fact]
    public async Task User_sort_direction_on_break_column_reverses_groups()
    {
        var result = await _executor.Query(Definition, new ReportState
        {
            Breaks = ["STATUS"],
            Sorts = [new SortRule { Col = "STATUS", Dir = SortDir.Desc }],
        }, NoParams);

        Assert.Equal("SHIPPED", result.BreakTotals[0].Key["STATUS"]);
        Assert.Equal("SHIPPED", result.Rows[0]["STATUS"]);
    }

    [Fact]
    public async Task Aggregates_over_empty_filtered_set_are_null()
    {
        var result = await _executor.Query(Definition, new ReportState
        {
            Filters = [Filter("STATUS = 'NO_SUCH_STATUS'")],
            Aggregates = [new AggregateRule { Col = "AMOUNT", Fn = AggregateFn.Sum }],
        }, NoParams);

        Assert.Equal(0, result.TotalRows);
        Assert.Null(result.Aggregates["AMOUNT"]["sum"]);
    }

    [Fact]
    public async Task Computed_column_filters_sorts_and_returns_values()
    {
        var result = await _executor.Query(Definition, new ReportState
        {
            Computed = [new ComputedColumn { Id = "c1", Label = "Double", Expr = "ROUND(AMOUNT * 2, 0)" }],
            Columns = ["CUSTOMER", "c1"],
            Filters = [Filter("c1 >= 10000")],
            Sorts = [new SortRule { Col = "c1", Dir = SortDir.Desc }],
        }, NoParams);

        // Doubled amounts ≥ 10000: 24000 (Stark), 18000 (Acme 9000), 15000 (Globex), 12000 (Tyrell), 10000 (Initech)
        Assert.Equal(5, result.TotalRows);
        Assert.Equal(["CUSTOMER", "c1"], result.Columns.Select(c => c.Name));
        Assert.True(result.Columns.Single(c => c.Name == "c1").Computed);
        var availableComputed = result.AvailableColumns.Single(c => c.Name == "c1");
        Assert.True(availableComputed.Computed);
        Assert.Equal("number", availableComputed.Type);
        Assert.Equal(24000m, Convert.ToDecimal(result.Rows[0]["c1"]));
        Assert.Equal("Stark Ind", result.Rows[0]["CUSTOMER"]);
    }

    [Fact]
    public async Task Aggregate_over_computed_column()
    {
        var result = await _executor.Query(Definition, new ReportState
        {
            Computed = [new ComputedColumn { Id = "c1", Expr = "AMOUNT * 2" }],
            Aggregates = [new AggregateRule { Col = "c1", Fn = AggregateFn.Sum }],
        }, NoParams);

        Assert.Equal(94400m, Convert.ToDecimal(result.Aggregates["c1"]["sum"]));   // 2 × 47200
    }

    [Fact]
    public async Task Text_computed_column_concatenates()
    {
        var result = await _executor.Query(Definition, new ReportState
        {
            Computed = [new ComputedColumn { Id = "c1", Expr = "UPPER(CUSTOMER) || '!'" }],
            Filters = [Filter("CUSTOMER = 'Globex'")],
        }, NoParams);

        Assert.Equal("GLOBEX!", Assert.Single(result.Rows)["c1"]);
    }

    [Fact]
    public async Task Case_computed_column_filters_and_sorts_like_any_other()
    {
        var result = await _executor.Query(Definition, new ReportState
        {
            Computed = [new ComputedColumn
            {
                Id = "c1",
                Label = "Size",
                Expr = "CASE WHEN AMOUNT >= 6000 THEN 'BIG' WHEN AMOUNT >= 2000 THEN 'MID' ELSE 'SMALL' END",
            }],
            Columns = ["CUSTOMER", "AMOUNT", "c1"],
            Filters = [Filter("c1 = 'BIG'")],
            Sorts = [new SortRule { Col = "AMOUNT", Dir = SortDir.Desc }],
        }, NoParams);

        // BIG = amounts ≥ 6000: Stark 12000, Acme 9000, Globex 7500, Tyrell 6000.
        Assert.Equal(4, result.TotalRows);
        Assert.Equal(["Stark Ind", "Acme Corp", "Globex", "Tyrell Corp"],
            result.Rows.Select(r => (string)r["CUSTOMER"]!));
        Assert.All(result.Rows, r => Assert.Equal("BIG", r["c1"]));
    }

    [Fact]
    public async Task Case_with_null_test_condition_aggregates()
    {
        var result = await _executor.Query(Definition, new ReportState
        {
            Computed = [new ComputedColumn
            {
                Id = "c1",
                Expr = "CASE WHEN NOTES IS NULL OR NOTES = '' THEN 0 ELSE 1 END",
            }],
            Aggregates = [new AggregateRule { Col = "c1", Fn = AggregateFn.Sum }],
        }, NoParams);

        // Rows with a real note: rush, fragile, call first, insured, standard, refunded.
        Assert.Equal(6m, Convert.ToDecimal(result.Aggregates["c1"]["sum"]));
    }

    // ORDER_DATE is stored (and discovered) as ISO text — the SQLite date story.
    // TO_DATE gives it the logical Date type; everything downstream is the portable
    // date vocabulary running on canonical datetime() text.
    private ReportDefinition DateDefinition
    {
        get
        {
            var def = Definition;
            def.Sql = "SELECT ORDER_ID, CUSTOMER, AMOUNT, ORDER_DATE FROM ORDERS";
            return def;
        }
    }

    [Fact]
    public async Task Date_window_between_counts_2026_orders()
    {
        var result = await _executor.Query(DateDefinition, new ReportState
        {
            Computed = [new ComputedColumn
            {
                Id = "c1",
                Expr = "CASE WHEN TO_DATE(ORDER_DATE) BETWEEN TO_DATE('2026-01-01') AND TO_DATE('2026-12-31') THEN 1 ELSE 0 END",
            }],
            Aggregates = [new AggregateRule { Col = "c1", Fn = AggregateFn.Sum }],
        }, NoParams);

        Assert.Equal(5m, Convert.ToDecimal(result.Aggregates["c1"]["sum"]));
    }

    [Fact]
    public async Task Between_is_inclusive_and_does_not_reorder_date_bounds()
    {
        var result = await _executor.Query(DateDefinition, new ReportState
        {
            Computed =
            [
                new ComputedColumn
                {
                    Id = "c1",
                    Expr = "CASE WHEN TO_DATE(ORDER_DATE) BETWEEN TO_DATE('2026-02-08') AND TO_DATE('2026-02-08') THEN 1 ELSE 0 END",
                },
                new ComputedColumn
                {
                    Id = "c2",
                    Expr = "CASE WHEN TO_DATE(ORDER_DATE) BETWEEN TO_DATE('2026-12-31') AND TO_DATE('2026-01-01') THEN 1 ELSE 0 END",
                },
            ],
            Aggregates =
            [
                new AggregateRule { Col = "c1", Fn = AggregateFn.Sum },
                new AggregateRule { Col = "c2", Fn = AggregateFn.Sum },
            ],
        }, NoParams);

        Assert.Equal(1m, Convert.ToDecimal(result.Aggregates["c1"]["sum"]));
        Assert.Equal(0m, Convert.ToDecimal(result.Aggregates["c2"]["sum"]));
    }

    [Fact]
    public async Task Date_trunc_equality_finds_the_february_orders()
    {
        var result = await _executor.Query(DateDefinition, new ReportState
        {
            Computed = [new ComputedColumn
            {
                Id = "c1",
                Expr = "CASE WHEN DATE_TRUNC('MONTH', TO_DATE(ORDER_DATE)) = TO_DATE('2026-02-01') THEN 1 ELSE 0 END",
            }],
            Aggregates = [new AggregateRule { Col = "c1", Fn = AggregateFn.Sum }],
        }, NoParams);

        // Feb 2026: acme llc (02-16), Umbrella (02-08), Tyrell Corp (02-19).
        Assert.Equal(3m, Convert.ToDecimal(result.Aggregates["c1"]["sum"]));
    }

    [Fact]
    public async Task To_string_formats_via_the_portable_tokens()
    {
        var result = await _executor.Query(DateDefinition, new ReportState
        {
            Computed = [new ComputedColumn { Id = "c1", Expr = "TO_STRING(TO_DATE(ORDER_DATE), 'MM/DD/YYYY')" }],
            Filters = [Filter("CUSTOMER = 'Globex'")],
        }, NoParams);

        Assert.Equal("07/21/2025", Assert.Single(result.Rows)["c1"]);
    }

    [Fact]
    public async Task Date_arithmetic_shifts_whole_days_across_month_ends()
    {
        var result = await _executor.Query(DateDefinition, new ReportState
        {
            Computed = [new ComputedColumn { Id = "c1", Expr = "TO_STRING(TO_DATE(ORDER_DATE) + 10)" }],
            Filters = [Filter("CUSTOMER = 'Tyrell Corp'")],
        }, NoParams);

        // 2026-02-19 + 10 crosses the February month end.
        Assert.Equal("2026-03-01", Assert.Single(result.Rows)["c1"]);
    }

    [Fact]
    public async Task Timezone_setting_is_ignored_where_no_session_timezone_exists()
    {
        // SQLite has no session timezone: a configured TimeZone is a deliberate
        // no-op, never an error (ARCHITECTURE §8).
        var def = Definition;
        def.TimeZone = "Pacific/Auckland";

        var result = await _executor.Query(def, new ReportState(), NoParams);

        Assert.Equal(10, result.TotalRows);
    }

    [Fact]
    public async Task Now_sits_inside_a_sane_window_on_every_row()
    {
        var result = await _executor.Query(DateDefinition, new ReportState
        {
            Computed = [new ComputedColumn
            {
                Id = "c1",
                Expr = "CASE WHEN NOW() BETWEEN TO_DATE('2020-01-01') AND NOW() + 1 THEN 1 ELSE 0 END",
            }],
            Aggregates = [new AggregateRule { Col = "c1", Fn = AggregateFn.Sum }],
        }, NoParams);

        Assert.Equal(10m, Convert.ToDecimal(result.Aggregates["c1"]["sum"]));
    }

    [Fact]
    public async Task Simple_case_maps_the_operand()
    {
        var result = await _executor.Query(Definition, new ReportState
        {
            Computed = [new ComputedColumn
            {
                Id = "c1",
                Expr = "CASE STATUS WHEN 'SHIPPED' THEN 'done' WHEN 'CANCELLED' THEN 'void' ELSE 'open' END",
            }],
            Filters = [Filter("CUSTOMER = 'Tyrell Corp'")],
        }, NoParams);

        Assert.Equal("void", Assert.Single(result.Rows)["c1"]);
    }

    [Fact]
    public async Task Highlights_hit_rows_and_cells_with_sql_parity()
    {
        var result = await _executor.Query(Definition, new ReportState
        {
            Sorts = [new SortRule { Col = "AMOUNT", Dir = SortDir.Desc }],
            Page = new PageRequest { Index = 1, Size = 10 },
            Highlights =
            [
                new HighlightRule { Id = "big", Scope = "row", Expr = "AMOUNT > 5000", Style = new HighlightStyle { Bg = "#fee2e2" } },
                new HighlightRule { Id = "acme", Scope = "cell", Col = "CUSTOMER", Expr = "CONTAINS(CUSTOMER, 'ACME')", Style = new HighlightStyle { Bg = "#fef3c7" } },
                new HighlightRule { Id = "noNotes", Scope = "row", Expr = "NOTES IS NULL OR NOTES = ''", Style = new HighlightStyle { Bg = "#e0f2fe" } },
            ],
        }, NoParams);

        // Amounts desc: 12000, 9000, 7500, 6000, 5000, 3000, 2000, 1500, 800, 400
        Assert.Equal([0, 1, 2, 3], result.Highlights.Where(h => h.Id == "big").Select(h => h.Row));

        var acme = result.Highlights.Where(h => h.Id == "acme").ToList();
        Assert.Equal([1, 5, 6], acme.Select(h => h.Row));            // Acme Corp ×2, acme llc (case-insensitive)
        Assert.All(acme, h => Assert.Equal("CUSTOMER", h.Col));

        // NOTES blank = 3 NULLs + 1 empty string: Globex(7500)→2, Initech(5000)→4, Hooli(1500)→7, Umbrella(800)→8
        Assert.Equal([2, 4, 7, 8], result.Highlights.Where(h => h.Id == "noNotes").Select(h => h.Row));
        Assert.Equal(["big", "acme"], result.Highlights.Where(h => h.Row == 1).Select(h => h.Id));
        Assert.All(result.Rows, row =>
            Assert.DoesNotContain(row.Keys, key => key.StartsWith("__ir_highlight_", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task Full_expression_conditions_drive_filters_and_highlights()
    {
        const string condition =
            "ROUND(AMOUNT, 0) >= 5000 AND "
            + "DATE_TRUNC('YEAR', TO_DATE(ORDER_DATE)) = TO_DATE('2026-01-01')";
        var definition = Definition;
        definition.Name = "orders-date-conditions";
        definition.Sql = "SELECT ORDER_ID, CUSTOMER, STATUS, AMOUNT, NOTES, ORDER_DATE FROM ORDERS";

        var filtered = await _executor.Query(definition, new ReportState
        {
            Filters = [new FilterRule { Expr = condition }],
        }, NoParams);
        Assert.Equal(2, filtered.TotalRows); // Stark and Tyrell

        var highlighted = await _executor.Query(definition, new ReportState
        {
            Sorts = [new SortRule { Col = "AMOUNT", Dir = SortDir.Desc }],
            Highlights =
            [
                new HighlightRule
                {
                    Id = "full", Scope = "row", Expr = condition,
                    Style = new HighlightStyle { Bg = "#fee2e2" },
                },
            ],
        }, NoParams);

        Assert.Equal([0, 3], highlighted.Highlights.Select(hit => hit.Row));
    }

    [Fact]
    public async Task GroupBy_view_returns_grouped_page_with_counts_and_values()
    {
        var result = await _executor.Query(Definition, new ReportState
        {
            View = new ViewSpec
            {
                Mode = "groupBy",
                GroupBy = ["STATUS"],
                Values = [new AggregateRule { Col = "AMOUNT", Fn = AggregateFn.Sum }],
            },
        }, NoParams);

        Assert.Equal(4, result.TotalRows);
        Assert.Equal(["STATUS", "__count", "v0"], result.Columns.Select(c => c.Name));
        Assert.Equal("sum(Amount)", result.Columns[2].Label);
        Assert.Null(result.Columns[1].FormatSource);
        Assert.Equal("AMOUNT", result.Columns[2].FormatSource);
        Assert.Equal(["CANCELLED", "NEW", "PENDING", "SHIPPED"], result.Rows.Select(r => (string)r["STATUS"]!));
        Assert.Equal([1L, 1L, 3L, 5L], result.Rows.Select(r => Convert.ToInt64(r["__count"])));
        Assert.Equal([6000m, 400m, 14800m, 26000m], result.Rows.Select(r => Convert.ToDecimal(r["v0"])));
    }

    [Fact]
    public async Task GroupBy_view_paginates_groups()
    {
        var result = await _executor.Query(Definition, new ReportState
        {
            View = new ViewSpec { Mode = "groupBy", GroupBy = ["STATUS"] },
            Page = new PageRequest { Index = 2, Size = 3 },
        }, NoParams);

        Assert.Equal(4, result.TotalRows);
        var row = Assert.Single(result.Rows);
        Assert.Equal("SHIPPED", row["STATUS"]);
    }

    [Fact]
    public async Task Pivot_view_builds_the_matrix_in_memory()
    {
        var result = await _executor.Query(Definition, new ReportState
        {
            View = new ViewSpec
            {
                Mode = "pivot",
                Rows = ["CUSTOMER"],
                Cols = ["STATUS"],
                Values = [new AggregateRule { Col = "AMOUNT", Fn = AggregateFn.Sum }],
            },
        }, NoParams);

        // 9 distinct customers; columns = CUSTOMER + 4 statuses (sorted: CANCELLED, NEW, PENDING, SHIPPED)
        Assert.Equal(9, result.TotalRows);
        Assert.Equal(5, result.Columns.Count);
        Assert.Equal(["CANCELLED", "NEW", "PENDING", "SHIPPED"], result.Columns.Skip(1).Select(c => c.Label));
        Assert.All(result.Columns.Skip(1), column => Assert.Equal("AMOUNT", column.FormatSource));

        var acme = result.Rows.Single(r => (string?)r["CUSTOMER"] == "Acme Corp");
        Assert.Equal(12000m, Convert.ToDecimal(acme["p3_0"]));       // SHIPPED: 9000 + 3000
        Assert.False(acme.TryGetValue("p2_0", out var pending) && pending is not null);   // no PENDING cell
    }

    [Fact]
    public async Task Pivot_with_no_values_defaults_to_counts()
    {
        var result = await _executor.Query(Definition, new ReportState
        {
            View = new ViewSpec { Mode = "pivot", Rows = ["CUSTOMER"], Cols = ["STATUS"] },
        }, NoParams);

        var acme = result.Rows.Single(r => (string?)r["CUSTOMER"] == "Acme Corp");
        Assert.Equal(2L, Convert.ToInt64(acme["p3_0"]));             // two SHIPPED orders
        Assert.All(result.Columns.Skip(1), column => Assert.Null(column.FormatSource));

        var explicitCount = await _executor.Query(Definition, new ReportState
        {
            View = new ViewSpec
            {
                Mode = "pivot", Rows = ["CUSTOMER"], Cols = ["STATUS"],
                Values = [new AggregateRule { Col = "AMOUNT", Fn = AggregateFn.Count }],
            },
        }, NoParams);
        Assert.All(explicitCount.Columns.Skip(1), column => Assert.Null(column.FormatSource));
    }

    [Fact]
    public async Task Pivot_column_cap_is_a_validation_error()
    {
        var def = Definition;
        def.MaxPivotColumns = 2;

        var ex = await Assert.ThrowsAsync<ReportValidationException>(() =>
            _executor.Query(def, new ReportState
            {
                View = new ViewSpec { Mode = "pivot", Rows = ["STATUS"], Cols = ["CUSTOMER"] },
            }, NoParams));

        Assert.Contains(ex.Errors, e => e.Path == "view.cols" && e.Message.Contains("max 2"));
    }

    [Fact]
    public async Task Export_grid_caps_rows_and_flags_truncation()
    {
        var def = Definition;
        def.MaxRows = 3;

        var export = await _executor.Export(def, new ReportState
        {
            Sorts = [new SortRule { Col = "AMOUNT", Dir = SortDir.Desc }],
        }, NoParams);

        Assert.True(export.Truncated);
        Assert.Equal(3, export.Rows.Count);
        Assert.Equal(12000m, Convert.ToDecimal(export.Rows[0]["AMOUNT"]));
    }

    [Fact]
    public async Task Export_groupBy_view_exports_all_groups_untruncated()
    {
        var export = await _executor.Export(Definition, new ReportState
        {
            View = new ViewSpec
            {
                Mode = "groupBy",
                GroupBy = ["STATUS"],
                Values = [new AggregateRule { Col = "AMOUNT", Fn = AggregateFn.Sum }],
            },
        }, NoParams);

        Assert.False(export.Truncated);
        Assert.Equal(4, export.Rows.Count);
        Assert.Equal(["STATUS", "__count", "v0"], export.Columns.Select(c => c.Name));
    }

    [Fact]
    public async Task Chart_view_aggregates_the_whole_filtered_set()
    {
        var result = await _executor.Query(Definition, new ReportState
        {
            Filters = [Filter("STATUS <> 'NEW'")],
            View = new ViewSpec
            {
                Mode = "chart", Type = "bar", Label = "STATUS", Value = "AMOUNT", Fn = AggregateFn.Sum,
                Sort = new ChartSortSpec { By = "value", Dir = SortDir.Desc },
            },
        }, NoParams);

        Assert.Equal(["STATUS", "v0"], result.Columns.Select(c => c.Name));
        Assert.Equal("sum(Amount)", result.Columns[1].Label);
        Assert.Equal("number", result.Columns[1].Type);
        Assert.Equal("AMOUNT", result.Columns[1].FormatSource);
        Assert.Equal(3, result.TotalRows);
        Assert.Equal(["SHIPPED", "PENDING", "CANCELLED"], result.Rows.Select(r => (string)r["STATUS"]!));
        Assert.Equal([26000m, 14800m, 6000m], result.Rows.Select(r => Convert.ToDecimal(r["v0"])));
        Assert.All(result.Rows, r => Assert.Equal(2, r.Count));      // the grouped __count never leaks
    }

    [Fact]
    public async Task Chart_view_supports_median_metrics()
    {
        var result = await _executor.Query(Definition, new ReportState
        {
            View = new ViewSpec
            {
                Mode = "chart", Type = "bar", Label = "STATUS", Value = "AMOUNT", Fn = AggregateFn.Median,
            },
        }, NoParams);

        Assert.Equal(["STATUS", "v0"], result.Columns.Select(c => c.Name));
        Assert.Equal("median(Amount)", result.Columns[1].Label);
        Assert.Equal(["CANCELLED", "NEW", "PENDING", "SHIPPED"], result.Rows.Select(r => (string)r["STATUS"]!));
        Assert.Equal([6000m, 400m, 2000m, 5000m], result.Rows.Select(r => Convert.ToDecimal(r["v0"])));
    }

    [Fact]
    public async Task Chart_count_alone_counts_rows_per_label()
    {
        var result = await _executor.Query(Definition, new ReportState
        {
            View = new ViewSpec { Mode = "chart", Type = "pie", Label = "STATUS", Fn = AggregateFn.Count },
        }, NoParams);

        Assert.Equal(["STATUS", "__count"], result.Columns.Select(c => c.Name));
        Assert.Equal("Count", result.Columns[1].Label);
        Assert.Equal(["CANCELLED", "NEW", "PENDING", "SHIPPED"], result.Rows.Select(r => (string)r["STATUS"]!));
        Assert.Equal([1L, 1L, 3L, 5L], result.Rows.Select(r => Convert.ToInt64(r["__count"])));
    }

    [Fact]
    public async Task Chart_aggregate_metric_key_does_not_overwrite_a_v0_label()
    {
        var def = Definition;
        def.Name = "chart-v0-label";
        def.Sql = "SELECT STATUS AS v0, AMOUNT FROM ORDERS";

        var result = await _executor.Query(def, new ReportState
        {
            View = new ViewSpec
            {
                Mode = "chart", Type = "bar", Label = "v0", Value = "AMOUNT", Fn = AggregateFn.Sum,
            },
        }, NoParams);

        Assert.Equal(["v0", "v0_metric"], result.Columns.Select(c => c.Name));
        Assert.Equal("AMOUNT", result.Columns[1].FormatSource);
        Assert.Equal(["CANCELLED", "NEW", "PENDING", "SHIPPED"], result.Rows.Select(r => (string)r["v0"]!));
        Assert.Equal([6000m, 400m, 14800m, 26000m], result.Rows.Select(r => Convert.ToDecimal(r["v0_metric"])));
    }

    [Fact]
    public async Task Chart_count_metric_key_does_not_overwrite_an___count_label()
    {
        var def = Definition;
        def.Name = "chart-count-label";
        def.Sql = "SELECT STATUS AS __count FROM ORDERS";

        var result = await _executor.Query(def, new ReportState
        {
            View = new ViewSpec { Mode = "chart", Type = "pie", Label = "__count", Fn = AggregateFn.Count },
        }, NoParams);

        Assert.Equal(["__count", "__count_metric"], result.Columns.Select(c => c.Name));
        Assert.Equal(["CANCELLED", "NEW", "PENDING", "SHIPPED"], result.Rows.Select(r => (string)r["__count"]!));
        Assert.Equal([1L, 1L, 3L, 5L], result.Rows.Select(r => Convert.ToInt64(r["__count_metric"])));
    }

    [Fact]
    public async Task Pie_chart_rejects_negative_metrics()
    {
        var ex = await Assert.ThrowsAsync<ReportValidationException>(() =>
            _executor.Query(Definition, new ReportState
            {
                Computed = [new ComputedColumn { Id = "c1", Label = "Loss", Expr = "0 - AMOUNT" }],
                View = new ViewSpec
                {
                    Mode = "chart", Type = "pie", Label = "STATUS", Value = "c1", Fn = AggregateFn.Sum,
                },
            }, NoParams));

        Assert.Contains(ex.Errors, error =>
            error.Path == "view.value" && error.Message.Contains("non-negative", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Chart_without_fn_plots_one_point_per_filtered_row()
    {
        var result = await _executor.Query(DateDefinition, new ReportState
        {
            Filters = [Filter("AMOUNT >= 5000")],
            View = new ViewSpec { Mode = "chart", Type = "line", Label = "ORDER_DATE", Value = "AMOUNT" },
        }, NoParams);

        Assert.Equal(5, result.TotalRows);
        Assert.Equal(["ORDER_DATE", "AMOUNT"], result.Columns.Select(c => c.Name));
        Assert.Equal("AMOUNT", result.Columns[1].FormatSource);
        Assert.Equal(
            [5000m, 7500m, 9000m, 6000m, 12000m],                    // label (date-text) ascending
            result.Rows.Select(r => Convert.ToDecimal(r["AMOUNT"])));
    }

    [Fact]
    public async Task Raw_chart_uses_distinct_keys_when_label_and_value_are_the_same_column()
    {
        var result = await _executor.Query(Definition, new ReportState
        {
            View = new ViewSpec { Mode = "chart", Type = "line", Label = "AMOUNT", Value = "AMOUNT" },
        }, NoParams);

        Assert.Equal(["AMOUNT", "AMOUNT_metric"], result.Columns.Select(c => c.Name));
        Assert.Equal("AMOUNT", result.Columns[1].FormatSource);
        Assert.All(result.Rows, row =>
            Assert.Equal(Convert.ToDecimal(row["AMOUNT"]), Convert.ToDecimal(row["AMOUNT_metric"])));
    }

    [Fact]
    public async Task Chart_aggregates_computed_value_by_computed_label()
    {
        var result = await _executor.Query(Definition, new ReportState
        {
            Computed =
            [
                new ComputedColumn { Id = "c1", Label = "Size", Expr = "CASE WHEN AMOUNT >= 6000 THEN 'BIG' ELSE 'SMALL' END" },
                new ComputedColumn { Id = "c2", Label = "Doubled", Expr = "AMOUNT * 2" },
            ],
            View = new ViewSpec { Mode = "chart", Type = "bar", Label = "c1", Value = "c2", Fn = AggregateFn.Sum },
        }, NoParams);

        Assert.Equal("sum(Doubled)", result.Columns[1].Label);
        Assert.Equal("c2", result.Columns[1].FormatSource);
        Assert.Equal(["BIG", "SMALL"], result.Rows.Select(r => (string)r["c1"]!));
        Assert.Equal([69000m, 25400m], result.Rows.Select(r => Convert.ToDecimal(r["v0"])));
    }

    [Fact]
    public async Task Chart_point_limit_is_a_precise_validation_error()
    {
        var def = Definition;
        def.MaxChartPoints = 3;

        var ex = await Assert.ThrowsAsync<ReportValidationException>(() =>
            _executor.Query(def, new ReportState
            {
                View = new ViewSpec { Mode = "chart", Type = "pie", Label = "CUSTOMER", Fn = AggregateFn.Count },
            }, NoParams));                                           // 9 distinct customers > 3

        Assert.Contains(ex.Errors, e => e.Path == "view" && e.Message.Contains("3 points"));
    }

    [Fact]
    public async Task Chart_ignores_grid_sorts_with_a_notice()
    {
        var result = await _executor.Query(Definition, new ReportState
        {
            Sorts = [new SortRule { Col = "AMOUNT", Dir = SortDir.Desc }],
            View = new ViewSpec { Mode = "chart", Type = "bar", Label = "STATUS", Fn = AggregateFn.Count },
        }, NoParams);

        Assert.Contains(result.Ignored, i => i.Kind == "view" && i.Detail.Contains("chart sort"));
        Assert.Equal(
            ["CANCELLED", "NEW", "PENDING", "SHIPPED"],              // chart's own label sort, not the grid sort
            result.Rows.Select(r => (string)r["STATUS"]!));
    }

    [Fact]
    public async Task Export_chart_view_exports_the_charted_points()
    {
        var export = await _executor.Export(Definition, new ReportState
        {
            View = new ViewSpec
            {
                Mode = "chart", Type = "bar", Label = "STATUS", Value = "AMOUNT", Fn = AggregateFn.Sum,
            },
        }, NoParams);

        Assert.False(export.Truncated);
        Assert.Equal(["STATUS", "v0"], export.Columns.Select(c => c.Name));
        Assert.Equal(4, export.Rows.Count);
    }

    [Fact]
    public async Task Highlight_on_computed_column_cell()
    {
        var result = await _executor.Query(Definition, new ReportState
        {
            Computed = [new ComputedColumn { Id = "c1", Expr = "AMOUNT * 2" }],
            Sorts = [new SortRule { Col = "AMOUNT", Dir = SortDir.Desc }],
            Highlights =
            [
                new HighlightRule { Id = "h1", Scope = "cell", Col = "c1", Expr = "c1 > 20000", Style = new HighlightStyle { Bg = "#fef3c7" } },
            ],
        }, NoParams);

        var hit = Assert.Single(result.Highlights);
        Assert.Equal((0, "h1", "c1"), (hit.Row, hit.Id, hit.Col));   // only Stark: 24000
    }
}

public sealed class SqliteE2EFixture : IReportConnectionFactory, IDisposable
{
    private const string ConnectionString = "Data Source=ir-e2e;Mode=Memory;Cache=Shared";
    private readonly SqliteConnection _keepAlive;

    public SqliteE2EFixture()
    {
        _keepAlive = new SqliteConnection(ConnectionString);
        _keepAlive.Open();

        using var cmd = _keepAlive.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE ORDERS (
                ORDER_ID INTEGER PRIMARY KEY,
                CUSTOMER TEXT NOT NULL,
                STATUS   TEXT NOT NULL,
                AMOUNT   NUMERIC NOT NULL,
                NOTES    TEXT NULL,
                ORDER_DATE TEXT NOT NULL
            );
            INSERT INTO ORDERS (CUSTOMER, STATUS, AMOUNT, NOTES, ORDER_DATE) VALUES
                ('Acme Corp',   'SHIPPED',   9000, 'rush',       '2025-11-03'),
                ('Globex',      'SHIPPED',   7500, NULL,         '2025-07-21'),
                ('Initech',     'SHIPPED',   5000, '',           '2025-03-09'),
                ('Acme Corp',   'SHIPPED',   3000, 'fragile',    '2025-12-30'),
                ('Hooli',       'SHIPPED',   1500, NULL,         '2025-05-14'),
                ('acme llc',    'PENDING',   2000, 'call first', '2026-02-16'),
                ('Umbrella',    'PENDING',    800, NULL,         '2026-02-08'),
                ('Stark Ind',   'PENDING',  12000, 'insured',    '2026-06-27'),
                ('Wayne Ent',   'NEW',        400, 'standard',   '2026-04-01'),
                ('Tyrell Corp', 'CANCELLED', 6000, 'refunded',   '2026-02-19');
            """;
        cmd.ExecuteNonQuery();
    }

    public DbConnection CreateConnection(string name) => new SqliteConnection(ConnectionString);

    public void Dispose() => _keepAlive.Dispose();
}
