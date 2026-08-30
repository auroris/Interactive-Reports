using InteractiveReport.Core.Model;
using InteractiveReport.Core.Validation;
using static InteractiveReport.Core.Tests.TestFixtures;

namespace InteractiveReport.Core.Tests;

/// <summary>
/// Resolver semantics: search and page resolve property-wise; activeTable and the
/// unordered table map replace their defaults when present. Everything is detached
/// so validation cannot mutate a cached default or the request.
/// </summary>
public sealed class ReportStateResolverTests
{
    [Fact]
    public void Resolution_detaches_page_state_as_well_as_table_state()
    {
        var request = new ReportState { Page = new PageRequest { Index = 3, Size = 25 } };

        var resolved = ReportStateResolver.Resolve(null, request);

        Assert.NotSame(request.Page, resolved.Page);
        Assert.Equal(3, resolved.Page!.Index);
        Assert.Equal(25, resolved.Page.Size);
    }

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
            alternatives: new() { ["summary"] = [Group(by: ["STATUS"])] });

        var resolved = ReportStateResolver.Resolve(defaults, new ReportState());

        Assert.Equal("open", resolved.Search);
        Assert.Equal(3, resolved.Page!.Index);
        Assert.Equal("source", resolved.ActiveTable);

        var source = resolved.Tables!["source"];
        var sort = Assert.Single(source.Composables!.Where(c => c.Kind == "sort")).Sorts!.Single();
        var labels = Assert.Single(source.Composables!.Where(c => c.Kind == "labels")).Labels!;
        Assert.Equal("AMOUNT", sort.Col);
        Assert.Equal("Order Total", labels["AMOUNT"]);
        Assert.Equal("STATUS", resolved.Tables["summary"].Composables![0].By!.Single());

        Assert.NotSame(defaults.Tables, resolved.Tables);
        Assert.NotSame(defaults.Tables!["source"], source);
        Assert.NotSame(defaults.Tables["source"].Composables, source.Composables);
        Assert.NotSame(defaults.Tables["source"].Composables![0], source.Composables![0]);
        Assert.NotSame(defaults.Tables["summary"], resolved.Tables["summary"]);
    }

    [Fact]
    public void Search_and_page_resolve_property_wise_with_explicit_empty_as_a_clear()
    {
        var defaults = Doc(search: "open", page: new PageRequest { Index = 3, Size = 75 });

        var cleared = ReportStateResolver.Resolve(defaults, new ReportState { Search = "" });
        Assert.Equal("", cleared.Search);
        Assert.Equal(3, cleared.Page!.Index);

        var paged = ReportStateResolver.Resolve(defaults, new ReportState
        {
            Page = new PageRequest { Index = 1, Size = 10 },
        });
        Assert.Equal("open", paged.Search);
        Assert.Equal((1, 10), (paged.Page!.Index, paged.Page.Size));
    }

    [Fact]
    public void Present_tables_replace_the_default_wholesale()
    {
        var defaults = Doc(
            source: new StageLayer
            {
                Filters = [new FilterRule { Expr = "AMOUNT > 100" }],
                Sorts = [new SortRule { Col = "AMOUNT" }],
                Columns = ["CUSTOMER"],
            },
            tail: [Group(by: ["STATUS"], values: [Metric("ir1", "AMOUNT", AggregateFn.Sum)])]);

        var resolved = ReportStateResolver.Resolve(defaults, Doc());

        var table = Assert.Single(resolved.Tables!);
        Assert.Equal("source", table.Key);
        Assert.Equal("definition", table.Value.From);
        Assert.Empty(table.Value.Composables!);

        var emptied = ReportStateResolver.Resolve(defaults, new ReportState
        {
            ActiveTable = "anything",
            Tables = [],
        });
        Assert.Empty(emptied.Tables!);
        Assert.Equal("anything", emptied.ActiveTable);
    }

    [Fact]
    public void Active_table_and_map_are_independent_explicit_values()
    {
        var defaults = Doc(tail: [Group(by: ["STATUS"])]);
        var request = new ReportState
        {
            ActiveTable = "arbitrary",
            Tables = new()
            {
                ["arbitrary"] = Pivot(rows: ["CUSTOMER"], cols: ["REGION"]),
            },
        };
        request.Tables["arbitrary"].From = "definition";

        var resolved = ReportStateResolver.Resolve(defaults, request);

        Assert.Equal("arbitrary", resolved.ActiveTable);
        Assert.Equal(["arbitrary"], resolved.Tables!.Keys);
        Assert.Equal("pivot", resolved.Tables["arbitrary"].Composables![0].Kind);
    }

    [Fact]
    public void Resolved_documents_are_detached_from_the_request_too()
    {
        var request = Doc(
            source: new StageLayer
            {
                Computed = [new ComputedColumn { Id = "ir1", Expr = "AMOUNT * 2" }],
                Formats = new()
                {
                    ["AMOUNT"] = new ColumnFormat
                    {
                        Mask = "decimal2",
                        Classes = ["amount-column"],
                        DisplayAs = "link",
                        UrlColumn = "NOTES",
                        TextColumn = "CUSTOMER",
                        Command = "open",
                        KeyColumn = "ORDER_ID",
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
            tail: [Group(by: ["STATUS"], values: [Metric("ir1", "AMOUNT", AggregateFn.Sum)])]);

        var resolved = ReportStateResolver.Resolve(null, request);
        var source = resolved.Tables!["source"];
        var originalSource = request.Tables!["source"];
        var format = source.Composables!.Single(c => c.Kind == "formats").Formats!["AMOUNT"];
        var highlight = source.Composables!.Single(c => c.Kind == "highlight").Highlights![0];
        var metric = resolved.Tables[resolved.ActiveTable!].Composables![0].Values![0];

        Assert.NotSame(originalSource, source);
        Assert.NotSame(originalSource.Composables, source.Composables);
        Assert.NotSame(originalSource.Composables!.Single(c => c.Kind == "formats").Formats!["AMOUNT"], format);
        Assert.NotSame(originalSource.Composables!.Single(c => c.Kind == "highlight").Highlights![0], highlight);
        Assert.NotSame(request.Tables[request.ActiveTable!].Composables![0].Values![0], metric);

        Assert.Equal("decimal2", format.Mask);
        Assert.Equal(["amount-column"], format.Classes);
        Assert.Equal(("link", "NOTES", "CUSTOMER", "open", "ORDER_ID"),
            (format.DisplayAs, format.UrlColumn, format.TextColumn, format.Command, format.KeyColumn));
        Assert.Equal(("h1", "Big", 10), (highlight.Id, highlight.Name, highlight.Sequence));
        Assert.Equal(("ir1", "AMOUNT", AggregateFn.Sum), (metric.Id, metric.Col, metric.Fn));
    }

    [Fact]
    public void Chart_composable_survives_the_deep_copy()
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
        var shape = resolved.Tables![resolved.ActiveTable!].Composables![0];
        var original = defaults.Tables![defaults.ActiveTable!].Composables![0];

        Assert.NotSame(original, shape);
        Assert.NotSame(original.Sort, shape.Sort);
        Assert.Equal(("chart", "bar", "STATUS", "AMOUNT", AggregateFn.Sum),
            (shape.Kind, shape.Type, shape.Label, shape.Value, shape.Fn));
        Assert.Equal("horizontal", shape.Orientation);
        Assert.Equal(("value", SortDir.Desc), (shape.Sort!.By, shape.Sort.Dir));
        Assert.Equal(("Status", "Total"), (shape.LabelAxisTitle, shape.ValueAxisTitle));
    }
}
