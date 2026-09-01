using InteractiveReport.Core.Model;

namespace InteractiveReport.AspNetCore.Tests;

/// <summary>
/// The default document returned by the schema endpoint is the single place
/// friendly names leave the server. Labels are layered onto the definition-input
/// table selected by activeTable ancestry, without relying on map order or names.
/// </summary>
public sealed class SchemaDefaultStateTests
{
    private static ReportDefinition Definition() => new()
    {
        Name = "orders",
        Connection = "db",
        Dialect = ReportDialect.Sqlite,
        Sql = "select 1 as ORDER_ID",
        ColumnLabels = new() { ["ORDER_ID"] = "Order #" },
    };

    private static ReportState Default(params TableComposable[] composables) => new()
    {
        ActiveTable = "orders",
        Tables = new()
        {
            ["orders"] = new ReportTable
            {
                From = "definition",
                Composables = [.. composables],
            },
        },
    };

    private static ReportTable Active(ReportState state) => state.Tables![state.ActiveTable!];
    private static TableComposable Node(ReportState state, string kind)
        => Active(state).Composables!.Single(node => node.Kind == kind);

    [Fact]
    public void Unconfigured_default_synthesizes_a_definition_table_carrying_the_mapping()
    {
        var state = ReportDocumentDefaults.Create(Definition());

        Assert.Equal("base", state.ActiveTable);
        var table = Assert.Single(state.Tables!);
        Assert.Equal("definition", table.Value.From);
        var labels = Assert.Single(table.Value.Composables!);
        Assert.Equal("labels", labels.Kind);
        Assert.Equal("Order #", labels.Labels!["ORDER_ID"]);
    }

    [Fact]
    public void Configured_default_without_labels_inherits_the_mapping()
    {
        var def = Definition();
        def.DefaultState = Default(new TableComposable
        {
            Kind = "sort",
            Sorts = [new SortRule { Col = "ORDER_ID", Dir = SortDir.Desc }],
        });

        var state = ReportDocumentDefaults.Create(def);

        Assert.Single(Node(state, "sort").Sorts!);
        Assert.Equal("Order #", Node(state, "labels").Labels!["ORDER_ID"]);
    }

    [Fact]
    public void Configured_labels_win_over_the_mapping_wholesale()
    {
        var def = Definition();
        def.DefaultState = Default(new TableComposable
        {
            Kind = "labels",
            Labels = new() { ["ORDER_ID"] = "Ticket" },
        });

        var labels = Node(ReportDocumentDefaults.Create(def), "labels").Labels!;

        Assert.Equal("Ticket", labels["ORDER_ID"]);
        Assert.Single(labels);
    }

    [Fact]
    public void Configured_formats_ride_the_default_state_to_the_client()
    {
        var def = Definition();
        def.DefaultState = Default(new TableComposable
        {
            Kind = "formats",
            Formats = new()
            {
                ["ORDER_ID"] = new ColumnFormat
                {
                    Align = "center",
                    Mask = "integer",
                    Classes = ["identifier-column"],
                    DisplayAs = "link",
                    UrlColumn = "ORDER_ID",
                    TextColumn = "ORDER_ID",
                },
            },
        });

        var state = ReportDocumentDefaults.Create(def);
        var formats = Node(state, "formats").Formats!;
        var configuredFormats = Node(def.DefaultState, "formats").Formats!;

        Assert.Equal(("center", "integer", "link", "ORDER_ID", "ORDER_ID"),
            (formats["ORDER_ID"].Align, formats["ORDER_ID"].Mask, formats["ORDER_ID"].DisplayAs,
                formats["ORDER_ID"].UrlColumn, formats["ORDER_ID"].TextColumn));
        Assert.Equal(["identifier-column"], formats["ORDER_ID"].Classes);
        Assert.NotSame(configuredFormats, formats);
        Assert.NotSame(configuredFormats["ORDER_ID"], formats["ORDER_ID"]);
    }

    [Fact]
    public void Column_override_labels_merge_over_column_labels_into_the_mapping()
    {
        var def = Definition();
        def.Columns = new() { ["LABEL"] = new ReportColumnOverride { Label = "Caption" } };

        var labels = Node(ReportDocumentDefaults.Create(def), "labels").Labels!;

        Assert.Equal("Order #", labels["ORDER_ID"]);
        Assert.Equal("Caption", labels["LABEL"]);
    }

    [Fact]
    public void Override_only_labels_seed_the_mapping_without_column_labels()
    {
        var def = Definition();
        def.ColumnLabels = null;
        def.Columns = new() { ["ORDER_ID"] = new ReportColumnOverride { Label = "Ticket" } };

        Assert.Equal("Ticket",
            Node(ReportDocumentDefaults.Create(def), "labels").Labels!["ORDER_ID"]);
    }

    [Fact]
    public void Labels_follow_active_ancestry_instead_of_dictionary_order()
    {
        var def = Definition();
        def.DefaultState = new ReportState
        {
            ActiveTable = "summary",
            Tables = new()
            {
                ["unrelated"] = new ReportTable { From = "definition", Composables = [] },
                ["summary"] = new ReportTable
                {
                    From = "actual-input",
                    Composables = [new TableComposable { Kind = "group", By = ["ORDER_ID"] }],
                },
                ["actual-input"] = new ReportTable { From = "definition", Composables = [] },
            },
        };

        var state = ReportDocumentDefaults.Create(def);

        Assert.DoesNotContain(state.Tables!["unrelated"].Composables!, c => c.Kind == "labels");
        Assert.Equal("Order #",
            state.Tables["actual-input"].Composables!.Single(c => c.Kind == "labels").Labels!["ORDER_ID"]);
    }

    [Theory]
    [InlineData("group")]
    [InlineData("pivot")]
    [InlineData("chart")]
    public void Post_shape_labels_do_not_suppress_definition_input_labels(string shapeKind)
    {
        var def = Definition();
        def.DefaultState = Default(
            new TableComposable
            {
                Kind = "sort",
                Sorts = [new SortRule { Col = "ORDER_ID" }],
            },
            new TableComposable { Kind = shapeKind },
            new TableComposable
            {
                Kind = "labels",
                Labels = new() { ["ir1"] = "Revenue" },
            });

        var composables = Active(ReportDocumentDefaults.Create(def)).Composables!;
        var outputLabels = composables[3].Labels!;

        Assert.Equal(["sort", "labels", shapeKind, "labels"], composables.Select(c => c.Kind));
        Assert.Equal("Order #", composables[1].Labels!["ORDER_ID"]);
        Assert.Equal("Revenue", outputLabels["ir1"]);
        Assert.DoesNotContain("ORDER_ID", outputLabels.Keys);
    }

    [Fact]
    public void Empty_post_shape_labels_do_not_inherit_definition_input_labels()
    {
        var def = Definition();
        def.DefaultState = Default(
            new TableComposable { Kind = "group", By = ["ORDER_ID"] },
            new TableComposable { Kind = "labels" });

        var composables = Active(ReportDocumentDefaults.Create(def)).Composables!;

        Assert.Equal(["labels", "group", "labels"], composables.Select(c => c.Kind));
        Assert.Equal("Order #", composables[0].Labels!["ORDER_ID"]);
        Assert.Null(composables[2].Labels);
    }

    [Fact]
    public void Response_shaping_never_mutates_the_definition()
    {
        var def = Definition();
        def.DefaultState = Default(new TableComposable
        {
            Kind = "sort",
            Sorts = [new SortRule { Col = "ORDER_ID" }],
        });

        var state = ReportDocumentDefaults.Create(def);
        var labels = Node(state, "labels").Labels!;
        var sorts = Node(state, "sort").Sorts!;

        Assert.NotSame(def.DefaultState, state);
        Assert.NotSame(def.DefaultState.Tables, state.Tables);
        Assert.NotSame(def.ColumnLabels, labels);
        labels["ORDER_ID"] = "changed";
        sorts.Clear();
        Assert.Equal("Order #", def.ColumnLabels!["ORDER_ID"]);
        Assert.Single(Node(def.DefaultState, "sort").Sorts!);
        Assert.DoesNotContain(Active(def.DefaultState).Composables!, c => c.Kind == "labels");
    }
}
