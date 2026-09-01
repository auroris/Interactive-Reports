using System.Data.Common;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Schema;
using InteractiveReport.Core.Validation;
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

        var result = await _executor.Query(def, Doc(source: new StageLayer
        {
            Labels = new() { ["CUSTOMER"] = "Client", ["GHOST"] = "Opaque" },
        }), NoParams);

        Assert.Equal("Customer", result.AvailableColumns.Single(c => c.Name == "CUSTOMER").Label);
        Assert.Equal(
            "Customer",
            result.Document!.Tables![result.Document.ActiveTable!].Schema!
                .Single(column => column.Name == "CUSTOMER").Label);
        Assert.Empty(result.Ignored);
    }

    [Fact]
    public async Task Grid_rows_include_hidden_renderer_sources_but_metadata_and_exports_do_not()
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
                    TextColumn = "CUSTOMER",
                },
            },
        });

        var result = await _executor.Query(Definition, state, NoParams);
        Assert.Equal(["CUSTOMER"], result.Columns.Select(c => c.Name));
        Assert.All(result.Rows, row => Assert.Equal(["CUSTOMER", "NOTES"], row.Keys));

        var export = await _executor.Download(Definition, state, NoParams);
        Assert.Equal(["CUSTOMER"], export.Columns.Select(c => c.Name));
        Assert.All(export.Rows, row => Assert.Equal(["CUSTOMER"], row.Keys));
    }

    [Fact]
    public async Task Hidden_computed_column_can_use_hidden_base_data_and_feed_a_link_renderer()
    {
        var state = Doc(
            source: new StageLayer
            {
                Computed =
                [
                    new ComputedColumn
                    {
                        Id = "ir1",
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
                        UrlColumn = "ir1",
                        TextColumn = "CUSTOMER",
                    },
                },
            },
            page: new PageRequest { Index = 1, Size = 1 });

        var result = await _executor.Query(Definition, state, NoParams);

        Assert.Equal(["CUSTOMER"], result.Columns.Select(c => c.Name));
        Assert.True(result.AvailableColumns.Single(c => c.Name == "ir1").Computed);
        var row = Assert.Single(result.Rows);
        Assert.Equal(["CUSTOMER", "ir1"], row.Keys);
        Assert.Equal("/orders/1", row["ir1"]);
        Assert.DoesNotContain("ORDER_ID", row.Keys);

        var export = await _executor.Download(Definition, state, NoParams);
        Assert.Equal(["CUSTOMER"], export.Columns.Select(c => c.Name));
        Assert.Equal("Acme Corp", export.Rows[0]["CUSTOMER"]);
        Assert.All(export.Rows, exported => Assert.DoesNotContain("ir1", exported.Keys));
    }

    /// An action-labeled column: CASE labels some rows and leaves the rest NULL
    /// (the blank-label-means-no-button convention).
    private ReportDefinition ActionDefinition => new()
    {
        Name = "orders-actions-e2e",
        Connection = "E2E",
        Dialect = ReportDialect.Sqlite,
        Sql = "SELECT ORDER_ID, CUSTOMER, STATUS, "
            + "CASE WHEN STATUS = 'PENDING' THEN 'Approve' END AS ACTION_APPROVE FROM ORDERS",
    };

    [Fact]
    public async Task Action_rows_carry_the_hidden_key_and_export_the_raw_label()
    {
        var state = Doc(source: new StageLayer
        {
            Columns = ["CUSTOMER", "ACTION_APPROVE"],
            Sorts = [new SortRule { Col = "ORDER_ID" }],
            Formats = new()
            {
                ["ACTION_APPROVE"] = new ColumnFormat
                {
                    DisplayAs = "action",
                    Command = "approve",
                    KeyColumn = "ORDER_ID",
                },
            },
        });

        var result = await _executor.Query(ActionDefinition, state, NoParams);
        Assert.Equal(["CUSTOMER", "ACTION_APPROVE"], result.Columns.Select(c => c.Name));
        Assert.All(result.Rows, row => Assert.Equal(["CUSTOMER", "ACTION_APPROVE", "ORDER_ID"], row.Keys));
        Assert.Contains(result.Rows, row => Equals(row["ACTION_APPROVE"], "Approve"));

        var export = await _executor.Download(ActionDefinition, state, NoParams);
        Assert.Equal(["CUSTOMER", "ACTION_APPROVE"], export.Columns.Select(c => c.Name));
        Assert.All(export.Rows, row => Assert.DoesNotContain("ORDER_ID", row.Keys));
        // Labels export as their raw value — never an HTML fragment; NULL stays empty.
        Assert.Contains(export.Rows, row => Equals(row["ACTION_APPROVE"], "Approve"));
        Assert.Contains(export.Rows, row => row["ACTION_APPROVE"] is null);
    }

    [Fact]
    public async Task Sqlite_discovery_types_expression_columns_as_other()
    {
        // Microsoft.Data.Sqlite has no decltype for expression columns on the
        // zero-row probe, so CASE/literal columns discover as Other. The admin
        // listing's SCOPE/ACTION_* columns depend on this staying contained
        // (renderers key off the format, never the kind) — pin it so a provider
        // change is loud.
        var schema = await _executor.GetSchema(ActionDefinition, NoParams);

        Assert.Equal(ColumnKind.Other, schema.Lookup["ACTION_APPROVE"].Kind);
        Assert.Equal(ColumnKind.Text, schema.Lookup["CUSTOMER"].Kind);
    }

    [Fact]
    public async Task Grid_download_renderers_emit_file_appropriate_text_and_urls()
    {
        var state = Doc(source: new StageLayer
        {
            Computed =
            [
                new ComputedColumn { Id = "ir1", Expr = "'/orders/' || ORDER_ID || '?a=1&b=2'" },
                new ComputedColumn { Id = "ir2", Expr = "'/images/' || ORDER_ID || '.png?a=1&b=2'" },
                new ComputedColumn { Id = "ir3", Expr = "'<Order & Customer>'" },
                new ComputedColumn { Id = "ir4", Expr = "'javascript:alert(1)'" },
            ],
            Columns = ["CUSTOMER", "NOTES", "STATUS"],
            Filters = [Filter("ORDER_ID = 1")],
            Formats = new()
            {
                ["CUSTOMER"] = new ColumnFormat
                {
                    DisplayAs = "link",
                    UrlColumn = "ir1",
                    TextColumn = "ir3",
                },
                ["NOTES"] = new ColumnFormat
                {
                    DisplayAs = "image",
                    UrlColumn = "ir2",
                },
                ["STATUS"] = new ColumnFormat
                {
                    DisplayAs = "link",
                    UrlColumn = "ir4",
                    TextColumn = "ir3",
                },
            },
        });

        var export = await _executor.Download(Definition, state, NoParams);
        var row = Assert.Single(export.Rows);

        Assert.Equal("<Order & Customer>", row["CUSTOMER"]);
        Assert.Equal("/images/1.png?a=1&b=2", row["NOTES"]);
        Assert.Equal("<Order & Customer>", row["STATUS"]);
        Assert.Equal(["CUSTOMER", "NOTES", "STATUS"], row.Keys);
    }

    [Fact]
    public async Task Grid_export_link_text_uses_the_source_columns_mask()
    {
        var export = await _executor.Download(Definition, Doc(source: new StageLayer
        {
            Computed = [new ComputedColumn { Id = "ir1", Expr = "'/orders/' || ORDER_ID" }],
            Columns = ["CUSTOMER"],
            Filters = [Filter("ORDER_ID = 1")],
            Formats = new()
            {
                ["CUSTOMER"] = new ColumnFormat
                {
                    DisplayAs = "link",
                    UrlColumn = "ir1",
                    TextColumn = "AMOUNT",
                },
                ["AMOUNT"] = new ColumnFormat { Mask = "currency:USD" },
            },
        }), NoParams);

        Assert.Equal("$9,000.00", Convert.ToString(Assert.Single(export.Rows)["CUSTOMER"]));
    }

    [Fact]
    public async Task Export_applies_the_documents_labels_because_it_renders_what_the_user_sees()
    {
        var def = Definition;
        def.ColumnLabels = new() { ["ORDER_ID"] = "Order #" };

        // Document metadata overlays the definition mapping. A mapping is cleared
        // only by an explicit empty labels composable.
        var grid = await _executor.Download(def, Doc(source: new StageLayer
        {
            Columns = ["ORDER_ID", "AMOUNT"],
            Labels = new() { ["AMOUNT"] = "Order Total" },
        }), NoParams);

        Assert.Equal(["ORDER_ID", "AMOUNT"], grid.Columns.Select(c => c.Name));
        Assert.Equal(["Order #", "Order Total"], grid.Columns.Select(c => c.Label));

        // A document with no labels of its own falls back to the configured mapping,
        // and synthetic aggregate columns rebuild their labels from the display name.
        var grouped = await _executor.Download(def, Doc(tail:
        [
            Group(by: ["STATUS"], values: [Metric("ir1", "ORDER_ID", AggregateFn.Max)]),
        ]), NoParams);

        Assert.Equal(["Status", "Count", "max(Order #)"], grouped.Columns.Select(c => c.Label));

        var inherited = await _executor.Download(def, Doc(
            source: new StageLayer { Labels = new() { ["AMOUNT"] = "Revenue" } },
            tail:
            [
                Group(by: ["STATUS"], values: [Metric("ir1", "AMOUNT", AggregateFn.Sum)]),
            ]), NoParams);

        Assert.Equal(["Status", "Count", "sum(Revenue)"], inherited.Columns.Select(c => c.Label));
    }

    [Fact]
    public async Task Filter_sort_page_end_to_end()
    {
        var result = await _executor.Query(Definition, Doc(
            source: new StageLayer
            {
                Filters = [Filter("STATUS = 'SHIPPED'")],
                Sorts = [new SortRule { Col = "AMOUNT", Dir = SortDir.Desc }],
            },
            page: new PageRequest { Index = 1, Size = 3 }), NoParams);

        Assert.Equal(5, result.TotalRows);                       // 5 SHIPPED rows seeded
        Assert.Equal(3, result.Rows.Count);                      // but only a page of 3
        var amounts = result.Rows.Select(r => Convert.ToDecimal(r["AMOUNT"])).ToArray();
        Assert.Equal([9000m, 7500m, 5000m], amounts);            // descending, from the top
    }

    [Fact]
    public async Task Second_page_continues_the_ordering()
    {
        var state = Doc(
            source: new StageLayer
            {
                Filters = [Filter("STATUS = 'SHIPPED'")],
                Sorts = [new SortRule { Col = "AMOUNT", Dir = SortDir.Desc }],
            },
            page: new PageRequest { Index = 2, Size = 3 });

        var result = await _executor.Query(Definition, state, NoParams);

        Assert.Equal(5, result.TotalRows);
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal([3000m, 1500m], result.Rows.Select(r => Convert.ToDecimal(r["AMOUNT"])));
    }

    [Fact]
    public async Task Explicit_null_placement_controls_query_and_export_order()
    {
        var nullsLast = Doc(source: new StageLayer
        {
            Sorts =
            [
                new SortRule { Col = "NOTES", Nulls = NullPlacement.Last },
                new SortRule { Col = "ORDER_ID" },
            ],
        });

        var query = await _executor.Query(Definition, nullsLast, NoParams);
        Assert.All(query.Rows.Take(7), row => Assert.NotNull(row["NOTES"]));
        Assert.All(query.Rows.TakeLast(3), row => Assert.Null(row["NOTES"]));

        var export = await _executor.Download(Definition, nullsLast, NoParams);
        Assert.All(export.Rows.Take(7), row => Assert.NotNull(row["NOTES"]));
        Assert.All(export.Rows.TakeLast(3), row => Assert.Null(row["NOTES"]));

        var nullsFirst = await _executor.Query(Definition, Doc(source: new StageLayer
        {
            Sorts =
            [
                new SortRule { Col = "NOTES", Dir = SortDir.Desc, Nulls = NullPlacement.First },
                new SortRule { Col = "ORDER_ID" },
            ],
        }), NoParams);
        Assert.All(nullsFirst.Rows.Take(3), row => Assert.Null(row["NOTES"]));
        Assert.All(nullsFirst.Rows.Skip(3), row => Assert.NotNull(row["NOTES"]));
    }

    [Fact]
    public async Task Positive_max_rows_caps_all_queries_and_exports()
    {
        var def = Definition;
        def.MaxRows = 3;
        def.MaxPageSize = 3;
        def.DefaultPageSize = 3;
        var state = Doc(
            source: new StageLayer { Sorts = [new SortRule { Col = "ORDER_ID" }] },
            page: new PageRequest { Index = 8, Size = 0 });

        var query = await _executor.Query(def, state, NoParams);

        Assert.Equal(10, query.TotalRows);
        Assert.Equal(3, query.Rows.Count);
        Assert.Equal(1, query.Page.Index);
        Assert.Equal(0, query.Page.Size);

        var export = await _executor.Download(def, state, NoParams);
        Assert.True(export.Truncated);
        Assert.Equal(3, export.Rows.Count);
    }

    [Fact]
    public async Task Positive_max_rows_caps_all_group_queries()
    {
        var def = Definition;
        def.MaxRows = 2;
        def.MaxPageSize = 2;
        def.DefaultPageSize = 2;
        var state = Doc(
            tail: [Group(by: ["STATUS"])],
            page: new PageRequest { Index = 1, Size = 0 });

        var query = await _executor.Query(def, state, NoParams);

        Assert.True(query.TotalRows > 2);
        Assert.Equal(2, query.Rows.Count);
        Assert.Equal(0, query.Page.Size);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Nonpositive_max_rows_leaves_all_queries_and_exports_unlimited(int maxRows)
    {
        var def = Definition;
        def.MaxRows = maxRows;
        var state = Doc(
            source: new StageLayer { Sorts = [new SortRule { Col = "ORDER_ID" }] },
            page: new PageRequest { Index = 1, Size = 0 });

        var query = await _executor.Query(def, state, NoParams);
        var export = await _executor.Download(def, state, NoParams);

        Assert.Equal(10, query.Rows.Count);
        Assert.Equal(10, export.Rows.Count);
        Assert.False(export.Truncated);
    }

    [Fact]
    public async Task Search_is_case_insensitive_across_text_columns()
    {
        var result = await _executor.Query(Definition, Doc(search: "ACME"), NoParams);

        // 'Acme Corp' ×2 and 'acme llc' ×1
        Assert.Equal(3, result.TotalRows);
    }

    [Fact]
    public async Task Lov_authored_text_matches_exactly_or_by_asterisk_wildcard_without_case()
    {
        var exact = await _executor.Query(Definition, Doc(source: new StageLayer
        {
            Filters = [Filter("LOWER(CUSTOMER) = LOWER('ACME CORP')")],
        }), NoParams);
        var wildcard = await _executor.Query(Definition, Doc(source: new StageLayer
        {
            Filters = [Filter("WILDCARD_MATCH(CUSTOMER, 'a*corp')")],
        }), NoParams);

        Assert.Equal(2, exact.TotalRows);
        Assert.Equal(2, wildcard.TotalRows);
    }

    [Fact]
    public async Task Lov_uses_the_submitted_active_relation_and_searches_before_returning_values()
    {
        var document = Doc(source: new StageLayer
        {
            Filters = [Filter("STATUS = 'PENDING'")],
        });

        var result = await _executor.Lov(Definition, new ReportLovRequest
        {
            Document = document,
            Table = "source",
            Column = "CUSTOMER",
            Search = "AC",
        }, NoParams);

        Assert.Equal("source", result.Table);
        Assert.Equal("CUSTOMER", result.Column);
        Assert.Equal("text", result.Type);
        Assert.Equal(["acme llc"], result.Items);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task Lov_returns_only_the_first_50_distinct_items()
    {
        var definition = new ReportDefinition
        {
            Name = "lov-limit",
            Connection = "E2E",
            Dialect = ReportDialect.Sqlite,
            Sql = "WITH RECURSIVE values_source(value) AS "
                + "(SELECT 1 UNION ALL SELECT value + 1 FROM values_source WHERE value < 60) "
                + "SELECT value AS VALUE FROM values_source",
        };

        var result = await _executor.Lov(definition, new ReportLovRequest
        {
            Document = new ReportState(),
            Table = "definition",
            Column = "VALUE",
        }, NoParams);

        Assert.Equal(ReportExecutor.MaxLovItems, result.Items.Count);
        Assert.Equal(1L, result.Items[0]);
        Assert.Equal(50L, result.Items[^1]);
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task Search_shapes_every_complete_set_but_export_ignores_request_paging()
    {
        var state = Doc(
            source: new StageLayer
            {
                Sorts = [new SortRule { Col = "ORDER_ID" }],
                Breaks = ["CUSTOMER"],
                Aggregates = [new AggregateRule { Col = "AMOUNT", Fn = AggregateFn.Sum }],
                Highlights =
                [
                    new HighlightRule
                    {
                        Id = "positive",
                        Expr = "AMOUNT > 0",
                        Style = new HighlightStyle { Bg = "yellow" },
                    },
                ],
            },
            search: "ACME",
            page: new PageRequest { Index = 2, Size = 1 });

        var result = await _executor.Query(Definition, state, NoParams);
        var export = await _executor.Download(Definition, state, NoParams);

        Assert.Equal(3, result.TotalRows);
        Assert.Single(result.Rows);
        Assert.Equal(14000m, Convert.ToDecimal(result.Aggregates["AMOUNT"]["sum"]));
        Assert.Equal(2, result.BreakTotals.Count);
        Assert.Single(result.Highlights);
        Assert.Equal(0, result.Highlights[0].Row);
        Assert.Equal(3, export.Rows.Count);
        Assert.All(export.Rows, row =>
            Assert.Contains("acme", Convert.ToString(row["CUSTOMER"])!, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Blank_matches_null_and_empty_string_on_sqlite()
    {
        var result = await _executor.Query(Definition, Doc(source: new StageLayer
        {
            Filters = [Filter("NOTES IS NULL OR NOTES = ''")],
        }), NoParams);

        Assert.Equal(4, result.TotalRows);                       // 3 NULL + 1 ''
    }

    [Fact]
    public async Task Disabled_condition_with_removed_column_remains_loadable()
    {
        var result = await _executor.Query(Definition, Doc(source: new StageLayer
        {
            Filters = [new FilterRule { Enabled = false, Expr = "REMOVED_COLUMN = 'x'" }],
        }), NoParams);

        Assert.Equal(10, result.TotalRows);
        Assert.Empty(result.Ignored);
    }

    [Fact]
    public async Task Between_and_in_compose_together()
    {
        var result = await _executor.Query(Definition, Doc(source: new StageLayer
        {
            Filters =
            [
                Filter("AMOUNT BETWEEN 1000 AND 8000"),
                Filter("IN_LIST(STATUS, 'SHIPPED', 'PENDING')"),
            ],
        }), NoParams);

        // SHIPPED: 7500, 5000, 3000, 1500 in range; PENDING: 2000 in range
        Assert.Equal(5, result.TotalRows);
    }

    [Fact]
    public async Task Aggregates_cover_the_whole_filtered_set_not_the_page()
    {
        var result = await _executor.Query(Definition, Doc(
            source: new StageLayer
            {
                Filters = [Filter("STATUS = 'SHIPPED'")],
                Aggregates =
                [
                    new AggregateRule { Col = "AMOUNT", Fn = AggregateFn.Sum },
                    new AggregateRule { Col = "AMOUNT", Fn = AggregateFn.Avg },
                    new AggregateRule { Col = "AMOUNT", Fn = AggregateFn.Median },
                    new AggregateRule { Col = "AMOUNT", Fn = AggregateFn.Min },
                    new AggregateRule { Col = "AMOUNT", Fn = AggregateFn.Max },
                ],
            },
            page: new PageRequest { Index = 1, Size = 2 }), NoParams);

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
        var result = await _executor.Query(Definition, Doc(source: new StageLayer
        {
            Computed =
            [
                new ComputedColumn
                {
                    Id = "ir1",
                    Expr = "CASE WHEN ORDER_ID = 1 THEN NULL ELSE AMOUNT END",
                },
            ],
            Filters = [Filter("ORDER_ID <= 5")],
            Aggregates = [new AggregateRule { Col = "ir1", Fn = AggregateFn.Median }],
        }), NoParams);

        Assert.Equal(4000m, Convert.ToDecimal(result.Aggregates["ir1"]["median"]));
    }

    [Fact]
    public async Task CountDistinct_counts_values_not_rows()
    {
        var result = await _executor.Query(Definition, Doc(source: new StageLayer
        {
            Aggregates = [new AggregateRule { Col = "CUSTOMER", Fn = AggregateFn.CountDistinct }],
        }), NoParams);

        // 10 rows, but 'Acme Corp' appears twice → 9 distinct
        Assert.Equal(9L, Convert.ToInt64(result.Aggregates["CUSTOMER"]["countDistinct"]));
    }

    [Fact]
    public async Task Break_totals_group_and_order_like_the_page()
    {
        var result = await _executor.Query(Definition, Doc(source: new StageLayer
        {
            Breaks = ["STATUS"],
            Aggregates =
            [
                new AggregateRule { Col = "AMOUNT", Fn = AggregateFn.Sum },
                new AggregateRule { Col = "AMOUNT", Fn = AggregateFn.Median },
            ],
        }), NoParams);

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
        var continuing = await _executor.Query(Definition, Doc(
            source: new StageLayer { Breaks = ["STATUS"] },
            page: new PageRequest { Index = 2, Size = 2 }), NoParams);

        Assert.Equal(2, continuing.Rows.Count);
        Assert.All(continuing.Rows, row => Assert.Equal("PENDING", row["STATUS"]));
        Assert.True(continuing.BreakContinues);

        var final = await _executor.Query(Definition, Doc(
            source: new StageLayer { Breaks = ["STATUS"] },
            page: new PageRequest { Index = 5, Size = 2 }), NoParams);

        Assert.Equal(2, final.Rows.Count);
        Assert.All(final.Rows, row => Assert.Equal("SHIPPED", row["STATUS"]));
        Assert.False(final.BreakContinues);
    }

    [Fact]
    public async Task User_sort_direction_on_break_column_reverses_groups()
    {
        var result = await _executor.Query(Definition, Doc(source: new StageLayer
        {
            Breaks = ["STATUS"],
            Sorts = [new SortRule { Col = "STATUS", Dir = SortDir.Desc }],
        }), NoParams);

        Assert.Equal("SHIPPED", result.BreakTotals[0].Key["STATUS"]);
        Assert.Equal("SHIPPED", result.Rows[0]["STATUS"]);
    }

    [Fact]
    public async Task Aggregates_over_empty_filtered_set_are_null()
    {
        var result = await _executor.Query(Definition, Doc(source: new StageLayer
        {
            Filters = [Filter("STATUS = 'NO_SUCH_STATUS'")],
            Aggregates = [new AggregateRule { Col = "AMOUNT", Fn = AggregateFn.Sum }],
        }), NoParams);

        Assert.Equal(0, result.TotalRows);
        Assert.Null(result.Aggregates["AMOUNT"]["sum"]);
    }

    [Fact]
    public async Task Computed_column_filters_sorts_and_returns_values()
    {
        var result = await _executor.Query(Definition, Doc(source: new StageLayer
        {
            Computed = [new ComputedColumn { Id = "ir1", Label = "Double", Expr = "ROUND(AMOUNT * 2, 0)" }],
            Columns = ["CUSTOMER", "ir1"],
            Filters = [Filter("ir1 >= 10000")],
            Sorts = [new SortRule { Col = "ir1", Dir = SortDir.Desc }],
        }), NoParams);

        // Doubled amounts ≥ 10000: 24000 (Stark), 18000 (Acme 9000), 15000 (Globex), 12000 (Tyrell), 10000 (Initech)
        Assert.Equal(5, result.TotalRows);
        Assert.Equal(["CUSTOMER", "ir1"], result.Columns.Select(c => c.Name));
        Assert.True(result.Columns.Single(c => c.Name == "ir1").Computed);
        var availableComputed = result.AvailableColumns.Single(c => c.Name == "ir1");
        Assert.True(availableComputed.Computed);
        Assert.Equal("number", availableComputed.Type);
        Assert.Equal(24000m, Convert.ToDecimal(result.Rows[0]["ir1"]));
        Assert.Equal("Stark Ind", result.Rows[0]["CUSTOMER"]);
    }

    [Fact]
    public async Task Relational_composables_follow_computed_dependencies_on_an_opaque_table_id()
    {
        var document = new ReportState
        {
            ActiveTable = "anything-the-author-chose",
            Tables = new()
            {
                ["anything-the-author-chose"] = new ReportTable
                {
                    From = "definition",
                    Composables =
                    [
                        new TableComposable
                        {
                            Kind = "compute",
                            // Forward references are scheduled by dependency, not by
                            // the composable's position in the document.
                            Computed = [new ComputedColumn { Id = "ir2", Expr = "ir1 + 1" }],
                        },
                        new TableComposable { Kind = "filter", Filters = [Filter("ir2 < 20000")] },
                        new TableComposable
                        {
                            Kind = "compute",
                            Computed = [new ComputedColumn { Id = "ir1", Expr = "AMOUNT * 2" }],
                        },
                        new TableComposable { Kind = "filter", Filters = [Filter("ir1 >= 10000")] },
                        new TableComposable
                        {
                            Kind = "sort",
                            Sorts = [new SortRule { Col = "ir2", Dir = SortDir.Desc }],
                        },
                        new TableComposable { Kind = "select", Columns = ["AMOUNT", "ir2"] },
                    ],
                },
            },
        };

        var result = await _executor.Query(Definition, document, NoParams);

        Assert.Equal([9000m, 7500m, 6000m, 5000m],
            result.Rows.Select(row => Convert.ToDecimal(row["AMOUNT"])));
        Assert.Equal([18001m, 15001m, 12001m, 10001m],
            result.Rows.Select(row => Convert.ToDecimal(row["ir2"])));
    }

    [Fact]
    public async Task Aggregate_over_computed_column()
    {
        var result = await _executor.Query(Definition, Doc(source: new StageLayer
        {
            Computed = [new ComputedColumn { Id = "ir1", Expr = "AMOUNT * 2" }],
            Aggregates = [new AggregateRule { Col = "ir1", Fn = AggregateFn.Sum }],
        }), NoParams);

        Assert.Equal(94400m, Convert.ToDecimal(result.Aggregates["ir1"]["sum"]));   // 2 × 47200
    }

    [Fact]
    public async Task Text_computed_column_concatenates()
    {
        var result = await _executor.Query(Definition, Doc(source: new StageLayer
        {
            Computed = [new ComputedColumn { Id = "ir1", Expr = "UPPER(CUSTOMER) || '!'" }],
            Filters = [Filter("CUSTOMER = 'Globex'")],
        }), NoParams);

        Assert.Equal("GLOBEX!", Assert.Single(result.Rows)["ir1"]);
    }

    [Fact]
    public async Task Case_computed_column_filters_and_sorts_like_any_other()
    {
        var result = await _executor.Query(Definition, Doc(source: new StageLayer
        {
            Computed = [new ComputedColumn
            {
                Id = "ir1",
                Label = "Size",
                Expr = "CASE WHEN AMOUNT >= 6000 THEN 'BIG' WHEN AMOUNT >= 2000 THEN 'MID' ELSE 'SMALL' END",
            }],
            Columns = ["CUSTOMER", "AMOUNT", "ir1"],
            Filters = [Filter("ir1 = 'BIG'")],
            Sorts = [new SortRule { Col = "AMOUNT", Dir = SortDir.Desc }],
        }), NoParams);

        // BIG = amounts ≥ 6000: Stark 12000, Acme 9000, Globex 7500, Tyrell 6000.
        Assert.Equal(4, result.TotalRows);
        Assert.Equal(["Stark Ind", "Acme Corp", "Globex", "Tyrell Corp"],
            result.Rows.Select(r => (string)r["CUSTOMER"]!));
        Assert.All(result.Rows, r => Assert.Equal("BIG", r["ir1"]));
    }

    [Fact]
    public async Task Case_with_null_test_condition_aggregates()
    {
        var result = await _executor.Query(Definition, Doc(source: new StageLayer
        {
            Computed = [new ComputedColumn
            {
                Id = "ir1",
                Expr = "CASE WHEN NOTES IS NULL OR NOTES = '' THEN 0 ELSE 1 END",
            }],
            Aggregates = [new AggregateRule { Col = "ir1", Fn = AggregateFn.Sum }],
        }), NoParams);

        // Rows with a real note: rush, fragile, call first, insured, standard, refunded.
        Assert.Equal(6m, Convert.ToDecimal(result.Aggregates["ir1"]["sum"]));
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
        var result = await _executor.Query(DateDefinition, Doc(source: new StageLayer
        {
            Computed = [new ComputedColumn
            {
                Id = "ir1",
                Expr = "CASE WHEN TO_DATE(ORDER_DATE) BETWEEN TO_DATE('2026-01-01') AND TO_DATE('2026-12-31') THEN 1 ELSE 0 END",
            }],
            Aggregates = [new AggregateRule { Col = "ir1", Fn = AggregateFn.Sum }],
        }), NoParams);

        Assert.Equal(5m, Convert.ToDecimal(result.Aggregates["ir1"]["sum"]));
    }

    [Fact]
    public async Task Between_is_inclusive_and_does_not_reorder_date_bounds()
    {
        var result = await _executor.Query(DateDefinition, Doc(source: new StageLayer
        {
            Computed =
            [
                new ComputedColumn
                {
                    Id = "ir1",
                    Expr = "CASE WHEN TO_DATE(ORDER_DATE) BETWEEN TO_DATE('2026-02-08') AND TO_DATE('2026-02-08') THEN 1 ELSE 0 END",
                },
                new ComputedColumn
                {
                    Id = "ir2",
                    Expr = "CASE WHEN TO_DATE(ORDER_DATE) BETWEEN TO_DATE('2026-12-31') AND TO_DATE('2026-01-01') THEN 1 ELSE 0 END",
                },
            ],
            Aggregates =
            [
                new AggregateRule { Col = "ir1", Fn = AggregateFn.Sum },
                new AggregateRule { Col = "ir2", Fn = AggregateFn.Sum },
            ],
        }), NoParams);

        Assert.Equal(1m, Convert.ToDecimal(result.Aggregates["ir1"]["sum"]));
        Assert.Equal(0m, Convert.ToDecimal(result.Aggregates["ir2"]["sum"]));
    }

    [Fact]
    public async Task Date_trunc_equality_finds_the_february_orders()
    {
        var result = await _executor.Query(DateDefinition, Doc(source: new StageLayer
        {
            Computed = [new ComputedColumn
            {
                Id = "ir1",
                Expr = "CASE WHEN DATE_TRUNC('MONTH', TO_DATE(ORDER_DATE)) = TO_DATE('2026-02-01') THEN 1 ELSE 0 END",
            }],
            Aggregates = [new AggregateRule { Col = "ir1", Fn = AggregateFn.Sum }],
        }), NoParams);

        // Feb 2026: acme llc (02-16), Umbrella (02-08), Tyrell Corp (02-19).
        Assert.Equal(3m, Convert.ToDecimal(result.Aggregates["ir1"]["sum"]));
    }

    [Fact]
    public async Task To_string_formats_via_the_portable_tokens()
    {
        var result = await _executor.Query(DateDefinition, Doc(source: new StageLayer
        {
            Computed = [new ComputedColumn { Id = "ir1", Expr = "TO_STRING(TO_DATE(ORDER_DATE), 'MM/DD/YYYY')" }],
            Filters = [Filter("CUSTOMER = 'Globex'")],
        }), NoParams);

        Assert.Equal("07/21/2025", Assert.Single(result.Rows)["ir1"]);
    }

    [Fact]
    public async Task Date_arithmetic_shifts_whole_days_across_month_ends()
    {
        var result = await _executor.Query(DateDefinition, Doc(source: new StageLayer
        {
            Computed = [new ComputedColumn { Id = "ir1", Expr = "TO_STRING(TO_DATE(ORDER_DATE) + 10)" }],
            Filters = [Filter("CUSTOMER = 'Tyrell Corp'")],
        }), NoParams);

        // 2026-02-19 + 10 crosses the February month end.
        Assert.Equal("2026-03-01", Assert.Single(result.Rows)["ir1"]);
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
        var result = await _executor.Query(DateDefinition, Doc(source: new StageLayer
        {
            Computed = [new ComputedColumn
            {
                Id = "ir1",
                Expr = "CASE WHEN NOW() BETWEEN TO_DATE('2020-01-01') AND NOW() + 1 THEN 1 ELSE 0 END",
            }],
            Aggregates = [new AggregateRule { Col = "ir1", Fn = AggregateFn.Sum }],
        }), NoParams);

        Assert.Equal(10m, Convert.ToDecimal(result.Aggregates["ir1"]["sum"]));
    }

    [Fact]
    public async Task Simple_case_maps_the_operand()
    {
        var result = await _executor.Query(Definition, Doc(source: new StageLayer
        {
            Computed = [new ComputedColumn
            {
                Id = "ir1",
                Expr = "CASE STATUS WHEN 'SHIPPED' THEN 'done' WHEN 'CANCELLED' THEN 'void' ELSE 'open' END",
            }],
            Filters = [Filter("CUSTOMER = 'Tyrell Corp'")],
        }), NoParams);

        Assert.Equal("void", Assert.Single(result.Rows)["ir1"]);
    }

    [Fact]
    public async Task Highlights_hit_rows_and_cells_with_sql_parity()
    {
        var result = await _executor.Query(Definition, Doc(
            source: new StageLayer
            {
                Sorts = [new SortRule { Col = "AMOUNT", Dir = SortDir.Desc }],
                Highlights =
                [
                    new HighlightRule { Id = "big", Scope = "row", Expr = "AMOUNT > 5000", Style = new HighlightStyle { Bg = "#fee2e2" } },
                    new HighlightRule { Id = "acme", Scope = "cell", Col = "CUSTOMER", Expr = "CONTAINS(CUSTOMER, 'ACME')", Style = new HighlightStyle { Bg = "#fef3c7" } },
                    new HighlightRule { Id = "noNotes", Scope = "row", Expr = "NOTES IS NULL OR NOTES = ''", Style = new HighlightStyle { Bg = "#e0f2fe" } },
                ],
            },
            page: new PageRequest { Index = 1, Size = 10 }), NoParams);

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

        var filtered = await _executor.Query(definition, Doc(source: new StageLayer
        {
            Filters = [new FilterRule { Expr = condition }],
        }), NoParams);
        Assert.Equal(2, filtered.TotalRows); // Stark and Tyrell

        var highlighted = await _executor.Query(definition, Doc(source: new StageLayer
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
        }), NoParams);

        Assert.Equal([0, 3], highlighted.Highlights.Select(hit => hit.Row));
    }

    [Fact]
    public async Task Repeated_highlight_composables_share_identity_sequence_and_marker_names()
    {
        static HighlightRule Rule(string id, string expression) => new()
        {
            Id = id,
            Scope = "row",
            Expr = expression,
            Style = new HighlightStyle { Bg = "gold" },
        };

        var document = new ReportState
        {
            ActiveTable = "result",
            Tables = new()
            {
                ["result"] = new ReportTable
                {
                    From = "definition",
                    Composables =
                    [
                        new TableComposable { Kind = "highlight", Highlights = [Rule("large", "AMOUNT > 10000")] },
                        new TableComposable { Kind = "highlight", Highlights = [Rule("small", "AMOUNT < 500")] },
                    ],
                },
            },
        };

        var definition = Definition;
        var schema = await _executor.GetSchema(definition, NoParams);
        var compiler = new ComposableTableCompiler(
            definition,
            document,
            schema,
            new DateTime(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc),
            (_, _, _, _, _) => Task.FromResult(new List<PivotGroup>()));
        var plan = compiler.CompleteForTarget(await compiler.Compile("result", default));
        Assert.Equal([10, 20], plan.Terminal.Decorations.Select(rule => rule.Effect.Sequence));
        Assert.Equal(2, plan.Terminal.Decorations
            .Select(rule => rule.Effect.ProjectionName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count());

        var result = await _executor.Query(definition, document, NoParams);
        var large = result.Highlights.Single(hit => hit.Id == "large");
        var small = result.Highlights.Single(hit => hit.Id == "small");
        Assert.Equal("Stark Ind", result.Rows[large.Row]["CUSTOMER"]);
        Assert.Equal("Wayne Ent", result.Rows[small.Row]["CUSTOMER"]);
    }

    [Fact]
    public async Task Group_stage_returns_grouped_page_with_counts_and_metric_ids()
    {
        var result = await _executor.Query(Definition, Doc(tail:
        [
            Group(by: ["STATUS"], values: [Metric("ir1", "AMOUNT", AggregateFn.Sum)]),
        ]), NoParams);

        Assert.Equal(4, result.TotalRows);
        Assert.Equal(["STATUS", "__count", "ir1"], result.Columns.Select(c => c.Name));
        Assert.Equal("sum(Amount)", result.Columns[2].Label);
        Assert.Null(result.Columns[1].FormatSource);
        Assert.Null(result.Columns[2].FormatSource);
        Assert.Equal(["CANCELLED", "NEW", "PENDING", "SHIPPED"], result.Rows.Select(r => (string)r["STATUS"]!));
        Assert.Equal([1L, 1L, 3L, 5L], result.Rows.Select(r => Convert.ToInt64(r["__count"])));
        Assert.Equal([6000m, 400m, 14800m, 26000m], result.Rows.Select(r => Convert.ToDecimal(r["ir1"])));
    }

    [Fact]
    public async Task Group_stage_paginates_groups()
    {
        var result = await _executor.Query(Definition, Doc(
            tail: [Group(by: ["STATUS"])],
            page: new PageRequest { Index = 2, Size = 3 }), NoParams);

        Assert.Equal(4, result.TotalRows);
        var row = Assert.Single(result.Rows);
        Assert.Equal("SHIPPED", row["STATUS"]);
    }

    [Fact]
    public async Task Hidden_columns_remain_available_and_can_be_restored_on_every_composition()
    {
        async Task AssertHideAndRestore(ReportState document, string hidden, string kept)
        {
            var table = document.Tables![document.ActiveTable!];
            table.Composables ??= [];
            table.Composables.Add(new TableComposable { Kind = "select", Columns = [kept] });

            var hiddenResult = await _executor.Query(Definition, document, NoParams);

            Assert.DoesNotContain(hiddenResult.Columns, column => column.Name == hidden);
            Assert.Contains(hiddenResult.AvailableColumns, column => column.Name == hidden);
            Assert.All(hiddenResult.Columns, visible =>
                Assert.Contains(hiddenResult.AvailableColumns, available => available.Name == visible.Name));

            // Adopt the server-returned document, as the client does. Changing this
            // table nulls only its cache; the next response refreshes it and restores
            // the formerly hidden column through the same select composable.
            var returned = hiddenResult.Document!;
            var returnedTable = returned.Tables![returned.ActiveTable!];
            var select = returnedTable.Composables!.Last(item => item.Kind == "select");
            select.Columns = [hidden, kept];
            returnedTable.Schema = null;

            var restored = await _executor.Query(Definition, returned, NoParams);

            Assert.Contains(restored.Columns, column => column.Name == hidden);
            Assert.Contains(restored.AvailableColumns, column => column.Name == hidden);
        }

        await AssertHideAndRestore(Doc(), hidden: "AMOUNT", kept: "CUSTOMER");
        await AssertHideAndRestore(
            Doc(tail:
            [
                Group(by: ["STATUS"], values: [Metric("ir1", "AMOUNT", AggregateFn.Sum)]),
            ]),
            hidden: "STATUS",
            kept: "ir1");
        await AssertHideAndRestore(
            Doc(tail:
            [
                ChartStage(shape =>
                {
                    shape.Type = "bar";
                    shape.Label = "STATUS";
                    shape.Fn = AggregateFn.Count;
                }),
            ]),
            hidden: "STATUS",
            kept: "__count");

        var pivotDocument = Doc(tail:
        [
            Pivot(
                rows: ["CUSTOMER"],
                cols: ["STATUS"],
                values: [Metric("ir1", "AMOUNT", AggregateFn.Sum)]),
        ]);
        var pivotSchema = await _executor.Query(Definition, pivotDocument, NoParams);
        var shippedPivotCell = PivotCellId("pivot1", "ir1", "SHIPPED");
        var shipped = pivotSchema.AvailableColumns.Single(column =>
            column.Name == shippedPivotCell).Name;
        await AssertHideAndRestore(
            pivotSchema.Document!,
            hidden: "CUSTOMER",
            kept: shipped);
    }

    [Fact]
    public async Task Group_layer_compute_filter_sort_and_highlight_ride_the_stage()
    {
        var result = await _executor.Query(Definition, Doc(tail:
        [
            Group(
                by: ["STATUS"],
                values: [Metric("ir1", "AMOUNT", AggregateFn.Sum)],
                layer: new StageLayer
                {
                    Computed =
                    [
                        new ComputedColumn { Id = "ir2", Label = "Per Order", Expr = "ROUND(ir1 * 1.0 / __count, 2)" },
                    ],
                    Filters = [Filter("ir2 >= 1000")],
                    Sorts = [new SortRule { Col = "ir1", Dir = SortDir.Desc }],
                    Highlights =
                    [
                        new HighlightRule
                        {
                            Id = "big", Scope = "row", Expr = "ir1 > 20000",
                            Style = new HighlightStyle { Bg = "#fee2e2" },
                        },
                    ],
                }),
        ]), NoParams);

        Assert.Equal(3, result.TotalRows);

        // Metadata comes from ForGroupStage: dims, __count, metrics by id, layer computed.
        Assert.Equal(["STATUS", "__count", "ir1", "ir2"], result.Columns.Select(c => c.Name));
        Assert.Equal(["Status", "Count", "sum(Amount)", "Per Order"], result.Columns.Select(c => c.Label));
        Assert.Null(result.Columns[2].FormatSource);
        Assert.False(result.Columns[2].Computed);
        Assert.True(result.Columns[3].Computed);
        Assert.Null(result.Columns[3].FormatSource);

        // Sorted by the metric, descending; computed values derive from ir1 and __count.
        Assert.Equal(["SHIPPED", "PENDING", "CANCELLED"], result.Rows.Select(r => (string)r["STATUS"]!));
        Assert.Equal([26000m, 14800m, 6000m], result.Rows.Select(r => Convert.ToDecimal(r["ir1"])));
        Assert.Equal([5L, 3L, 1L], result.Rows.Select(r => Convert.ToInt64(r["__count"])));
        Assert.Equal([5200m, 4933.33m, 6000m], result.Rows.Select(r => Convert.ToDecimal(r["ir2"])));
        Assert.All(result.Rows, row => Assert.Equal(["STATUS", "__count", "ir1", "ir2"], row.Keys));

        // The group-layer highlight evaluated in SQL against the stage table.
        var hit = Assert.Single(result.Highlights);
        Assert.Equal((0, "big", null), (hit.Row, hit.Id, hit.Col));
        Assert.All(result.Rows, row =>
            Assert.DoesNotContain(row.Keys, key => key.StartsWith("__ir_highlight_", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task Group_layer_breaks_and_aggregates_describe_the_completed_group_table()
    {
        var result = await _executor.Query(Definition, Doc(
            tail:
            [
                Group(
                    by: ["STATUS", "CUSTOMER"],
                    values: [Metric("ir1", "AMOUNT", AggregateFn.Sum)],
                    layer: new StageLayer
                    {
                        Computed =
                        [
                            new ComputedColumn { Id = "ir2", Expr = "ir1 * 1.0 / __count" },
                        ],
                        Filters = [Filter("ir1 >= 1000")],
                        Breaks = ["STATUS"],
                        Aggregates =
                        [
                            new AggregateRule { Col = "ir1", Fn = AggregateFn.Sum },
                            new AggregateRule { Col = "__count", Fn = AggregateFn.Sum },
                            new AggregateRule { Col = "ir2", Fn = AggregateFn.Sum },
                        ],
                    }),
            ],
            page: new PageRequest { Index = 1, Size = 4 }), NoParams);

        Assert.Equal(7, result.TotalRows); // NEW and Umbrella's small PENDING group were filtered post-group.
        Assert.Equal(4, result.Rows.Count);
        Assert.Equal(["CANCELLED", "PENDING", "PENDING", "SHIPPED"], result.Rows.Select(row => row["STATUS"]));
        Assert.True(result.BreakContinues); // The next page also begins inside SHIPPED.

        Assert.Equal(46000m, Convert.ToDecimal(result.Aggregates["ir1"]["sum"]));
        Assert.Equal(8m, Convert.ToDecimal(result.Aggregates["__count"]["sum"]));
        Assert.Equal(40000m, Convert.ToDecimal(result.Aggregates["ir2"]["sum"]));

        var pending = result.BreakTotals.Single(total => Equals(total.Key["STATUS"], "PENDING"));
        Assert.Equal(2, pending.Rows); // Two surviving Group rows, not source rows.
        Assert.Equal(14000m, Convert.ToDecimal(pending.Aggregates["ir1"]["sum"]));
        Assert.Equal(2m, Convert.ToDecimal(pending.Aggregates["__count"]["sum"]));

        var shipped = result.BreakTotals.Single(total => Equals(total.Key["STATUS"], "SHIPPED"));
        Assert.Equal(4, shipped.Rows); // Four customer groups contain five source rows.
        Assert.Equal(26000m, Convert.ToDecimal(shipped.Aggregates["ir1"]["sum"]));
        Assert.Equal(5m, Convert.ToDecimal(shipped.Aggregates["__count"]["sum"]));
        Assert.Equal(20000m, Convert.ToDecimal(shipped.Aggregates["ir2"]["sum"]));
    }

    [Fact]
    public async Task Grid_and_group_share_sql_terminal_result_assembly()
    {
        static StageLayer TerminalLayer(string valueColumn) => new()
        {
            Columns = [valueColumn],
            Breaks = ["STATUS"],
            Highlights =
            [
                new HighlightRule
                {
                    Id = "terminal",
                    Scope = "row",
                    Expr = $"{valueColumn} > 0",
                    Style = new HighlightStyle { Bg = "gold" },
                },
            ],
        };

        async Task AssertTerminal(ReportState document, string valueColumn, long totalRows)
        {
            var result = await _executor.Query(Definition, document, NoParams);

            Assert.Equal([valueColumn], result.Columns.Select(column => column.Name));
            Assert.Contains(result.AvailableColumns, column => column.Name == "STATUS");
            Assert.Equal((2, 2), (result.Page.Index, result.Page.Size));
            Assert.Equal(totalRows, result.TotalRows);
            Assert.Equal(2, result.Rows.Count);
            Assert.True(result.BreakContinues);
            Assert.Equal([0, 1], result.Highlights.Select(hit => hit.Row));
            Assert.All(result.Highlights, hit => Assert.Equal("terminal", hit.Id));
            Assert.Equal(4, result.BreakTotals.Count);
            Assert.All(result.Rows, row =>
            {
                Assert.Equal(2, row.Count);
                Assert.Contains(valueColumn, row.Keys);
                Assert.Contains("STATUS", row.Keys);
                Assert.DoesNotContain(row.Keys, key =>
                    key.StartsWith("__ir_highlight_", StringComparison.OrdinalIgnoreCase));
            });
        }

        var page = new PageRequest { Index = 2, Size = 2 };
        await AssertTerminal(
            Doc(source: TerminalLayer("AMOUNT"), page: page),
            "AMOUNT",
            totalRows: 10);
        await AssertTerminal(
            Doc(
                tail:
                [
                    Group(
                        by: ["STATUS", "CUSTOMER"],
                        values: [Metric("ir1", "AMOUNT", AggregateFn.Sum)],
                        layer: TerminalLayer("ir1")),
                ],
                page: page),
            "ir1",
            totalRows: 9);
    }

    [Fact]
    public async Task Pivot_view_builds_the_matrix_in_memory()
    {
        var cancelledCell = PivotCellId("pivot1", "ir1", "CANCELLED");
        var newCell = PivotCellId("pivot1", "ir1", "NEW");
        var pendingCell = PivotCellId("pivot1", "ir1", "PENDING");
        var shippedCell = PivotCellId("pivot1", "ir1", "SHIPPED");
        var state = Doc(tail:
        [
            Pivot(rows: ["CUSTOMER"], cols: ["STATUS"], values: [Metric("ir1", "AMOUNT", AggregateFn.Sum)], totals: true),
        ]);
        var result = await _executor.Query(Definition, state, NoParams);

        // 9 distinct customers; columns = CUSTOMER + 4 statuses (sorted: CANCELLED, NEW, PENDING, SHIPPED)
        Assert.Equal(9, result.TotalRows);
        Assert.Equal(5, result.Columns.Count);
        Assert.Equal(
            [cancelledCell, newCell, pendingCell, shippedCell],
            result.Columns.Skip(1).Select(c => c.Name));
        Assert.Equal(["CANCELLED", "NEW", "PENDING", "SHIPPED"], result.Columns.Skip(1).Select(c => c.Label));
        Assert.All(result.Columns.Skip(1), column => Assert.Null(column.FormatSource));
        Assert.All(result.Columns.Skip(1), column => Assert.Equal("ir1", column.PivotMetricId));
        Assert.All(
            result.Document!.Tables![state.ActiveTable!].Schema!.Skip(1),
            column => Assert.Equal("ir1", column.PivotMetricId));

        var acme = result.Rows.Single(r => (string?)r["CUSTOMER"] == "Acme Corp");
        Assert.Equal(12000m, Convert.ToDecimal(acme[shippedCell]));       // SHIPPED: 9000 + 3000
        Assert.False(acme.TryGetValue(pendingCell, out var pending) && pending is not null);   // no PENDING cell
        Assert.Equal(6000m, Convert.ToDecimal(result.Aggregates[cancelledCell]["sum"]));
        Assert.Equal(400m, Convert.ToDecimal(result.Aggregates[newCell]["sum"]));
        Assert.Equal(14800m, Convert.ToDecimal(result.Aggregates[pendingCell]["sum"]));
        Assert.Equal(26000m, Convert.ToDecimal(result.Aggregates[shippedCell]["sum"]));

        var export = await _executor.Download(Definition, state, NoParams);
        var exportedTotal = export.Rows[^1];
        Assert.Equal("Sum:", exportedTotal["CUSTOMER"]);
        Assert.Equal(26000m, Convert.ToDecimal(exportedTotal[shippedCell]));

        var hiddenDimensionExport = await _executor.Download(Definition, Doc(tail:
        [
            Pivot(
                rows: ["CUSTOMER"],
                cols: ["STATUS"],
                values: [Metric("ir1", "AMOUNT", AggregateFn.Sum)],
                totals: true,
                layer: new StageLayer { Columns = [shippedCell] }),
        ]), NoParams);
        Assert.Equal([shippedCell], hiddenDimensionExport.Columns.Select(column => column.Name));
        Assert.Equal(26000m, Convert.ToDecimal(hiddenDimensionExport.Rows[^1][shippedCell]));

        var averages = await _executor.Query(Definition, Doc(tail:
        [
            Pivot(rows: ["CUSTOMER"], cols: ["STATUS"], values: [Metric("ir1", "AMOUNT", AggregateFn.Avg)], totals: true),
        ]), NoParams);
        Assert.Equal(5200d, Convert.ToDouble(averages.Aggregates[shippedCell]["avg"]));
    }

    [Fact]
    public async Task Pivot_export_applies_terminal_labels_formats_and_link_renderer()
    {
        var shipped = PivotCellId("pivot1", "ir1", "SHIPPED");
        var state = Doc(tail:
        [
            Pivot(
                rows: ["CUSTOMER"],
                cols: ["STATUS"],
                values: [Metric("ir1", "AMOUNT", AggregateFn.Sum)],
                layer: new StageLayer
                {
                    Computed =
                    [
                        new ComputedColumn
                        {
                            Id = "ir2",
                            Label = "Customer URL",
                            Expr = "'/customer'",
                        },
                    ],
                    Columns = ["CUSTOMER", shipped],
                    Labels = new() { [shipped] = "Shipped Revenue" },
                    Formats = new()
                    {
                        [shipped] = new ColumnFormat
                        {
                            DisplayAs = "link",
                            UrlColumn = "ir2",
                            TextColumn = shipped,
                            Mask = "currency:USD",
                        },
                    },
                }),
        ]);

        var query = await _executor.Query(Definition, state, NoParams);
        Assert.Equal(["Customer", "SHIPPED"], query.Columns.Select(column => column.Label));
        Assert.Equal(
            "SHIPPED",
            query.Document!.Tables![state.ActiveTable!].Schema!
                .Single(column => column.Name == shipped).Label);

        var export = await _executor.Download(Definition, state, NoParams);
        Assert.Equal(["Customer", "Shipped Revenue"], export.Columns.Select(column => column.Label));
        var acme = export.Rows.Single(row => Equals(row["CUSTOMER"], "Acme Corp"));
        Assert.Equal("$12,000.00", Convert.ToString(acme[shipped]));
        Assert.Equal(["CUSTOMER", shipped], acme.Keys);
    }

    [Fact]
    public async Task Pivot_with_no_values_defaults_to_counts()
    {
        var shippedCount = PivotCellId("pivot1", "__count", "SHIPPED");
        var result = await _executor.Query(Definition, Doc(tail:
        [
            Pivot(rows: ["CUSTOMER"], cols: ["STATUS"]),
        ]), NoParams);

        var acme = result.Rows.Single(r => (string?)r["CUSTOMER"] == "Acme Corp");
        Assert.Equal(2L, Convert.ToInt64(acme[shippedCount]));        // two SHIPPED orders
        Assert.All(result.Columns.Skip(1), column => Assert.Null(column.FormatSource));
        Assert.All(result.Columns.Skip(1), column => Assert.Equal("__count", column.PivotMetricId));

        var explicitCount = await _executor.Query(Definition, Doc(tail:
        [
            Pivot(rows: ["CUSTOMER"], cols: ["STATUS"], values: [Metric("ir1", "AMOUNT", AggregateFn.Count)]),
        ]), NoParams);
        Assert.All(explicitCount.Columns.Skip(1), column => Assert.Null(column.FormatSource));
        Assert.All(explicitCount.Columns.Skip(1), column => Assert.Equal("ir1", column.PivotMetricId));
    }

    [Fact]
    public async Task Pivot_layer_computes_filters_sorts_highlights_and_projects_generated_cells()
    {
        var shipped = PivotCellId("pivot1", "ir1", "SHIPPED");
        var pending = PivotCellId("pivot1", "ir1", "PENDING");
        var state = Doc(tail:
        [
            Pivot(
                rows: ["CUSTOMER"],
                cols: ["STATUS"],
                values: [Metric("ir1", "AMOUNT", AggregateFn.Sum)],
                layer: new StageLayer
                {
                    Computed =
                    [
                        new ComputedColumn
                        {
                            Id = "ir2",
                            Label = "Shipped Less Pending",
                            Expr = $"COALESCE(`{shipped}`, 0) - COALESCE(`{pending}`, 0)",
                        },
                    ],
                    Filters = [Filter("ir2 >= 10000")],
                    Sorts = [new SortRule { Col = "ir2", Dir = SortDir.Desc }],
                    Highlights =
                    [
                        new HighlightRule
                        {
                            Id = "h1", Scope = "cell", Col = shipped,
                            Expr = $"`{shipped}` > 10000",
                            Style = new HighlightStyle { Bg = "gold" },
                        },
                    ],
                    Columns = ["CUSTOMER", shipped, "ir2"],
                    Labels = new() { [shipped] = "Shipped", ["ir2"] = "Shipped Less Pending" },
                }),
        ]);

        var result = await _executor.Query(Definition, state, NoParams);

        var acme = Assert.Single(result.Rows);
        Assert.Equal("Acme Corp", acme["CUSTOMER"]);
        Assert.Equal(12000m, Convert.ToDecimal(acme[shipped]));
        Assert.Equal(12000m, Convert.ToDecimal(acme["ir2"]));
        Assert.Equal(["CUSTOMER", shipped, "ir2"], acme.Keys);
        // Label composables are client presentation. Query/cache metadata stays on
        // structural labels; export applies the override.
        Assert.Equal(["Customer", "SHIPPED", "Shipped Less Pending"], result.Columns.Select(column => column.Label));
        var hit = Assert.Single(result.Highlights);
        Assert.Equal((0, "h1", shipped), (hit.Row, hit.Id, hit.Col));
    }

    [Fact]
    public async Task Pivot_uses_the_shared_break_aggregate_and_hidden_projection_path()
    {
        var shipped = PivotCellId("pivot1", "ir1", "SHIPPED");
        var result = await _executor.Query(Definition, Doc(tail:
        [
            Pivot(
                rows: ["CUSTOMER"],
                cols: ["STATUS"],
                values: [Metric("ir1", "AMOUNT", AggregateFn.Sum)],
                layer: new StageLayer
                {
                    Columns = [shipped],
                    Breaks = ["CUSTOMER"],
                    Aggregates = [new AggregateRule { Col = shipped, Fn = AggregateFn.Sum }],
                }),
        ]), NoParams);

        Assert.Equal([shipped], result.Columns.Select(column => column.Name));
        Assert.Contains(result.Rows, row => row.ContainsKey("CUSTOMER"));
        Assert.Equal(26000m, Convert.ToDecimal(result.Aggregates[shipped]["sum"]));
        var acme = result.BreakTotals.Single(total => Equals(total.Key["CUSTOMER"], "Acme Corp"));
        Assert.Equal(12000m, Convert.ToDecimal(acme.Aggregates[shipped]["sum"]));
    }

    [Fact]
    public async Task Pivot_column_cap_is_a_validation_error()
    {
        var def = Definition;
        def.MaxPivotColumns = 2;

        var ex = await Assert.ThrowsAsync<ReportValidationException>(() =>
            _executor.Query(def, Doc(tail:
            [
                Pivot(rows: ["STATUS"], cols: ["CUSTOMER"]),
            ]), NoParams));

        Assert.Contains(ex.Errors, e => e.Path == "tables.pivot1.composables[0].cols" && e.Message.Contains("max 2"));
    }

    [Fact]
    public async Task Export_grid_caps_rows_and_flags_truncation()
    {
        var def = Definition;
        def.MaxRows = 3;

        var export = await _executor.Download(def, Doc(source: new StageLayer
        {
            Sorts = [new SortRule { Col = "AMOUNT", Dir = SortDir.Desc }],
        }), NoParams);

        Assert.True(export.Truncated);
        Assert.Equal(3, export.Rows.Count);
        Assert.Equal(12000m, Convert.ToDecimal(export.Rows[0]["AMOUNT"]));
    }

    [Fact]
    public async Task Export_group_stage_exports_all_groups_untruncated()
    {
        var export = await _executor.Download(Definition, Doc(tail:
        [
            Group(by: ["STATUS"], values: [Metric("ir1", "AMOUNT", AggregateFn.Sum)]),
        ]), NoParams);

        Assert.False(export.Truncated);
        Assert.Equal(4, export.Rows.Count);
        Assert.Equal(["STATUS", "__count", "ir1"], export.Columns.Select(c => c.Name));
    }

    [Fact]
    public async Task Group_export_applies_terminal_labels_and_image_renderer()
    {
        var state = Doc(tail:
        [
            Group(
                by: ["STATUS"],
                values: [Metric("ir1", "AMOUNT", AggregateFn.Sum)],
                layer: new StageLayer
                {
                    Computed =
                    [
                        new ComputedColumn
                        {
                            Id = "ir2",
                            Label = "Status image",
                            Expr = "'/status.png'",
                        },
                    ],
                    Columns = ["ir1"],
                    Labels = new() { ["ir1"] = "Revenue image" },
                    Formats = new()
                    {
                        ["ir1"] = new ColumnFormat
                        {
                            DisplayAs = "image",
                            UrlColumn = "ir2",
                        },
                    },
                }),
        ]);

        var query = await _executor.Query(Definition, state, NoParams);
        Assert.Equal("sum(Amount)", Assert.Single(query.Columns).Label);
        Assert.Equal(
            "sum(Amount)",
            query.Document!.Tables![state.ActiveTable!].Schema!
                .Single(column => column.Name == "ir1").Label);

        var export = await _executor.Download(Definition, state, NoParams);
        Assert.Equal("Revenue image", Assert.Single(export.Columns).Label);
        Assert.All(export.Rows, row =>
        {
            Assert.Equal("/status.png", row["ir1"]);
            Assert.Equal(["ir1"], row.Keys);
        });
    }

    [Fact]
    public async Task Group_export_does_not_inherit_a_source_tables_renderer()
    {
        var state = Doc(
            source: new StageLayer
            {
                Formats = new()
                {
                    ["AMOUNT"] = new ColumnFormat
                    {
                        DisplayAs = "link",
                        UrlColumn = "NOTES",
                        Mask = "currency:USD",
                    },
                },
            },
            tail:
            [
                Group(
                    by: ["STATUS"],
                    values: [Metric("ir1", "AMOUNT", AggregateFn.Sum)],
                    layer: new StageLayer { Columns = ["ir1"] }),
            ]);

        var export = await _executor.Download(Definition, state, NoParams);

        Assert.Equal("$6,000.00", Convert.ToString(export.Rows[0]["ir1"]));
        Assert.DoesNotContain("<a", Convert.ToString(export.Rows[0]["ir1"]));
    }

    [Fact]
    public async Task Group_export_inherits_labels_and_masks_but_not_parent_renderers()
    {
        ReportTable Ancestor() => Group(
            by: ["STATUS"],
            values: [Metric("ir1", "AMOUNT", AggregateFn.Sum)],
            layer: new StageLayer
            {
                Computed = [new ComputedColumn { Id = "ir2", Expr = "'/ancestor'" }],
                Labels = new() { ["ir1"] = "Ancestor revenue" },
                Formats = new()
                {
                    ["ir1"] = new ColumnFormat
                    {
                        DisplayAs = "link",
                        UrlColumn = "ir2",
                        TextColumn = "ir1",
                        Mask = "currency:USD",
                    },
                },
            });

        var source = new StageLayer
        {
            Labels = new() { ["AMOUNT"] = "Input revenue" },
            Formats = new()
            {
                ["AMOUNT"] = new ColumnFormat
                {
                    DisplayAs = "link",
                    UrlColumn = "NOTES",
                    Mask = "currency:USD",
                },
            },
        };
        var inheritedState = Doc(
            source,
            tail:
            [
                Ancestor(),
                new ReportTable
                {
                    Composables = Composables(new StageLayer { Columns = ["ir1"] }),
                },
            ]);
        var query = await _executor.Query(Definition, inheritedState, NoParams);
        Assert.Equal("sum(Amount)", Assert.Single(query.Columns).Label);
        Assert.Equal(
            "sum(Amount)",
            query.Document!.Tables![inheritedState.ActiveTable!].Schema!
                .Single(column => column.Name == "ir1").Label);

        var inherited = await _executor.Download(Definition, inheritedState, NoParams);

        Assert.Equal("Ancestor revenue", Assert.Single(inherited.Columns).Label);
        Assert.Equal("$6,000.00", Convert.ToString(inherited.Rows[0]["ir1"]));
        Assert.DoesNotContain("<a", Convert.ToString(inherited.Rows[0]["ir1"]));

        var clearedState = Doc(
            source,
            tail:
            [
                Ancestor(),
                new ReportTable
                {
                    Composables = Composables(new StageLayer
                    {
                        Columns = ["ir1"],
                        Labels = new(),
                        Formats = new(),
                    }),
                },
            ]);
        var clearedQuery = await _executor.Query(Definition, clearedState, NoParams);
        var cleared = await _executor.Download(Definition, clearedState, NoParams);

        Assert.Equal("sum(Amount)", Assert.Single(cleared.Columns).Label);
        Assert.Equal(6000m, Convert.ToDecimal(cleared.Rows[0]["ir1"]));
        Assert.Null(clearedQuery.Document!.Tables![clearedState.ActiveTable!].Schema!
            .Single(column => column.Name == "ir1").FormatSource);
    }

    [Fact]
    public async Task Chart_view_aggregates_the_whole_filtered_set()
    {
        var result = await _executor.Query(Definition, Doc(
            source: new StageLayer { Filters = [Filter("STATUS <> 'NEW'")] },
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
            ]), NoParams);

        Assert.Equal(["STATUS", "v0"], result.Columns.Select(c => c.Name));
        Assert.Equal("sum(Amount)", result.Columns[1].Label);
        Assert.Equal("number", result.Columns[1].Type);
        Assert.Null(result.Columns[1].FormatSource);
        Assert.Equal(3, result.TotalRows);
        Assert.Equal(["SHIPPED", "PENDING", "CANCELLED"], result.Rows.Select(r => (string)r["STATUS"]!));
        Assert.Equal([26000m, 14800m, 6000m], result.Rows.Select(r => Convert.ToDecimal(r["v0"])));
        Assert.All(result.Rows, r => Assert.Equal(2, r.Count));      // the grouped __count never leaks
    }

    [Fact]
    public async Task Chart_output_uses_the_shared_ordinary_composable_processor()
    {
        var chart = ChartStage(shape =>
        {
            shape.Type = "bar";
            shape.Label = "STATUS";
            shape.Value = "AMOUNT";
            shape.Fn = AggregateFn.Sum;
        });
        chart.Composables!.AddRange(
        [
            new TableComposable
            {
                Kind = "compute",
                Computed = [new ComputedColumn { Id = "ir2", Expr = "v0 / 100" }],
            },
            new TableComposable { Kind = "filter", Filters = [Filter("ir2 >= 100")] },
            new TableComposable
            {
                Kind = "sort",
                Sorts = [new SortRule { Col = "ir2", Dir = SortDir.Asc }],
            },
            new TableComposable { Kind = "break", Breaks = ["STATUS"] },
            new TableComposable
            {
                Kind = "aggregate",
                Aggregates = [new AggregateRule { Col = "ir2", Fn = AggregateFn.Sum }],
            },
            new TableComposable
            {
                Kind = "highlight",
                Highlights =
                [
                    new HighlightRule
                    {
                        Id = "large", Scope = "row", Expr = "ir2 > 200",
                        Style = new HighlightStyle { Bg = "gold" },
                    },
                ],
            },
            new TableComposable { Kind = "select", Columns = ["ir2"] },
        ]);

        var result = await _executor.Query(Definition, Doc(tail: [chart]), NoParams);

        Assert.Equal(["ir2"], result.Columns.Select(column => column.Name));
        Assert.Equal([148m, 260m], result.Rows.Select(row => Convert.ToDecimal(row["ir2"])));
        Assert.All(result.Rows, row => Assert.Contains("STATUS", row.Keys));
        Assert.Equal(408m, Convert.ToDecimal(result.Aggregates["ir2"]["sum"]));
        Assert.Equal(2, result.BreakTotals.Count);
        Assert.Equal((1, "large"), (Assert.Single(result.Highlights).Row, Assert.Single(result.Highlights).Id));
    }

    [Fact]
    public async Task Chart_view_supports_median_metrics()
    {
        var result = await _executor.Query(Definition, Doc(tail:
        [
            ChartStage(shape =>
            {
                shape.Type = "bar";
                shape.Label = "STATUS";
                shape.Value = "AMOUNT";
                shape.Fn = AggregateFn.Median;
            }),
        ]), NoParams);

        Assert.Equal(["STATUS", "v0"], result.Columns.Select(c => c.Name));
        Assert.Equal("median(Amount)", result.Columns[1].Label);
        Assert.Equal(["CANCELLED", "NEW", "PENDING", "SHIPPED"], result.Rows.Select(r => (string)r["STATUS"]!));
        Assert.Equal([6000m, 400m, 2000m, 5000m], result.Rows.Select(r => Convert.ToDecimal(r["v0"])));
    }

    [Fact]
    public async Task Chart_count_alone_counts_rows_per_label()
    {
        var result = await _executor.Query(Definition, Doc(tail:
        [
            ChartStage(shape =>
            {
                shape.Type = "pie";
                shape.Label = "STATUS";
                shape.Fn = AggregateFn.Count;
            }),
        ]), NoParams);

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

        var result = await _executor.Query(def, Doc(tail:
        [
            ChartStage(shape =>
            {
                shape.Type = "bar";
                shape.Label = "v0";
                shape.Value = "AMOUNT";
                shape.Fn = AggregateFn.Sum;
            }),
        ]), NoParams);

        Assert.Equal(["v0", "v0_metric"], result.Columns.Select(c => c.Name));
        Assert.Null(result.Columns[1].FormatSource);
        Assert.Equal(["CANCELLED", "NEW", "PENDING", "SHIPPED"], result.Rows.Select(r => (string)r["v0"]!));
        Assert.Equal([6000m, 400m, 14800m, 26000m], result.Rows.Select(r => Convert.ToDecimal(r["v0_metric"])));
    }

    [Fact]
    public async Task Chart_count_metric_key_does_not_overwrite_an___count_label()
    {
        var def = Definition;
        def.Name = "chart-count-label";
        def.Sql = "SELECT STATUS AS __count FROM ORDERS";

        var result = await _executor.Query(def, Doc(tail:
        [
            ChartStage(shape =>
            {
                shape.Type = "pie";
                shape.Label = "__count";
                shape.Fn = AggregateFn.Count;
            }),
        ]), NoParams);

        Assert.Equal(["__count", "__count_metric"], result.Columns.Select(c => c.Name));
        Assert.Equal(["CANCELLED", "NEW", "PENDING", "SHIPPED"], result.Rows.Select(r => (string)r["__count"]!));
        Assert.Equal([1L, 1L, 3L, 5L], result.Rows.Select(r => Convert.ToInt64(r["__count_metric"])));
    }

    [Fact]
    public async Task Pie_chart_rejects_negative_metrics()
    {
        var ex = await Assert.ThrowsAsync<ReportValidationException>(() =>
            _executor.Query(Definition, Doc(
                source: new StageLayer
                {
                    Computed = [new ComputedColumn { Id = "ir1", Label = "Loss", Expr = "0 - AMOUNT" }],
                },
                tail:
                [
                    ChartStage(shape =>
                    {
                        shape.Type = "pie";
                        shape.Label = "STATUS";
                        shape.Value = "ir1";
                        shape.Fn = AggregateFn.Sum;
                    }),
                ]), NoParams));

        Assert.Contains(ex.Errors, error =>
            error.Path == "tables.chart1.composables[0].value" && error.Message.Contains("non-negative", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Pie_with_hidden_metric_falls_back_to_a_generic_table()
    {
        var chart = ChartStage(shape =>
        {
            shape.Type = "pie";
            shape.Label = "STATUS";
            shape.Value = "ir1";
            shape.Fn = AggregateFn.Sum;
        });
        chart.Composables!.Add(new TableComposable
        {
            Kind = "select",
            Columns = ["STATUS"],
        });

        var state = Doc(
                source: new StageLayer
                {
                    Computed = [new ComputedColumn { Id = "ir1", Label = "Loss", Expr = "0 - AMOUNT" }],
                },
                tail: [chart]);
        var result = await _executor.Query(Definition, state, NoParams);
        var export = await _executor.Download(Definition, state, NoParams);

        Assert.Equal(["STATUS"], result.Columns.Select(column => column.Name));
        Assert.All(result.Rows, row => Assert.Equal(["STATUS"], row.Keys));
        Assert.Equal(["STATUS"], export.Columns.Select(column => column.Name));
        Assert.All(export.Rows, row => Assert.Equal(["STATUS"], row.Keys));
    }

    [Fact]
    public async Task Pie_validation_runs_after_downstream_filters()
    {
        var chart = ChartStage(shape =>
        {
            shape.Type = "pie";
            shape.Label = "STATUS";
            shape.Value = "ir1";
            shape.Fn = AggregateFn.Sum;
        });
        chart.Composables!.Add(new TableComposable
        {
            Kind = "filter",
            Filters = [Filter("v0 >= 0")],
        });

        var result = await _executor.Query(Definition, Doc(
            source: new StageLayer
            {
                Computed = [new ComputedColumn { Id = "ir1", Label = "Loss", Expr = "0 - AMOUNT" }],
            },
            tail: [chart]), NoParams);

        Assert.Empty(result.Rows);
        Assert.Equal(0, result.TotalRows);
    }

    [Fact]
    public async Task Chart_without_fn_plots_one_point_per_filtered_row()
    {
        var result = await _executor.Query(DateDefinition, Doc(
            source: new StageLayer { Filters = [Filter("AMOUNT >= 5000")] },
            tail:
            [
                ChartStage(shape =>
                {
                    shape.Type = "line";
                    shape.Label = "ORDER_DATE";
                    shape.Value = "AMOUNT";
                }),
            ]), NoParams);

        Assert.Equal(5, result.TotalRows);
        Assert.Equal(["ORDER_DATE", "AMOUNT"], result.Columns.Select(c => c.Name));
        Assert.Null(result.Columns[1].FormatSource);
        Assert.Equal(
            [5000m, 7500m, 9000m, 6000m, 12000m],                    // label (date-text) ascending
            result.Rows.Select(r => Convert.ToDecimal(r["AMOUNT"])));
    }

    [Fact]
    public async Task Raw_chart_uses_distinct_keys_when_label_and_value_are_the_same_column()
    {
        var result = await _executor.Query(Definition, Doc(tail:
        [
            ChartStage(shape =>
            {
                shape.Type = "line";
                shape.Label = "AMOUNT";
                shape.Value = "AMOUNT";
            }),
        ]), NoParams);

        Assert.Equal(["AMOUNT", "AMOUNT_metric"], result.Columns.Select(c => c.Name));
        Assert.Null(result.Columns[1].FormatSource);
        Assert.All(result.Rows, row =>
            Assert.Equal(Convert.ToDecimal(row["AMOUNT"]), Convert.ToDecimal(row["AMOUNT_metric"])));
    }

    [Fact]
    public async Task Chart_aggregates_computed_value_by_computed_label()
    {
        var result = await _executor.Query(Definition, Doc(
            source: new StageLayer
            {
                Computed =
                [
                    new ComputedColumn { Id = "ir1", Label = "Size", Expr = "CASE WHEN AMOUNT >= 6000 THEN 'BIG' ELSE 'SMALL' END" },
                    new ComputedColumn { Id = "ir2", Label = "Doubled", Expr = "AMOUNT * 2" },
                ],
            },
            tail:
            [
                ChartStage(shape =>
                {
                    shape.Type = "bar";
                    shape.Label = "ir1";
                    shape.Value = "ir2";
                    shape.Fn = AggregateFn.Sum;
                }),
            ]), NoParams);

        Assert.Equal("sum(Doubled)", result.Columns[1].Label);
        Assert.Null(result.Columns[1].FormatSource);
        Assert.Equal(["BIG", "SMALL"], result.Rows.Select(r => (string)r["ir1"]!));
        Assert.Equal([69000m, 25400m], result.Rows.Select(r => Convert.ToDecimal(r["v0"])));
    }

    [Fact]
    public async Task Chart_point_limit_is_a_precise_validation_error()
    {
        var def = Definition;
        def.MaxChartPoints = 3;

        var ex = await Assert.ThrowsAsync<ReportValidationException>(() =>
            _executor.Query(def, Doc(tail:
            [
                ChartStage(shape =>
                {
                    shape.Type = "pie";
                    shape.Label = "CUSTOMER";
                    shape.Fn = AggregateFn.Count;
                }),
            ]), NoParams));                                          // 9 distinct customers > 3

        Assert.Contains(ex.Errors, e => e.Path == "tables.chart1.composables[0]" && e.Message.Contains("3 points"));
    }

    [Fact]
    public async Task Downstream_chart_filter_runs_before_the_terminal_point_cap()
    {
        var def = Definition;
        def.MaxChartPoints = 3;
        var chart = ChartStage(shape =>
        {
            shape.Type = "bar";
            shape.Label = "CUSTOMER";
            shape.Fn = AggregateFn.Count;
        });
        chart.Composables!.Add(new TableComposable
        {
            Kind = "filter",
            // Wayne sorts after the old maxPoints+1 source window, so this also proves
            // the shaped SQL was not truncated before the downstream predicate ran.
            Filters = [Filter("CUSTOMER = 'Wayne Ent'")],
        });

        var result = await _executor.Query(def, Doc(tail: [chart]), NoParams);

        var row = Assert.Single(result.Rows);
        Assert.Equal("Wayne Ent", row["CUSTOMER"]);
        Assert.Equal(1L, Convert.ToInt64(row["__count"]));
        Assert.Equal(1, result.TotalRows);
    }

    [Fact]
    public async Task Chart_silently_leaves_source_sorts_inactive()
    {
        var result = await _executor.Query(Definition, Doc(
            source: new StageLayer { Sorts = [new SortRule { Col = "AMOUNT", Dir = SortDir.Desc }] },
            tail:
            [
                ChartStage(shape =>
                {
                    shape.Type = "bar";
                    shape.Label = "STATUS";
                    shape.Fn = AggregateFn.Count;
                }),
            ]), NoParams);

        Assert.Empty(result.Ignored);
        Assert.Equal(
            ["CANCELLED", "NEW", "PENDING", "SHIPPED"],              // chart's own label sort, not the grid sort
            result.Rows.Select(r => (string)r["STATUS"]!));
    }

    [Fact]
    public async Task Export_chart_view_exports_the_charted_points()
    {
        var chart = ChartStage(shape =>
        {
            shape.Type = "bar";
            shape.Label = "STATUS";
            shape.Value = "AMOUNT";
            shape.Fn = AggregateFn.Sum;
        });
        chart.Composables!.AddRange(Composables(new StageLayer
        {
            Computed = [new ComputedColumn { Id = "ir2", Label = "Chart URL", Expr = "'/chart'" }],
            Columns = ["STATUS", "v0"],
            Labels = new() { ["v0"] = "Chart revenue" },
            Formats = new()
            {
                ["v0"] = new ColumnFormat
                {
                    DisplayAs = "link",
                    UrlColumn = "ir2",
                    TextColumn = "v0",
                    Mask = "currency:USD",
                },
            },
        }));
        var state = Doc(tail: [chart]);

        var query = await _executor.Query(Definition, state, NoParams);
        Assert.Equal(["Status", "sum(Amount)"], query.Columns.Select(column => column.Label));
        Assert.Equal(
            "sum(Amount)",
            query.Document!.Tables![state.ActiveTable!].Schema!
                .Single(column => column.Name == "v0").Label);

        var export = await _executor.Download(Definition, state, NoParams);

        Assert.False(export.Truncated);
        Assert.Equal(["STATUS", "v0"], export.Columns.Select(c => c.Name));
        Assert.Equal(["Status", "Chart revenue"], export.Columns.Select(c => c.Label));
        Assert.Equal(4, export.Rows.Count);
        Assert.Equal("$6,000.00", Convert.ToString(export.Rows[0]["v0"]));
        Assert.All(export.Rows, row => Assert.Equal(["STATUS", "v0"], row.Keys));
    }

    [Fact]
    public async Task Highlight_on_computed_column_cell()
    {
        var result = await _executor.Query(Definition, Doc(source: new StageLayer
        {
            Computed = [new ComputedColumn { Id = "ir1", Expr = "AMOUNT * 2" }],
            Sorts = [new SortRule { Col = "AMOUNT", Dir = SortDir.Desc }],
            Highlights =
            [
                new HighlightRule { Id = "h1", Scope = "cell", Col = "ir1", Expr = "ir1 > 20000", Style = new HighlightStyle { Bg = "#fef3c7" } },
            ],
        }), NoParams);

        var hit = Assert.Single(result.Highlights);
        Assert.Equal((0, "h1", "ir1"), (hit.Row, hit.Id, hit.Col));   // only Stark: 24000
    }
}

public sealed class SqliteE2EFixture : IReportConnectionFactory, IDisposable
{
    private readonly string _connectionString =
        $"Data Source=ir-e2e-{Guid.NewGuid():n};Mode=Memory;Cache=Shared";
    private readonly SqliteConnection _keepAlive;

    public SqliteE2EFixture()
    {
        _keepAlive = new SqliteConnection(_connectionString);
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

    public DbConnection CreateConnection(string name) => new SqliteConnection(_connectionString);

    public void Dispose() => _keepAlive.Dispose();
}
