using InteractiveReport.Core.Model;
using InteractiveReport.Core.Validation;
using static InteractiveReport.Core.Tests.TestFixtures;

namespace InteractiveReport.Core.Tests;

/// <summary>
/// The resolver's v3 semantics: search and page resolve property-wise (null inherits,
/// explicit empty clears); pipeline, shelf, and schema replace the default wholesale
/// when present; everything is deep-copied so validation never mutates a cached default.
/// </summary>
public sealed class ReportStateResolverTests
{
    [Fact]
    public void Missing_values_inherit_defaults_as_deep_copies()
    {
        var defaults = Doc(
            source: new StageLayer
            {
                Sorts = [new SortRule { Col = "AMOUNT", Dir = SortDir.Desc }],
                Labels = new() { ["AMOUNT"] = "Order Total" },
            },
            search: "open",
            page: new PageRequest { Index = 3, Size = 75 },
            schema: new() { ["AMOUNT"] = "number" },
            shelf: new() { ["groupBy"] = [Group(by: ["STATUS"])] });

        var resolved = ReportStateResolver.Resolve(defaults, new ReportState());

        Assert.Equal("open", resolved.Search);
        Assert.Equal(3, resolved.Page!.Index);
        Assert.Equal("number", resolved.Schema!["AMOUNT"]);

        var layer = resolved.Pipeline![0].Layer!;
        Assert.Equal("AMOUNT", Assert.Single(layer.Sorts!).Col);
        Assert.Equal("Order Total", layer.Labels!["AMOUNT"]);
        Assert.Equal("STATUS", Assert.Single(resolved.Shelf!["groupBy"][0].Shape!.By!));

        Assert.NotSame(defaults.Pipeline, resolved.Pipeline);
        Assert.NotSame(defaults.Pipeline![0], resolved.Pipeline[0]);
        Assert.NotSame(defaults.Pipeline[0].Layer, resolved.Pipeline[0].Layer);
        Assert.NotSame(defaults.Pipeline[0].Layer!.Sorts, layer.Sorts);
        Assert.NotSame(defaults.Pipeline[0].Layer!.Labels, layer.Labels);
        Assert.NotSame(defaults.Schema, resolved.Schema);
        Assert.NotSame(defaults.Shelf, resolved.Shelf);
        Assert.NotSame(defaults.Shelf!["groupBy"][0], resolved.Shelf["groupBy"][0]);
    }

    [Fact]
    public void Search_and_page_resolve_property_wise_with_explicit_empty_as_a_clear()
    {
        var defaults = Doc(search: "open", page: new PageRequest { Index = 3, Size = 75 });

        var cleared = ReportStateResolver.Resolve(defaults, new ReportState { Search = "" });
        Assert.Equal("", cleared.Search);
        Assert.Equal(3, cleared.Page!.Index);   // page still inherits independently

        var paged = ReportStateResolver.Resolve(defaults, new ReportState
        {
            Page = new PageRequest { Index = 1, Size = 10 },
        });
        Assert.Equal("open", paged.Search);
        Assert.Equal((1, 10), (paged.Page!.Index, paged.Page.Size));
    }

    [Fact]
    public void Present_pipeline_replaces_the_default_wholesale()
    {
        var defaults = Doc(
            source: new StageLayer
            {
                Filters = [new FilterRule { Expr = "AMOUNT > 100" }],
                Sorts = [new SortRule { Col = "AMOUNT" }],
                Columns = ["CUSTOMER"],
            },
            tail: [Group(by: ["STATUS"], values: [Metric("m1", "AMOUNT", AggregateFn.Sum)])]);

        // Stage arrays never merge: a request pipeline with a bare source layer drops
        // the default's filters, sorts, columns, and tail entirely.
        var resolved = ReportStateResolver.Resolve(defaults, Doc());

        var stage = Assert.Single(resolved.Pipeline!);
        Assert.Equal("source", stage.Shape!.Kind);
        Assert.Null(stage.Layer);

        // An explicitly empty pipeline is also a wholesale replacement, not an inherit.
        var emptied = ReportStateResolver.Resolve(defaults, new ReportState { Pipeline = [] });
        Assert.Empty(emptied.Pipeline!);
    }

    [Fact]
    public void Present_shelf_and_schema_replace_wholesale()
    {
        var defaults = Doc(
            schema: new() { ["AMOUNT"] = "number", ["STATUS"] = "text" },
            shelf: new()
            {
                ["groupBy"] = [Group(by: ["STATUS"])],
                ["chart"] = [ChartStage(shape => shape.Type = "pie")],
            });

        var resolved = ReportStateResolver.Resolve(defaults, Doc(
            schema: new() { ["ONLY"] = "text" },
            shelf: new() { ["pivot"] = [Group(by: ["REGION"]), Spread(cols: ["REGION"])] }));

        Assert.Equal(["ONLY"], resolved.Schema!.Keys);
        Assert.Equal(["pivot"], resolved.Shelf!.Keys);

        var clearedShelf = ReportStateResolver.Resolve(defaults, Doc(shelf: new()));
        Assert.Empty(clearedShelf.Shelf!);
        Assert.Equal(2, clearedShelf.Schema!.Count);   // schema still inherits independently
    }

    [Fact]
    public void Resolved_documents_are_detached_from_the_request_too()
    {
        var request = Doc(
            source: new StageLayer
            {
                Computed = [new ComputedColumn { Id = "c1", Expr = "AMOUNT * 2" }],
                Formats = new()
                {
                    ["AMOUNT"] = new ColumnFormat
                    {
                        Mask = "decimal2",
                        Classes = ["amount-column"],
                        DisplayAs = "link",
                        UrlColumn = "NOTES",
                        TextColumn = "CUSTOMER",
                    },
                },
                Highlights =
                [
                    new HighlightRule
                    {
                        Id = "h1", Name = "Big", Sequence = 10, Scope = "cell", Col = "AMOUNT",
                        Expr = "AMOUNT > 1", Style = new HighlightStyle { Bg = "red", Fg = "white" },
                    },
                ],
            },
            tail: [Group(by: ["STATUS"], values: [Metric("m1", "AMOUNT", AggregateFn.Sum)])]);

        var resolved = ReportStateResolver.Resolve(null, request);

        var sourceLayer = resolved.Pipeline![0].Layer!;
        Assert.NotSame(request.Pipeline![0].Layer, sourceLayer);
        Assert.NotSame(request.Pipeline[0].Layer!.Computed![0], sourceLayer.Computed![0]);
        Assert.NotSame(request.Pipeline[0].Layer!.Formats!["AMOUNT"], sourceLayer.Formats!["AMOUNT"]);
        Assert.NotSame(request.Pipeline[0].Layer!.Formats!["AMOUNT"].Classes, sourceLayer.Formats["AMOUNT"].Classes);
        Assert.NotSame(request.Pipeline[0].Layer!.Highlights![0], sourceLayer.Highlights![0]);
        Assert.NotSame(request.Pipeline[1].Shape, resolved.Pipeline[1].Shape);
        Assert.NotSame(request.Pipeline[1].Shape!.Values![0], resolved.Pipeline[1].Shape!.Values![0]);

        // Field fidelity through the deep copy.
        Assert.Equal("decimal2", sourceLayer.Formats["AMOUNT"].Mask);
        Assert.Equal(["amount-column"], sourceLayer.Formats["AMOUNT"].Classes);
        Assert.Equal("link", sourceLayer.Formats["AMOUNT"].DisplayAs);
        Assert.Equal("NOTES", sourceLayer.Formats["AMOUNT"].UrlColumn);
        Assert.Equal("CUSTOMER", sourceLayer.Formats["AMOUNT"].TextColumn);
        Assert.Equal(("h1", "Big", 10), (sourceLayer.Highlights[0].Id, sourceLayer.Highlights[0].Name, sourceLayer.Highlights[0].Sequence));
        Assert.Equal(("m1", "AMOUNT", AggregateFn.Sum), (
            resolved.Pipeline[1].Shape!.Values![0].Id,
            resolved.Pipeline[1].Shape!.Values![0].Col,
            resolved.Pipeline[1].Shape!.Values![0].Fn));
    }

    [Fact]
    public void Chart_shapes_survive_the_deep_copy()
    {
        var defaults = Doc(tail:
        [
            ChartStage(shape =>
            {
                shape.Type = "bar";
                shape.Label = "STATUS";
                shape.Value = "AMOUNT";
                shape.Fn = AggregateFn.Sum;
                shape.Orientation = "horizontal";
                shape.Sort = new ChartSortSpec { By = "value", Dir = SortDir.Desc };
                shape.LabelAxisTitle = "Status";
                shape.ValueAxisTitle = "Total";
            }),
        ]);

        var resolved = ReportStateResolver.Resolve(defaults, new ReportState());

        var shape = resolved.Pipeline![1].Shape!;
        Assert.NotSame(defaults.Pipeline![1].Shape, shape);
        Assert.NotSame(defaults.Pipeline[1].Shape!.Sort, shape.Sort);
        Assert.Equal("bar", shape.Type);
        Assert.Equal("STATUS", shape.Label);
        Assert.Equal("AMOUNT", shape.Value);
        Assert.Equal(AggregateFn.Sum, shape.Fn);
        Assert.Equal("horizontal", shape.Orientation);
        Assert.Equal(("value", SortDir.Desc), (shape.Sort!.By, shape.Sort.Dir));
        Assert.Equal("Status", shape.LabelAxisTitle);
        Assert.Equal("Total", shape.ValueAxisTitle);
    }
}
