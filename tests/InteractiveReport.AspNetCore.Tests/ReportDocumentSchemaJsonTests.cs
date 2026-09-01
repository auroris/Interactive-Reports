using System.Text.Json;
using InteractiveReport.Core.Model;

namespace InteractiveReport.AspNetCore.Tests;

public sealed class ReportDocumentSchemaJsonTests
{
    [Fact]
    public void Per_table_schema_cache_round_trips_with_protocol_field_names()
    {
        var state = new ReportState
        {
            ActiveTable = "orders",
            Tables = new()
            {
                ["orders"] = new ReportTable
                {
                    From = "definition",
                    Schema =
                    [
                        new ColumnInfo("TOTAL", "Order Total", "number", true)
                        {
                            FormatSource = "AMOUNT",
                            PivotMetricId = "ir9",
                        },
                    ],
                    Composables = [],
                },
            },
        };

        var json = JsonSerializer.Serialize(state, IrJson.Options);
        using var parsed = JsonDocument.Parse(json);
        var column = parsed.RootElement.GetProperty("tables").GetProperty("orders")
            .GetProperty("schema")[0];

        Assert.Equal("TOTAL", column.GetProperty("name").GetString());
        Assert.Equal("Order Total", column.GetProperty("label").GetString());
        Assert.Equal("number", column.GetProperty("type").GetString());
        Assert.True(column.GetProperty("computed").GetBoolean());
        Assert.Equal("AMOUNT", column.GetProperty("formatSource").GetString());
        Assert.Equal("ir9", column.GetProperty("pivotMetricId").GetString());

        var roundTrip = JsonSerializer.Deserialize<ReportState>(json, IrJson.Options)!;
        var hydrated = Assert.Single(roundTrip.Tables!["orders"].Schema!);
        Assert.Equal(("TOTAL", "Order Total", "number", true, "AMOUNT", "ir9"),
            (hydrated.Name, hydrated.Label, hydrated.Type, hydrated.Computed,
                hydrated.FormatSource, hydrated.PivotMetricId));
    }

    [Fact]
    public void Explicit_null_schema_is_accepted_as_cache_invalidation()
    {
        var state = JsonSerializer.Deserialize<ReportState>(
            """{"activeTable":"orders","tables":{"orders":{"from":"definition","schema":null,"composables":[]}}}""",
            IrJson.Options)!;

        Assert.Null(state.Tables!["orders"].Schema);
        var serialized = JsonSerializer.Serialize(state, IrJson.Options);
        Assert.DoesNotContain("schema", serialized);
    }

    [Fact]
    public void Report_document_file_with_json_schema_link_deserializes_correctly()
    {
        var json = """
            {
              "$schema": "../../../schemas/report-document.schema.json",
              "title": "Default",
              "default": true,
              "state": {
                "activeTable": "orders",
                "tables": {
                  "orders": {
                    "from": "definition",
                    "schema": null,
                    "composables": [
                      { "kind": "select", "columns": [ "ORDER_ID", "CUSTOMER" ] }
                    ]
                  }
                }
              }
            }
            """;

        var document = JsonSerializer.Deserialize<ReportDocumentFile>(json, IrJson.Options);
        Assert.NotNull(document);
        Assert.Equal("Default", document.Title);
        Assert.True(document.Default);
        Assert.NotNull(document.State);
        Assert.Equal("orders", document.State.ActiveTable);
        Assert.NotNull(document.State.Tables);
        var ordersTable = document.State.Tables["orders"];
        Assert.Equal("definition", ordersTable.From);
        Assert.Single(ordersTable.Composables!);
        Assert.Equal("select", ordersTable.Composables![0].Kind);
    }
}
