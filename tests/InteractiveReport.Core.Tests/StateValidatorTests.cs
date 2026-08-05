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
    public void Future_milestone_features_are_reported_ignored_not_fatal()
    {
        var result = Validate(new ReportState
        {
            Computed = [new ComputedColumn { Id = "c1", Expr = "1" }],
            Highlights = [new HighlightRule { Id = "h1" }],
        });

        Assert.Equal(2, result.Ignored.Count(i => i.Kind == "not-implemented"));
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
}
