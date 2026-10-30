using InteractiveReport.Client.FileDownload;
using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Tests;

public sealed class HtmlReportPresentationTests
{
    [Fact]
    public void Html_escapes_values_and_renders_breaks_and_whole_set_totals()
    {
        var result = new ReportResult {
            Columns = [new("TEAM", "Team", "text", false), new("VALUE", "Value", "number", false)],
            AvailableColumns = [], Page = new() { Size = 25 }, TotalRows = 1, Ignored = [],
            Rows = [new Dictionary<string, object?> { ["TEAM"] = "<script>alert(1)</script>", ["VALUE"] = 12 }],
            Aggregates = new Dictionary<string, IReadOnlyDictionary<string, object?>> { ["VALUE"] = new Dictionary<string, object?> { ["sum"] = 12 } },
            BreakTotals = [new(new Dictionary<string, object?> { ["TEAM"] = "<script>alert(1)</script>" }, 1,
                new Dictionary<string, IReadOnlyDictionary<string, object?>> { ["VALUE"] = new Dictionary<string, object?> { ["sum"] = 12 } })],
            Document = new ReportState { ActiveTable = "base", Tables = new() {
                ["base"] = new() { From = "definition", Composables = [new() { Kind = "formats", Formats = new() {
                    ["VALUE"] = new() { Mask = "$#,##0.00" },
                } }] },
            } },
        };
        var html = HtmlReportPresentation.Render("<Title>", result, "all", "A4", true);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;Title&gt;", html);
        Assert.Contains("Subtotal", html);
        Assert.Contains("All matching rows — sum", html);
        Assert.Contains("size:A4 landscape", html);
        Assert.Equal(3, html.Split("$12.00").Length - 1); // Detail, subtotal, and whole-set total share the mask.
    }

}
