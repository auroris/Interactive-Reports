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
            Sorts =
            [
                new SortRule { Col = "A", Nulls = NullPlacement.First },
                new SortRule { Col = "B" },
            ],
        };

        var json = JsonSerializer.Serialize(state, IrJson.Options);
        using var document = JsonDocument.Parse(json);
        var sorts = document.RootElement.GetProperty("sorts");

        Assert.Equal("first", sorts[0].GetProperty("nulls").GetString());
        Assert.False(sorts[1].TryGetProperty("nulls", out _));

        var roundTrip = JsonSerializer.Deserialize<ReportState>(json, IrJson.Options)!;
        Assert.Equal(NullPlacement.First, roundTrip.Sorts![0].Nulls);
        Assert.Null(roundTrip.Sorts[1].Nulls);
    }

    [Fact]
    public void Configured_views_round_trip_independently_from_the_selected_default_view()
    {
        var state = new ReportState
        {
            View = new ViewSpec
            {
                Mode = "pivot", Rows = ["CUSTOMER"], Cols = ["STATUS"],
            },
            Views = new()
            {
                ["pivot"] = new ViewSpec
                {
                    Mode = "pivot", Rows = ["CUSTOMER"], Cols = ["STATUS"],
                },
                ["chart"] = new ViewSpec
                {
                    Mode = "chart", Type = "pie", Label = "STATUS", Fn = AggregateFn.Count,
                },
            },
        };

        var json = JsonSerializer.Serialize(state, IrJson.Options);
        var roundTrip = JsonSerializer.Deserialize<ReportState>(json, IrJson.Options)!;

        Assert.Equal("pivot", roundTrip.View!.Mode);
        Assert.Equal(["CUSTOMER"], roundTrip.Views!["pivot"].Rows);
        Assert.Equal("pie", roundTrip.Views["chart"].Type);
        Assert.Equal(AggregateFn.Count, roundTrip.Views["chart"].Fn);
    }

    private sealed record WireNumbers(long Signed, ulong Unsigned, decimal Precise);
}
