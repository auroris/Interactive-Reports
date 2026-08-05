using InteractiveReport.Core.Model;
using InteractiveReport.Core.Validation;
using static InteractiveReport.Core.Tests.TestFixtures;

namespace InteractiveReport.Core.Tests;

public class StateValidatorTests
{
    private static ValidatedState Validate(ReportState state, ReportDefinition? def = null)
        => StateValidator.Validate(def ?? OrdersDefinition(ReportDialect.Sqlite), state, OrdersSchema);

    [Fact]
    public void Unknown_filter_column_is_ignored_not_fatal()
    {
        var result = Validate(new ReportState { Filters = [Filter("NO_SUCH", FilterOp.Eq, 1)] });

        Assert.Empty(result.Filters);
        Assert.Contains(result.Ignored, i => i.Kind == "filter" && i.Detail.Contains("NO_SUCH"));
    }

    [Fact]
    public void Text_operator_on_number_column_is_a_validation_error()
    {
        var ex = Assert.Throws<ReportValidationException>(() =>
            Validate(new ReportState { Filters = [Filter("AMOUNT", FilterOp.Contains, "12")] }));

        Assert.Contains(ex.Errors, e => e.Path == "filters[0]" && e.Message.Contains("text column"));
    }

    [Fact]
    public void Between_requires_two_element_array()
    {
        var ex = Assert.Throws<ReportValidationException>(() =>
            Validate(new ReportState { Filters = [Filter("AMOUNT", FilterOp.Between, new[] { 1 })] }));

        Assert.Contains(ex.Errors, e => e.Message.Contains("two-element"));
    }

    [Fact]
    public void Comparison_without_value_points_at_blank_operators()
    {
        var ex = Assert.Throws<ReportValidationException>(() =>
            Validate(new ReportState { Filters = [Filter("STATUS", FilterOp.Eq)] }));

        Assert.Contains(ex.Errors, e => e.Message.Contains("blank"));
    }

    [Fact]
    public void Untypeable_value_is_precise_error()
    {
        var ex = Assert.Throws<ReportValidationException>(() =>
            Validate(new ReportState { Filters = [Filter("AMOUNT", FilterOp.Gt, "not-a-number")] }));

        Assert.Contains(ex.Errors, e => e.Path == "filters[0]" && e.Message.Contains("AMOUNT"));
    }

    [Fact]
    public void Blank_needs_no_value_and_passes()
    {
        var result = Validate(new ReportState { Filters = [Filter("NOTES", FilterOp.Blank)] });

        var f = Assert.Single(result.Filters);
        Assert.Equal(FilterOp.Blank, f.Op);
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
        var result = Validate(new ReportState { Filters = [Filter("status", FilterOp.Eq, "SHIPPED")] });

        var f = Assert.Single(result.Filters);
        Assert.Equal("STATUS", f.Column.Name);
    }

    [Fact]
    public void Computed_columns_join_the_effective_schema_for_everything_downstream()
    {
        var result = Validate(new ReportState
        {
            Computed = [new ComputedColumn { Id = "c1", Label = "Double", Expr = "AMOUNT * 2" }],
            Filters = [Filter("c1", FilterOp.Gt, 100)],
            Sorts = [new SortRule { Col = "c1", Dir = SortDir.Desc }],
            Aggregates = [new AggregateRule { Col = "c1", Fn = AggregateFn.Sum }],
        });

        Assert.Equal("c1", Assert.Single(result.Computed).Column.Name);
        Assert.Equal("c1", Assert.Single(result.Filters).Column.Name);
        Assert.Equal("c1", Assert.Single(result.Sorts).Column.Name);
        Assert.Equal("c1", Assert.Single(result.Aggregates).Column.Name);
        Assert.Contains(result.SelectColumns, c => c.Name == "c1" && c.IsComputed && c.Label == "Double");
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
    public void Highlights_validate_scope_condition_and_resilience()
    {
        var result = Validate(new ReportState
        {
            Highlights =
            [
                new HighlightRule
                {
                    Id = "h1", Scope = "row",
                    Condition = Filter("AMOUNT", FilterOp.Gt, 1000),
                },
                new HighlightRule
                {
                    Id = "h2", Scope = "cell", Col = "GONE_COLUMN",
                    Condition = Filter("AMOUNT", FilterOp.Gt, 1),
                },
                new HighlightRule
                {
                    Id = "h3", Scope = "row",
                    Condition = Filter("GONE_TOO", FilterOp.Eq, 1),
                },
            ],
        });

        var valid = Assert.Single(result.Highlights);
        Assert.Equal("h1", valid.Id);
        Assert.Equal(2, result.Ignored.Count(i => i.Kind == "highlight"));
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
                Highlights = [new HighlightRule { Id = "h1", Scope = "diagonal", Condition = Filter("AMOUNT", FilterOp.Gt, 1) }],
            }));
        Assert.Contains(badScope.Errors, e => e.Message.Contains("'row' or 'cell'"));

        var badCondition = Assert.Throws<ReportValidationException>(() =>
            Validate(new ReportState
            {
                Highlights = [new HighlightRule { Id = "h1", Scope = "row", Condition = Filter("AMOUNT", FilterOp.Contains, "x") }],
            }));
        Assert.Contains(badCondition.Errors, e => e.Path == "highlights[0].condition");
    }
}
