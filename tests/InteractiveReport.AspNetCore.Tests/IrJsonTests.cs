using System.Text.Json;
using InteractiveReport.Core.Model;

namespace InteractiveReport.AspNetCore.Tests;

public sealed class IrJsonTests
{
    [Fact]
    public void Int64_uint64_and_decimal_values_are_exact_strings_on_the_wire()
    {
        var payload = new
        {
            signed = long.MaxValue,
            unsigned = ulong.MaxValue,
            precise = 79228162514264337593543950335m,
            scale = 12345678901234567890.123456789m,
            ordinary = 42,
            boxed = new Dictionary<string, object?>
            {
                ["long"] = 9007199254740993L,
                ["decimal"] = 999999999999999999.999999999m,
            },
        };

        var json = JsonSerializer.Serialize(payload, IrJson.Options);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(JsonValueKind.String, root.GetProperty("signed").ValueKind);
        Assert.Equal("9223372036854775807", root.GetProperty("signed").GetString());
        Assert.Equal("18446744073709551615", root.GetProperty("unsigned").GetString());
        Assert.Equal("79228162514264337593543950335", root.GetProperty("precise").GetString());
        Assert.Equal("12345678901234567890.123456789", root.GetProperty("scale").GetString());
        Assert.Equal(JsonValueKind.Number, root.GetProperty("ordinary").ValueKind);
        Assert.Equal("9007199254740993", root.GetProperty("boxed").GetProperty("long").GetString());
        Assert.Equal("999999999999999999.999999999", root.GetProperty("boxed").GetProperty("decimal").GetString());
    }

    [Fact]
    public void Exact_number_converters_accept_legacy_json_numbers_and_new_strings()
    {
        var fromStrings = JsonSerializer.Deserialize<WireNumbers>(
            """{"signed":"9223372036854775807","unsigned":"18446744073709551615","precise":"0.1234567890123456789012345678"}""",
            IrJson.Options)!;
        var fromNumbers = JsonSerializer.Deserialize<WireNumbers>(
            """{"signed":42,"unsigned":43,"precise":44.5}""",
            IrJson.Options)!;

        Assert.Equal(long.MaxValue, fromStrings.Signed);
        Assert.Equal(ulong.MaxValue, fromStrings.Unsigned);
        Assert.Equal(0.1234567890123456789012345678m, fromStrings.Precise);
        Assert.Equal(42, fromNumbers.Signed);
        Assert.Equal(43ul, fromNumbers.Unsigned);
        Assert.Equal(44.5m, fromNumbers.Precise);
    }

    [Fact]
    public void Sort_null_placement_uses_camel_case_strings_and_default_is_omitted()
    {
        var state = new ReportState
        {
            Pipeline =
            [
                new PipelineStage
                {
                    Shape = new StageShape { Kind = "source" },
                    Layer = new StageLayer
                    {
                        Sorts =
                        [
                            new SortRule { Col = "A", Nulls = NullPlacement.First },
                            new SortRule { Col = "B" },
                        ],
                    },
                },
            ],
        };

        var json = JsonSerializer.Serialize(state, IrJson.Options);
        using var document = JsonDocument.Parse(json);
        var sorts = document.RootElement.GetProperty("pipeline")[0]
            .GetProperty("layer").GetProperty("sorts");

        Assert.Equal("first", sorts[0].GetProperty("nulls").GetString());
        Assert.False(sorts[1].TryGetProperty("nulls", out _));

        var roundTrip = JsonSerializer.Deserialize<ReportState>(json, IrJson.Options)!;
        var layerSorts = roundTrip.Pipeline![0].Layer!.Sorts!;
        Assert.Equal(NullPlacement.First, layerSorts[0].Nulls);
        Assert.Null(layerSorts[1].Nulls);
    }

    [Fact]
    public void Pipeline_and_shelf_round_trip_with_camel_case_shape_fields()
    {
        var state = new ReportState
        {
            Pipeline =
            [
                new PipelineStage { Shape = new StageShape { Kind = "source" } },
                new PipelineStage
                {
                    Shape = new StageShape
                    {
                        Kind = "group",
                        By = ["CUSTOMER", "STATUS"],
                        Values = [new MetricRule { Id = "m1", Col = "AMOUNT", Fn = AggregateFn.Sum }],
                    },
                },
                new PipelineStage { Shape = new StageShape { Kind = "spread", Cols = ["STATUS"], Totals = true } },
            ],
            Shelf = new()
            {
                ["chart"] =
                [
                    new PipelineStage
                    {
                        Shape = new StageShape
                        {
                            Kind = "chart", Type = "pie", Label = "STATUS", Fn = AggregateFn.Count,
                        },
                    },
                ],
            },
        };

        var json = JsonSerializer.Serialize(state, IrJson.Options);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("source", root.GetProperty("pipeline")[0].GetProperty("shape").GetProperty("kind").GetString());
        Assert.Equal("sum", root.GetProperty("pipeline")[1].GetProperty("shape")
            .GetProperty("values")[0].GetProperty("fn").GetString());          // camelCase enum on the wire
        Assert.True(root.GetProperty("pipeline")[2].GetProperty("shape").GetProperty("totals").GetBoolean());
        Assert.Equal("chart", root.GetProperty("shelf").GetProperty("chart")[0]
            .GetProperty("shape").GetProperty("kind").GetString());           // shelf keys stay verbatim

        var roundTrip = JsonSerializer.Deserialize<ReportState>(json, IrJson.Options)!;
        Assert.Equal(3, roundTrip.Pipeline!.Count);
        var metric = Assert.Single(roundTrip.Pipeline[1].Shape!.Values!);
        Assert.Equal(("m1", "AMOUNT", AggregateFn.Sum), (metric.Id, metric.Col, metric.Fn));
        Assert.Equal(["STATUS"], roundTrip.Pipeline[2].Shape!.Cols!);
        Assert.True(roundTrip.Pipeline[2].Shape!.Totals);
        var shelfChart = Assert.Single(roundTrip.Shelf!["chart"]);
        Assert.Equal(("chart", "pie", "STATUS", AggregateFn.Count),
            (shelfChart.Shape!.Kind, shelfChart.Shape.Type, shelfChart.Shape.Label, shelfChart.Shape.Fn));
    }

    [Fact]
    public void Legacy_documents_with_a_recorded_schema_snapshot_still_hydrate()
    {
        // The schema-snapshot contract was retired 2026-08-28; rows saved before
        // then still carry the key, which now falls through as an unknown member.
        var state = JsonSerializer.Deserialize<ReportState>(
            """{"v":3,"schema":{"AMOUNT":"number"},"search":"open","pipeline":[{"shape":{"kind":"source"}}]}""",
            IrJson.Options)!;

        Assert.Equal("open", state.Search);
        Assert.Equal("source", Assert.Single(state.Pipeline!).Shape!.Kind);

        // Hydration is where the key dies: a re-serialized document is clean.
        Assert.DoesNotContain("schema", JsonSerializer.Serialize(state, IrJson.Options));
    }

    private sealed record WireNumbers(long Signed, ulong Unsigned, decimal Precise);
}
