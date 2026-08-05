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
            Breaks = ["REGION"],
            Aggregates = [new AggregateRule { Col = "AMOUNT", Fn = AggregateFn.Sum }],
        });

        Assert.Equal(2, result.Ignored.Count(i => i.Kind == "not-implemented"));
    }

    [Fact]
    public void Case_insensitive_column_matching()
    {
        var result = Validate(new ReportState { Filters = [Filter("status", FilterOp.Eq, "SHIPPED")] });

        var f = Assert.Single(result.Filters);
        Assert.Equal("STATUS", f.Column.Name);
    }
}
