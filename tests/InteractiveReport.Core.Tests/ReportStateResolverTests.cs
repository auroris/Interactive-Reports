using InteractiveReport.Core.Model;
using InteractiveReport.Core.Validation;

namespace InteractiveReport.Core.Tests;

public sealed class ReportStateResolverTests
{
    [Fact]
    public void Missing_values_inherit_defaults()
    {
        var defaults = new ReportState
        {
            Search = "open",
            Sorts = [new SortRule { Col = "AMOUNT", Dir = SortDir.Desc }],
            Page = new PageRequest { Index = 3, Size = 75 },
        };

        var resolved = ReportStateResolver.Resolve(defaults, new ReportState());

        Assert.Equal("open", resolved.Search);
        Assert.Single(resolved.Sorts!);
        Assert.Equal(3, resolved.Page!.Index);
        Assert.NotSame(defaults.Sorts, resolved.Sorts);
    }

    [Fact]
    public void Explicit_empty_values_clear_defaults()
    {
        var defaults = new ReportState
        {
            Search = "open",
            Filters = [new FilterRule { Expr = "AMOUNT > 100" }],
            Sorts = [new SortRule { Col = "AMOUNT" }],
            Columns = ["CUSTOMER"],
        };
        var request = new ReportState
        {
            Search = "",
            Filters = [],
            Sorts = [],
            Columns = [],
        };

        var resolved = ReportStateResolver.Resolve(defaults, request);

        Assert.Equal("", resolved.Search);
        Assert.Empty(resolved.Filters!);
        Assert.Empty(resolved.Sorts!);
        Assert.Empty(resolved.Columns!);
    }

    [Fact]
    public void Explicit_grid_view_overrides_an_alternate_default()
    {
        var defaults = new ReportState { View = new ViewSpec { Mode = "pivot" } };
        var request = new ReportState { View = new ViewSpec { Mode = "grid" } };

        var resolved = ReportStateResolver.Resolve(defaults, request);

        Assert.Equal("grid", resolved.View!.Mode);
    }
}
