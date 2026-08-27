using InteractiveReport.Core.Model;
using InteractiveReport.Core.Schema;

namespace InteractiveReport.AspNetCore.Tests;

/// <summary>
/// The default report the schema endpoint sends down is the single place friendly
/// names leave the server: columnLabels seed the source layer's labels, a configured
/// default's own labels win, a pipeline always exists, the live schema snapshot is
/// stamped when absent, and response shaping never mutates the stored definition.
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

    private static ReportSchema LiveSchema() => ReportSchema.Create("orders",
    [
        new ColumnModel { Name = "ORDER_ID", Label = "Order Id", ClrType = typeof(long) },
        new ColumnModel { Name = "LABEL", Label = "Label", ClrType = typeof(string) },
    ]);

    [Fact]
    public void Unconfigured_default_synthesizes_a_source_pipeline_carrying_the_mapping()
    {
        var state = EndpointExtensions.SchemaDefaultState(Definition(), LiveSchema());

        Assert.Equal(ReportState.CurrentVersion, state.V);
        var stage = Assert.Single(state.Pipeline!);
        Assert.Equal("source", stage.Shape!.Kind);
        Assert.NotNull(stage.Layer);
        Assert.Null(stage.Layer!.Columns);   // null = every schema column in database order
        Assert.Null(stage.Layer.Filters);
        Assert.Equal("Order #", stage.Layer.Labels!["ORDER_ID"]);
    }

    [Fact]
    public void Delivered_default_is_stamped_with_the_live_schema_snapshot()
    {
        var state = EndpointExtensions.SchemaDefaultState(Definition(), LiveSchema());

        Assert.Equal("number", state.Schema!["ORDER_ID"]);
        Assert.Equal("text", state.Schema["LABEL"]);
        Assert.Equal(2, state.Schema.Count);
        Assert.True(state.Schema.ContainsKey("order_id"));   // case-insensitive lookup
    }

    [Fact]
    public void A_configured_snapshot_is_preserved_not_overwritten()
    {
        var def = Definition();
        def.DefaultState = new ReportState { Schema = new() { ["ORDER_ID"] = "text" } };

        var state = EndpointExtensions.SchemaDefaultState(def, LiveSchema());

        Assert.Equal("text", Assert.Single(state.Schema!).Value);
    }

    [Fact]
    public void Configured_default_without_labels_inherits_the_mapping()
    {
        var def = Definition();
        def.DefaultState = new ReportState
        {
            Pipeline =
            [
                new PipelineStage
                {
                    Shape = new StageShape { Kind = "source" },
                    Layer = new StageLayer { Sorts = [new SortRule { Col = "ORDER_ID", Dir = SortDir.Desc }] },
                },
            ],
        };

        var state = EndpointExtensions.SchemaDefaultState(def, LiveSchema());

        var layer = state.Pipeline![0].Layer!;
        Assert.Single(layer.Sorts!);
        Assert.Equal("Order #", layer.Labels!["ORDER_ID"]);
    }

    [Fact]
    public void Configured_labels_win_over_the_mapping_wholesale()
    {
        var def = Definition();
        def.DefaultState = new ReportState
        {
            Pipeline =
            [
                new PipelineStage
                {
                    Shape = new StageShape { Kind = "source" },
                    Layer = new StageLayer { Labels = new() { ["ORDER_ID"] = "Ticket" } },
                },
            ],
        };

        var state = EndpointExtensions.SchemaDefaultState(def, LiveSchema());

        var labels = state.Pipeline![0].Layer!.Labels!;
        Assert.Equal("Ticket", labels["ORDER_ID"]);
        Assert.Single(labels);
    }

    [Fact]
    public void Configured_formats_ride_the_default_state_to_the_client()
    {
        var def = Definition();
        def.DefaultState = new ReportState
        {
            Pipeline =
            [
                new PipelineStage
                {
                    Shape = new StageShape { Kind = "source" },
                    Layer = new StageLayer
                    {
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
                    },
                },
            ],
        };

        var state = EndpointExtensions.SchemaDefaultState(def, LiveSchema());

        var formats = state.Pipeline![0].Layer!.Formats!;
        Assert.Equal("center", formats["ORDER_ID"].Align);
        Assert.Equal("integer", formats["ORDER_ID"].Mask);
        Assert.Equal(["identifier-column"], formats["ORDER_ID"].Classes);
        Assert.Equal("link", formats["ORDER_ID"].DisplayAs);
        Assert.Equal("ORDER_ID", formats["ORDER_ID"].UrlColumn);
        Assert.Equal("ORDER_ID", formats["ORDER_ID"].TextColumn);
        Assert.NotSame(def.DefaultState.Pipeline![0].Layer!.Formats, formats);
        Assert.NotSame(def.DefaultState.Pipeline[0].Layer!.Formats!["ORDER_ID"], formats["ORDER_ID"]);
    }

    [Fact]
    public void Column_override_labels_merge_over_column_labels_into_the_mapping()
    {
        var def = Definition();                                       // columnLabels: ORDER_ID → "Order #"
        def.Columns = new() { ["LABEL"] = new ReportColumnOverride { Label = "Caption" } };

        var state = EndpointExtensions.SchemaDefaultState(def, LiveSchema());

        var labels = state.Pipeline![0].Layer!.Labels!;
        Assert.Equal("Order #", labels["ORDER_ID"]);
        Assert.Equal("Caption", labels["LABEL"]);
    }

    [Fact]
    public void Override_only_labels_seed_the_mapping_without_column_labels()
    {
        var def = Definition();
        def.ColumnLabels = null;
        def.Columns = new() { ["ORDER_ID"] = new ReportColumnOverride { Label = "Ticket" } };

        var state = EndpointExtensions.SchemaDefaultState(def, LiveSchema());

        Assert.Equal("Ticket", state.Pipeline![0].Layer!.Labels!["ORDER_ID"]);
    }

    [Fact]
    public void Response_shaping_never_mutates_the_definition()
    {
        var def = Definition();
        def.DefaultState = new ReportState
        {
            Pipeline =
            [
                new PipelineStage
                {
                    Shape = new StageShape { Kind = "source" },
                    Layer = new StageLayer { Sorts = [new SortRule { Col = "ORDER_ID" }] },
                },
            ],
        };

        var state = EndpointExtensions.SchemaDefaultState(def, LiveSchema());

        Assert.NotSame(def.DefaultState, state);
        Assert.NotSame(def.DefaultState.Pipeline, state.Pipeline);
        var layer = state.Pipeline![0].Layer!;
        Assert.NotSame(def.ColumnLabels, layer.Labels);
        layer.Labels!["ORDER_ID"] = "changed";
        layer.Sorts!.Clear();
        Assert.Equal("Order #", def.ColumnLabels!["ORDER_ID"]);
        Assert.Single(def.DefaultState.Pipeline![0].Layer!.Sorts!);
        Assert.Null(def.DefaultState.Pipeline[0].Layer!.Labels);
        Assert.Null(def.DefaultState.Schema);
    }
}
