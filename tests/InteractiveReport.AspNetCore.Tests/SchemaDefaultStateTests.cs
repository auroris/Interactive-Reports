using InteractiveReport.Core.Model;

namespace InteractiveReport.AspNetCore.Tests;

/// <summary>
/// The default report the schema endpoint sends down is the single place friendly
/// names leave the server: columnLabels seed its labels map, a configured default's
/// own labels win, and response shaping never mutates the stored definition.
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

    [Fact]
    public void Unconfigured_default_synthesizes_an_empty_state_carrying_the_mapping()
    {
        var state = EndpointExtensions.SchemaDefaultState(Definition());

        Assert.Equal(ReportState.CurrentVersion, state.V);
        Assert.Null(state.Columns);   // null = every schema column in database order
        Assert.Null(state.Filters);
        Assert.Equal("Order #", state.Labels!["ORDER_ID"]);
    }

    [Fact]
    public void Configured_default_without_labels_inherits_the_mapping()
    {
        var def = Definition();
        def.DefaultState = new ReportState { Sorts = [new SortRule { Col = "ORDER_ID", Dir = SortDir.Desc }] };

        var state = EndpointExtensions.SchemaDefaultState(def);

        Assert.Single(state.Sorts!);
        Assert.Equal("Order #", state.Labels!["ORDER_ID"]);
    }

    [Fact]
    public void Configured_labels_win_over_the_mapping_wholesale()
    {
        var def = Definition();
        def.DefaultState = new ReportState { Labels = new() { ["ORDER_ID"] = "Ticket" } };

        var state = EndpointExtensions.SchemaDefaultState(def);

        Assert.Equal("Ticket", state.Labels!["ORDER_ID"]);
        Assert.Single(state.Labels);
    }

    [Fact]
    public void Configured_formats_ride_the_default_state_to_the_client()
    {
        var def = Definition();
        def.DefaultState = new ReportState
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
        };

        var state = EndpointExtensions.SchemaDefaultState(def);

        Assert.Equal("center", state.Formats!["ORDER_ID"].Align);
        Assert.Equal("integer", state.Formats["ORDER_ID"].Mask);
        Assert.Equal(["identifier-column"], state.Formats["ORDER_ID"].Classes);
        Assert.Equal("link", state.Formats["ORDER_ID"].DisplayAs);
        Assert.Equal("ORDER_ID", state.Formats["ORDER_ID"].UrlColumn);
        Assert.Equal("ORDER_ID", state.Formats["ORDER_ID"].TextColumn);
        Assert.NotSame(def.DefaultState.Formats, state.Formats);
        Assert.NotSame(def.DefaultState.Formats["ORDER_ID"], state.Formats["ORDER_ID"]);
    }

    [Fact]
    public void Response_shaping_never_mutates_the_definition()
    {
        var def = Definition();
        def.DefaultState = new ReportState { Sorts = [new SortRule { Col = "ORDER_ID" }] };

        var state = EndpointExtensions.SchemaDefaultState(def);

        Assert.NotSame(def.DefaultState, state);
        Assert.NotSame(def.ColumnLabels, state.Labels);
        state.Labels!["ORDER_ID"] = "changed";
        state.Sorts!.Clear();
        Assert.Equal("Order #", def.ColumnLabels!["ORDER_ID"]);
        Assert.Single(def.DefaultState.Sorts!);
        Assert.Null(def.DefaultState.Labels);
    }
}
