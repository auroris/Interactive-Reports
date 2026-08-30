using InteractiveReport.Core.Model;
using InteractiveReport.Core.Planning;

namespace InteractiveReport.Core.Tests;

public sealed class BoundRequestOverlayTests
{
    [Fact]
    public void From_uses_definition_defaults_when_the_request_has_no_page()
    {
        var definition = Definition(defaultPageSize: 25, maxPageSize: 100);

        var overlay = BoundRequestOverlay.From(definition, new ReportState());

        Assert.Equal(
            new BoundRequestOverlay(Search: null, PageIndex: 1, PageSize: 25, PageAll: false),
            overlay);
    }

    [Theory]
    [InlineData(-7, -3, 1, 1)]
    [InlineData(0, 1, 1, 1)]
    [InlineData(3, 101, 3, 100)]
    [InlineData(int.MaxValue, int.MaxValue, int.MaxValue, 100)]
    public void From_clamps_requested_index_and_size(
        int requestedIndex,
        int requestedSize,
        int expectedIndex,
        int expectedSize)
    {
        var definition = Definition(defaultPageSize: 25, maxPageSize: 100);
        var document = new ReportState
        {
            Page = new PageRequest { Index = requestedIndex, Size = requestedSize },
        };

        var overlay = BoundRequestOverlay.From(definition, document);

        Assert.Equal(
            new BoundRequestOverlay(null, expectedIndex, expectedSize, PageAll: false),
            overlay);
    }

    [Fact]
    public void From_clamps_an_oversized_definition_default()
    {
        var definition = Definition(defaultPageSize: 250, maxPageSize: 100);

        var overlay = BoundRequestOverlay.From(definition, new ReportState());

        Assert.Equal(
            new BoundRequestOverlay(Search: null, PageIndex: 1, PageSize: 100, PageAll: false),
            overlay);
    }

    [Fact]
    public void From_treats_zero_as_page_all_and_resets_the_page_index()
    {
        var explicitPageAll = BoundRequestOverlay.From(
            Definition(defaultPageSize: 25, maxPageSize: 100),
            new ReportState { Page = new PageRequest { Index = 99, Size = 0 } });
        var defaultPageAll = BoundRequestOverlay.From(
            Definition(defaultPageSize: 0, maxPageSize: 100),
            new ReportState());

        var expected = new BoundRequestOverlay(
            Search: null,
            PageIndex: 1,
            PageSize: 0,
            PageAll: true);
        Assert.Equal(expected, explicitPageAll);
        Assert.Equal(expected, defaultPageAll);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("  open orders  ", "open orders")]
    public void From_normalizes_request_search(string? requested, string? expected)
    {
        var document = new ReportState { Search = requested };

        var overlay = BoundRequestOverlay.From(
            Definition(defaultPageSize: 25, maxPageSize: 100),
            document);

        Assert.Equal(expected, overlay.Search);
    }

    private static ReportDefinition Definition(int defaultPageSize, int maxPageSize)
        => new()
        {
            DefaultPageSize = defaultPageSize,
            MaxPageSize = maxPageSize,
        };
}
