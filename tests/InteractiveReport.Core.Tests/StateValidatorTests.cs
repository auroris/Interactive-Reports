using InteractiveReport.Core.Model;
using InteractiveReport.Core.Validation;
using static InteractiveReport.Core.Tests.TestFixtures;

namespace InteractiveReport.Core.Tests;

public class StateValidatorTests
{
    private static ValidatedState Validate(ReportState state, ReportDefinition? def = null)
        => StateValidator.Validate(def ?? OrdersDefinition(ReportDialect.Sqlite), state, OrdersSchema);

    [Fact]
    public void Version_one_state_gets_a_direct_migration_error()
    {
        var ex = Assert.Throws<ReportValidationException>(() =>
            Validate(new ReportState { V = 1 }));

        Assert.Contains(ex.Errors, error => error.Path == "v" && error.Message.Contains("version 2"));
    }

    [Fact]
    public void Unknown_filter_column_is_a_precise_expression_error()
    {
        var ex = Assert.Throws<ReportValidationException>(() =>
            Validate(new ReportState { Filters = [Filter("NO_SUCH = 1")] }));

        Assert.Contains(ex.Errors, error => error.Path == "filters[0].expr" && error.Message.Contains("NO_SUCH"));
    }

    [Fact]
    public void Text_operator_on_number_column_is_a_validation_error()
    {
        var ex = Assert.Throws<ReportValidationException>(() =>
            Validate(new ReportState { Filters = [Filter("CONTAINS(AMOUNT, '12')")] }));

        Assert.Contains(ex.Errors, e => e.Path == "filters[0].expr" && e.Message.Contains("must be text"));
    }

    [Fact]
    public void Between_requires_two_element_array()
    {
        var ex = Assert.Throws<ReportValidationException>(() =>
            Validate(new ReportState { Filters = [Filter("AMOUNT BETWEEN 1")] }));

        Assert.Contains(ex.Errors, e => e.Message.Contains("expected AND"));
    }

    [Fact]
    public void Comparison_without_value_points_at_blank_operators()
    {
        var ex = Assert.Throws<ReportValidationException>(() =>
            Validate(new ReportState { Filters = [Filter("STATUS = NULL")] }));

        Assert.Contains(ex.Errors, e => e.Message.Contains("use IS NULL"));
    }

    [Fact]
    public void Untypeable_value_is_precise_error()
    {
        var ex = Assert.Throws<ReportValidationException>(() =>
            Validate(new ReportState { Filters = [Filter("AMOUNT > 'not-a-number'")] }));

        Assert.Contains(ex.Errors, e => e.Path == "filters[0].expr" && e.Message.Contains("number and text"));
    }

    [Fact]
    public void Blank_needs_no_value_and_passes()
    {
        var result = Validate(new ReportState { Filters = [Filter("NOTES IS NULL OR NOTES = ''")] });

        var rule = Assert.Single(result.Rules.RowPredicates);
        Assert.NotNull(rule.Expression.Ast);
    }

    [Fact]
    public void Disabled_expression_rules_remain_state_but_leave_the_execution_plan()
    {
        var result = Validate(new ReportState
        {
            Filters = [new FilterRule { Enabled = false, Expr = "REMOVED_COLUMN = 1" }],
            Computed = [new ComputedColumn { Enabled = false, Id = "invalid", Expr = "also invalid" }],
            Highlights =
            [
                new HighlightRule
                {
                    Id = "", Enabled = false, Scope = "diagonal", Expr = "also invalid",
                },
            ],
        });

        Assert.Empty(result.Rules.Definitions);
        Assert.Empty(result.Rules.RowPredicates);
        Assert.Empty(result.Rules.Decorations);
    }

    [Fact]
    public void Page_size_is_clamped_to_max_and_index_to_one()
    {
        var result = Validate(new ReportState { Page = new PageRequest { Index = -3, Size = 99999 } });

        Assert.Equal(1, result.PageIndex);
        Assert.Equal(500, result.PageSize);
    }

    [Fact]
    public void Default_sorts_apply_when_state_has_none()
    {
        var def = OrdersDefinition(ReportDialect.Sqlite);
        def.DefaultState = new ReportState { Sorts = [new SortRule { Col = "ORDER_DATE", Dir = SortDir.Desc }] };

        var result = Validate(new ReportState(), def);

        var sort = Assert.Single(result.Sorts);
        Assert.Equal("ORDER_DATE", sort.Column.Name);
        Assert.Equal(SortDir.Desc, sort.Dir);
    }

    [Fact]
    public void Explicit_empty_sorts_clear_report_defaults()
    {
        var def = OrdersDefinition(ReportDialect.Sqlite);
        def.DefaultState = new ReportState { Sorts = [new SortRule { Col = "ORDER_DATE", Dir = SortDir.Desc }] };

        var result = Validate(new ReportState { Sorts = [] }, def);

        Assert.Empty(result.Sorts);
    }

    [Fact]
    public void Client_sorts_override_defaults_and_unknown_sort_is_ignored()
    {
        var def = OrdersDefinition(ReportDialect.Sqlite);
        def.DefaultState = new ReportState { Sorts = [new SortRule { Col = "ORDER_DATE", Dir = SortDir.Desc }] };

        var result = Validate(new ReportState
        {
            Sorts = [new SortRule { Col = "AMOUNT" }, new SortRule { Col = "GONE" }],
        }, def);

        var sort = Assert.Single(result.Sorts);
        Assert.Equal("AMOUNT", sort.Column.Name);
        Assert.Contains(result.Ignored, i => i.Kind == "sort" && i.Detail.Contains("GONE"));
    }

    [Fact]
    public void Column_selection_preserves_request_order_and_drops_unknown()
    {
        var result = Validate(new ReportState { Columns = ["AMOUNT", "GHOST", "CUSTOMER"] });

        Assert.Equal(["AMOUNT", "CUSTOMER"], result.SelectColumns.Select(c => c.Name));
        Assert.Contains(result.Ignored, i => i.Kind == "column" && i.Detail.Contains("GHOST"));
    }

    [Fact]
    public void Aggregates_validate_and_dedupe()
    {
        var result = Validate(new ReportState
        {
            Aggregates =
            [
                new AggregateRule { Col = "AMOUNT", Fn = AggregateFn.Sum },
                new AggregateRule { Col = "amount", Fn = AggregateFn.Sum },   // dupe, case-insensitive
                new AggregateRule { Col = "GHOST", Fn = AggregateFn.Sum },    // unknown → ignored
            ],
        });

        var agg = Assert.Single(result.Aggregates);
        Assert.Equal(("AMOUNT", AggregateFn.Sum), (agg.Column.Name, agg.Fn));
        Assert.Contains(result.Ignored, i => i.Kind == "aggregate" && i.Detail.Contains("GHOST"));
    }

    [Fact]
    public void Sum_on_text_column_is_a_validation_error()
    {
        var ex = Assert.Throws<ReportValidationException>(() =>
            Validate(new ReportState
            {
                Aggregates = [new AggregateRule { Col = "CUSTOMER", Fn = AggregateFn.Sum }],
            }));

        Assert.Contains(ex.Errors, e => e.Path == "aggregates[0]" && e.Message.Contains("text column"));
    }

    [Fact]
    public void Min_max_allowed_on_text_and_dates_but_count_on_anything()
    {
        var result = Validate(new ReportState
        {
            Aggregates =
            [
                new AggregateRule { Col = "CUSTOMER", Fn = AggregateFn.Min },
                new AggregateRule { Col = "ORDER_DATE", Fn = AggregateFn.Max },
                new AggregateRule { Col = "NOTES", Fn = AggregateFn.Count },
            ],
        });

        Assert.Equal(3, result.Aggregates.Count);
    }

    [Fact]
    public void Break_columns_are_forced_into_the_selection()
    {
        var result = Validate(new ReportState
        {
            Columns = ["AMOUNT"],
            Breaks = ["REGION", "GHOST"],
        });

        Assert.Equal(["AMOUNT", "REGION"], result.SelectColumns.Select(c => c.Name));
        var b = Assert.Single(result.Breaks);
        Assert.Equal("REGION", b.Name);
        Assert.Contains(result.Ignored, i => i.Kind == "break" && i.Detail.Contains("GHOST"));
    }

    [Fact]
    public void Case_insensitive_column_matching()
    {
        var result = Validate(new ReportState { Filters = [Filter("status = 'SHIPPED'")] });

        var rule = Assert.Single(result.Rules.RowPredicates);
        var comparison = Assert.IsType<InteractiveReport.Core.Expressions.Comparison>(rule.Expression.Ast);
        Assert.Equal("STATUS", Assert.IsType<InteractiveReport.Core.Expressions.ColumnRef>(comparison.Left).Column.Name);
    }

    [Fact]
    public void Computed_columns_join_the_effective_schema_for_everything_downstream()
    {
        var result = Validate(new ReportState
        {
            Computed = [new ComputedColumn { Id = "c1", Label = "Double", Expr = "AMOUNT * 2" }],
            Filters = [Filter("c1 > 100")],
            Sorts = [new SortRule { Col = "c1", Dir = SortDir.Desc }],
            Aggregates = [new AggregateRule { Col = "c1", Fn = AggregateFn.Sum }],
        });

        Assert.Equal("c1", Assert.Single(result.Rules.Definitions).Effect.Column.Name);
        var condition = Assert.IsType<InteractiveReport.Core.Expressions.Comparison>(
            Assert.Single(result.Rules.RowPredicates).Expression.Ast);
        Assert.Equal("c1", Assert.IsType<InteractiveReport.Core.Expressions.ColumnRef>(condition.Left).Column.Name);
        Assert.Equal("c1", Assert.Single(result.Sorts).Column.Name);
        Assert.Equal("c1", Assert.Single(result.Aggregates).Column.Name);
        Assert.Contains(result.SelectColumns, c => c.Name == "c1" && c.IsComputed && c.Label == "Double");
    }

    [Fact]
    public void Expression_rules_compile_into_typed_effect_phases()
    {
        var result = Validate(new ReportState
        {
            Computed = [new ComputedColumn { Id = "c1", Expr = "AMOUNT * 2" }],
            Filters = [Filter("c1 > 100")],
            Highlights =
            [
                new HighlightRule
                {
                    Id = "h1",
                    Scope = "cell",
                    Col = "c1",
                    Expr = "c1 > 200",
                    Style = new HighlightStyle { Bg = "gold" },
                },
            ],
        });

        var definition = Assert.Single(result.Rules.Definitions);
        Assert.Equal(ColumnKind.Number, definition.Expression.Kind);
        Assert.Equal("c1", definition.Effect.Column.Name);

        var predicate = Assert.Single(result.Rules.RowPredicates);
        Assert.Equal(ColumnKind.Bool, predicate.Expression.Kind);

        var decoration = Assert.Single(result.Rules.Decorations);
        Assert.Equal(ColumnKind.Bool, decoration.Expression.Kind);
        Assert.Equal("c1", decoration.Effect.Column!.Name);
    }

    [Fact]
    public void Computed_id_rules_are_enforced()
    {
        var bad = Assert.Throws<ReportValidationException>(() =>
            Validate(new ReportState { Computed = [new ComputedColumn { Id = "x1", Expr = "1" }] }));
        Assert.Contains(bad.Errors, e => e.Message.Contains("must match c1"));

        var dupe = Assert.Throws<ReportValidationException>(() =>
            Validate(new ReportState
            {
                Computed =
                [
                    new ComputedColumn { Id = "c1", Expr = "1" },
                    new ComputedColumn { Id = "c1", Expr = "2" },
                ],
            }));
        Assert.Contains(dupe.Errors, e => e.Message.Contains("duplicate"));
    }

    [Fact]
    public void Computed_id_shadowing_a_schema_column_is_rejected()
    {
        var schemaWithC1 = OrdersSchema.Append(Col("C1", typeof(string))).ToList();
        var ex = Assert.Throws<ReportValidationException>(() =>
            StateValidator.Validate(
                OrdersDefinition(ReportDialect.Sqlite),
                new ReportState { Computed = [new ComputedColumn { Id = "c1", Expr = "1" }] },
                schemaWithC1));

        Assert.Contains(ex.Errors, e => e.Message.Contains("shadows"));
    }

    [Fact]
    public void Bad_expression_is_a_precise_error_at_the_expr_path()
    {
        var ex = Assert.Throws<ReportValidationException>(() =>
            Validate(new ReportState { Computed = [new ComputedColumn { Id = "c1", Expr = "AMOUNT +" }] }));

        Assert.Contains(ex.Errors, e => e.Path == "computed[0].expr");
    }

    [Fact]
    public void Highlights_validate_scope_expression_and_resilience()
    {
        var result = Validate(new ReportState
        {
            Highlights =
            [
                new HighlightRule
                {
                    Id = "h1", Scope = "row",
                    Expr = "AMOUNT > 1000", Style = new HighlightStyle { Bg = "#fff3cd" },
                },
                new HighlightRule
                {
                    Id = "h2", Scope = "cell", Col = "GONE_COLUMN",
                    Expr = "AMOUNT > 1", Style = new HighlightStyle { Bg = "#fff3cd" },
                },
                new HighlightRule
                {
                    Id = "h3", Scope = "row", Enabled = false,
                    Expr = "GONE_TOO = 1",
                },
            ],
        });

        var valid = Assert.Single(result.Rules.Decorations);
        Assert.Equal("h1", valid.Effect.Id);
        Assert.Single(result.Ignored, i => i.Kind == "highlight");
    }

    [Fact]
    public void GroupBy_view_validates_dims_and_moves_grid_features_to_ignored()
    {
        var result = Validate(new ReportState
        {
            View = new ViewSpec
            {
                Mode = "groupBy",
                GroupBy = ["REGION", "GHOST"],
                Values = [new AggregateRule { Col = "AMOUNT", Fn = AggregateFn.Sum }],
            },
            Breaks = ["STATUS"],
            Aggregates = [new AggregateRule { Col = "AMOUNT", Fn = AggregateFn.Avg }],
            Sorts = [new SortRule { Col = "REGION", Dir = SortDir.Desc }, new SortRule { Col = "AMOUNT" }],
        });

        Assert.Equal(ViewMode.GroupBy, result.View.Mode);
        Assert.Equal("REGION", Assert.Single(result.View.GroupBy).Name);
        Assert.Equal("AMOUNT", Assert.Single(result.View.Values).Column.Name);
        Assert.Empty(result.Breaks);
        Assert.Empty(result.Aggregates);
        var sort = Assert.Single(result.Sorts);                       // non-dim sort dropped
        Assert.Equal(("REGION", SortDir.Desc), (sort.Column.Name, sort.Dir));
        Assert.Contains(result.Ignored, i => i.Detail.Contains("unknown groupBy column 'GHOST'"));
        Assert.Contains(result.Ignored, i => i.Detail.Contains("control breaks"));
    }

    [Fact]
    public void Chart_view_validates_and_moves_grid_features_to_ignored()
    {
        var result = Validate(new ReportState
        {
            View = new ViewSpec
            {
                Mode = "chart",
                Type = "bar",
                Label = "STATUS",
                Value = "AMOUNT",
                Fn = AggregateFn.Sum,
                Orientation = "horizontal",
                Sort = new ChartSortSpec { By = "value", Dir = SortDir.Desc },
                LabelAxisTitle = "  Status  ",
                ValueAxisTitle = "Total",
            },
            Breaks = ["REGION"],
            Aggregates = [new AggregateRule { Col = "AMOUNT", Fn = AggregateFn.Avg }],
            Sorts = [new SortRule { Col = "AMOUNT", Dir = SortDir.Desc }],
        });

        Assert.Equal(ViewMode.Chart, result.View.Mode);
        var chart = result.View.Chart!;
        Assert.Equal(ChartType.Bar, chart.Type);
        Assert.Equal("STATUS", chart.Label.Name);
        Assert.Equal("AMOUNT", chart.Value!.Name);
        Assert.Equal(AggregateFn.Sum, chart.Fn);
        Assert.Equal(ChartOrientation.Horizontal, chart.Orientation);
        Assert.Equal((ChartSortBy.Value, SortDir.Desc), (chart.SortBy, chart.SortDir));
        Assert.Equal("Status", chart.LabelAxisTitle);
        Assert.Equal("Total", chart.ValueAxisTitle);
        Assert.Empty(result.Breaks);
        Assert.Empty(result.Aggregates);
        Assert.Empty(result.Sorts);
        Assert.Contains(result.Ignored, i => i.Kind == "view" && i.Detail.Contains("chart sort"));
    }

    [Fact]
    public void Chart_defaults_fill_optional_fields()
    {
        var result = Validate(new ReportState
        {
            View = new ViewSpec { Mode = "chart", Type = "pie", Label = "STATUS", Fn = AggregateFn.Count },
        });

        var chart = result.View.Chart!;
        Assert.Null(chart.Value);                                    // count alone = COUNT(*)
        Assert.Equal(AggregateFn.Count, chart.Fn);
        Assert.Equal(ChartOrientation.Vertical, chart.Orientation);
        Assert.Equal((ChartSortBy.Label, SortDir.Asc), (chart.SortBy, chart.SortDir));
        Assert.Null(chart.LabelAxisTitle);
        Assert.Null(chart.ValueAxisTitle);
    }

    [Fact]
    public void Chart_metric_must_be_numeric_where_grid_aggregation_is_looser()
    {
        // max(ORDER_DATE) is a valid grid aggregate but produces a date — unplottable.
        var ex = Assert.Throws<ReportValidationException>(() =>
            Validate(new ReportState
            {
                View = new ViewSpec
                {
                    Mode = "chart", Type = "bar", Label = "STATUS",
                    Value = "ORDER_DATE", Fn = AggregateFn.Max,
                },
            }));

        Assert.Contains(ex.Errors, e => e.Path == "view.value" && e.Message.Contains("numeric"));
    }

    [Fact]
    public void Chart_without_fn_requires_a_number_value_column()
    {
        var ex = Assert.Throws<ReportValidationException>(() =>
            Validate(new ReportState
            {
                View = new ViewSpec { Mode = "chart", Type = "line", Label = "ORDER_DATE", Value = "CUSTOMER" },
            }));

        Assert.Contains(ex.Errors, e => e.Path == "view.value" && e.Message.Contains("number"));
    }

    [Fact]
    public void Chart_value_is_required_unless_counting_rows()
    {
        var sum = Assert.Throws<ReportValidationException>(() =>
            Validate(new ReportState
            {
                View = new ViewSpec { Mode = "chart", Type = "bar", Label = "STATUS", Fn = AggregateFn.Sum },
            }));
        Assert.Contains(sum.Errors, e => e.Path == "view.value" && e.Message.Contains("'sum'"));

        var distinct = Assert.Throws<ReportValidationException>(() =>
            Validate(new ReportState
            {
                View = new ViewSpec { Mode = "chart", Type = "bar", Label = "STATUS", Fn = AggregateFn.CountDistinct },
            }));
        Assert.Contains(distinct.Errors, e => e.Path == "view.value");

        var bare = Assert.Throws<ReportValidationException>(() =>
            Validate(new ReportState
            {
                View = new ViewSpec { Mode = "chart", Type = "bar", Label = "STATUS" },
            }));
        Assert.Contains(bare.Errors, e => e.Path == "view.value");
    }

    [Fact]
    public void Chart_structural_problems_are_errors()
    {
        var badType = Assert.Throws<ReportValidationException>(() =>
            Validate(new ReportState
            {
                View = new ViewSpec { Mode = "chart", Type = "donut", Label = "STATUS", Fn = AggregateFn.Count },
            }));
        Assert.Contains(badType.Errors, e => e.Path == "view.type" && e.Message.Contains("donut"));

        var noLabel = Assert.Throws<ReportValidationException>(() =>
            Validate(new ReportState
            {
                View = new ViewSpec { Mode = "chart", Type = "bar", Fn = AggregateFn.Count },
            }));
        Assert.Contains(noLabel.Errors, e => e.Path == "view.label");

        var unknownLabel = Assert.Throws<ReportValidationException>(() =>
            Validate(new ReportState
            {
                View = new ViewSpec { Mode = "chart", Type = "bar", Label = "GHOST", Fn = AggregateFn.Count },
            }));
        Assert.Contains(unknownLabel.Errors, e => e.Path == "view.label" && e.Message.Contains("GHOST"));

        var badOrientation = Assert.Throws<ReportValidationException>(() =>
            Validate(new ReportState
            {
                View = new ViewSpec
                {
                    Mode = "chart", Type = "bar", Label = "STATUS", Fn = AggregateFn.Count,
                    Orientation = "diagonal",
                },
            }));
        Assert.Contains(badOrientation.Errors, e => e.Path == "view.orientation");

        var badSort = Assert.Throws<ReportValidationException>(() =>
            Validate(new ReportState
            {
                View = new ViewSpec
                {
                    Mode = "chart", Type = "bar", Label = "STATUS", Fn = AggregateFn.Count,
                    Sort = new ChartSortSpec { By = "hue" },
                },
            }));
        Assert.Contains(badSort.Errors, e => e.Path == "view.sort.by");
    }

    [Fact]
    public void Chart_label_of_unknowable_kind_is_rejected()
    {
        var schemaWithBlob = OrdersSchema.Append(Col("PAYLOAD", typeof(byte[]))).ToList();
        var ex = Assert.Throws<ReportValidationException>(() =>
            StateValidator.Validate(
                OrdersDefinition(ReportDialect.Sqlite),
                new ReportState
                {
                    View = new ViewSpec { Mode = "chart", Type = "bar", Label = "PAYLOAD", Fn = AggregateFn.Count },
                },
                schemaWithBlob));

        Assert.Contains(ex.Errors, e => e.Path == "view.label" && e.Message.Contains("cannot label"));
    }

    [Fact]
    public void View_structural_problems_are_errors()
    {
        var badMode = Assert.Throws<ReportValidationException>(() =>
            Validate(new ReportState { View = new ViewSpec { Mode = "kaleidoscope" } }));
        Assert.Contains(badMode.Errors, e => e.Path == "view.mode");

        var noDims = Assert.Throws<ReportValidationException>(() =>
            Validate(new ReportState { View = new ViewSpec { Mode = "groupBy", GroupBy = ["GHOST"] } }));
        Assert.Contains(noDims.Errors, e => e.Path == "view.groupBy");

        var overlap = Assert.Throws<ReportValidationException>(() =>
            Validate(new ReportState
            {
                View = new ViewSpec { Mode = "pivot", Rows = ["REGION"], Cols = ["REGION"] },
            }));
        Assert.Contains(overlap.Errors, e => e.Message.Contains("both a pivot row and a pivot column"));
    }

    [Fact]
    public void Highlight_structural_problems_are_errors()
    {
        var badScope = Assert.Throws<ReportValidationException>(() =>
            Validate(new ReportState
            {
                Highlights = [new HighlightRule { Id = "h1", Scope = "diagonal", Expr = "AMOUNT > 1", Style = new HighlightStyle { Bg = "red" } }],
            }));
        Assert.Contains(badScope.Errors, e => e.Message.Contains("'row' or 'cell'"));

        var badCondition = Assert.Throws<ReportValidationException>(() =>
            Validate(new ReportState
            {
                Highlights = [new HighlightRule { Id = "h1", Scope = "row", Expr = "AMOUNT + 1", Style = new HighlightStyle { Bg = "red" } }],
            }));
        Assert.Contains(badCondition.Errors, e => e.Path == "highlights[0].expr");

        var noColor = Assert.Throws<ReportValidationException>(() =>
            Validate(new ReportState
            {
                Highlights = [new HighlightRule { Id = "h1", Scope = "row", Expr = "AMOUNT > 1" }],
            }));
        Assert.Contains(noColor.Errors, e => e.Path == "highlights[0].style");
    }
}
