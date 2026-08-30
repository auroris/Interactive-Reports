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
            ActiveTable = "orders",
            Tables = new()
            {
                ["orders"] = new ReportTable
                {
                    From = "definition",
                    Composables =
                    [
                        new TableComposable
                        {
                            Kind = "sort",
                            Sorts =
                            [
                                new SortRule { Col = "A", Nulls = NullPlacement.First },
                                new SortRule { Col = "B" },
                            ],
                        },
                    ],
                },
            },
        };

        var json = JsonSerializer.Serialize(state, IrJson.Options);
        using var document = JsonDocument.Parse(json);
        var sorts = document.RootElement.GetProperty("tables").GetProperty("orders")
            .GetProperty("composables")[0].GetProperty("sorts");

        Assert.Equal("first", sorts[0].GetProperty("nulls").GetString());
        Assert.False(sorts[1].TryGetProperty("nulls", out _));

        var roundTrip = JsonSerializer.Deserialize<ReportState>(json, IrJson.Options)!;
        var layerSorts = roundTrip.Tables!["orders"].Composables![0].Sorts!;
        Assert.Equal(NullPlacement.First, layerSorts[0].Nulls);
        Assert.Null(layerSorts[1].Nulls);
    }

    [Fact]
    public void Named_tables_and_composables_round_trip_with_camel_case_fields()
    {
        var state = new ReportState
        {
            ActiveTable = "anything",
            Tables = new()
            {
                ["anything"] = new ReportTable
                {
                    From = "definition",
                    Composables =
                    [
                        new TableComposable
                        {
                        Kind = "pivot",
                        Rows = ["CUSTOMER"],
                        Cols = ["STATUS"],
                        Values = [new MetricRule { Id = "ir1", Col = "AMOUNT", Fn = AggregateFn.Sum }],
                        Totals = true,
                        },
                        new TableComposable
                        {
                            Kind = "sort",
                            Sorts = [new SortRule { Col = "CUSTOMER", Dir = SortDir.Desc }],
                        },
                    ],
                },
                ["another"] = new ReportTable
                {
                    From = "definition",
                    Composables =
                    [
                        new TableComposable
                        {
                            Kind = "chart", Type = "pie", Label = "STATUS", Fn = AggregateFn.Count,
                        },
                    ],
                },
            },
        };

        var json = JsonSerializer.Serialize(state, IrJson.Options);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("anything", root.GetProperty("activeTable").GetString());
        var pivot = root.GetProperty("tables").GetProperty("anything").GetProperty("composables")[0];
        Assert.Equal("definition", root.GetProperty("tables").GetProperty("anything").GetProperty("from").GetString());
        Assert.Equal("pivot", pivot.GetProperty("kind").GetString());
        Assert.Equal("sum", pivot.GetProperty("values")[0].GetProperty("fn").GetString());
        Assert.True(pivot.GetProperty("totals").GetBoolean());
        Assert.Equal("chart", root.GetProperty("tables").GetProperty("another")
            .GetProperty("composables")[0].GetProperty("kind").GetString());

        var roundTrip = JsonSerializer.Deserialize<ReportState>(json, IrJson.Options)!;
        Assert.Equal(2, roundTrip.Tables!.Count);
        Assert.Equal("anything", roundTrip.ActiveTable);
        var pivotComposable = roundTrip.Tables["anything"].Composables![0];
        var metric = Assert.Single(pivotComposable.Values!);
        Assert.Equal(("ir1", "AMOUNT", AggregateFn.Sum), (metric.Id, metric.Col, metric.Fn));
        Assert.Equal(["CUSTOMER"], pivotComposable.Rows!);
        Assert.Equal(["STATUS"], pivotComposable.Cols!);
        Assert.True(pivotComposable.Totals);
        var shelfChart = Assert.Single(roundTrip.Tables["another"].Composables!);
        Assert.Equal(("chart", "pie", "STATUS", AggregateFn.Count),
            (shelfChart.Kind, shelfChart.Type, shelfChart.Label, shelfChart.Fn));
    }

    [Fact]
    public void Legacy_documents_with_a_recorded_schema_snapshot_still_hydrate()
    {
        // The schema-snapshot contract was retired 2026-08-28; rows saved before
        // then still carry the key, which now falls through as an unknown member.
        var state = JsonSerializer.Deserialize<ReportState>(
            """{"schema":{"AMOUNT":"number"},"search":"open","pipeline":[{"shape":{"kind":"source"}}]}""",
            IrJson.Options)!;

        Assert.Equal("open", state.Search);
        Assert.Null(state.Tables);

        // Hydration is where the key dies: a re-serialized document is clean.
        var clean = JsonSerializer.Serialize(state, IrJson.Options);
        Assert.DoesNotContain("schema", clean);
        Assert.DoesNotContain("pipeline", clean);
    }

    [Fact]
    public void Numeric_enum_values_are_rejected_as_malformed()
    {
        // The serializer only ever writes camelCase strings for enums, so a numeric
        // token (dir: 99) is foreign input; accepting it would deserialize an
        // undefined member that downstream code silently reinterprets.
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ReportState>(
            """{"activeTable":"x","tables":{"x":{"from":"definition","composables":[{"kind":"sort","sorts":[{"col":"A","dir":99}]}]}}}""",
            IrJson.Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ReportState>(
            """{"activeTable":"x","tables":{"x":{"from":"definition","composables":[{"kind":"group","by":["A"],"values":[{"id":"ir1","col":"A","fn":3}]}]}}}""",
            IrJson.Options));
    }

    private sealed record WireNumbers(long Signed, ulong Unsigned, decimal Precise);
}
