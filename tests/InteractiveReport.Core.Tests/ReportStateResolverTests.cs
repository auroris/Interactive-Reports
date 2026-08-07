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
            Labels = new() { ["AMOUNT"] = "Order Total" },
            Page = new PageRequest { Index = 3, Size = 75 },
        };

        var resolved = ReportStateResolver.Resolve(defaults, new ReportState());

        Assert.Equal("open", resolved.Search);
        Assert.Single(resolved.Sorts!);
        Assert.Equal("Order Total", resolved.Labels!["AMOUNT"]);
        Assert.Equal(3, resolved.Page!.Index);
        Assert.NotSame(defaults.Sorts, resolved.Sorts);
        Assert.NotSame(defaults.Labels, resolved.Labels);
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
    public void Formats_inherit_override_and_clear_like_labels()
    {
        var defaults = new ReportState
        {
            Formats = new() { ["AMOUNT"] = new ColumnFormat { Mask = "decimal2", Align = "right" } },
        };

        var inherited = ReportStateResolver.Resolve(defaults, new ReportState());
        Assert.Equal("decimal2", inherited.Formats!["AMOUNT"].Mask);
        Assert.NotSame(defaults.Formats, inherited.Formats);

        var overridden = ReportStateResolver.Resolve(defaults, new ReportState
        {
            Formats = new() { ["AMOUNT"] = new ColumnFormat { Bold = true } },
        });
        Assert.Null(overridden.Formats!["AMOUNT"].Mask);
        Assert.True(overridden.Formats["AMOUNT"].Bold);

        var cleared = ReportStateResolver.Resolve(defaults, new ReportState { Formats = new() });
        Assert.Empty(cleared.Formats!);
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
