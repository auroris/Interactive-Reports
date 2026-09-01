using System.Text;
using InteractiveReport.Client.FileDownload;
using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Tests;

public class CsvWriterTests
{
    private static readonly IReadOnlyList<ColumnInfo> Columns =
    [
        new("NAME", "Customer Name", "text", false),
        new("AMOUNT", "Amount", "number", false),
    ];

    private static string WriteString(params IReadOnlyDictionary<string, object?>[] rows)
    {
        var bytes = CsvWriter.Write(Columns, rows);
        Assert.Equal(Encoding.UTF8.GetPreamble(), bytes.Take(3).ToArray());   // BOM for Excel
        return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
    }

    private static Dictionary<string, object?> Row(object? name, object? amount)
        => new() { ["NAME"] = name, ["AMOUNT"] = amount };

    [Fact]
    public void Headers_are_labels_rows_crlf_terminated()
    {
        var csv = WriteString(Row("Acme", 12.5m));

        Assert.Equal("Customer Name,Amount\r\nAcme,12.5\r\n", csv);
    }

    [Fact]
    public void Fields_with_commas_quotes_and_newlines_are_quoted_and_escaped()
    {
        var csv = WriteString(Row("Acme, \"The\" Corp\nLine2", 1m));

        Assert.Contains("\"Acme, \"\"The\"\" Corp\nLine2\",1", csv);
    }

    [Fact]
    public void Nulls_are_empty_and_dates_are_sortable_invariant()
    {
        var csv = WriteString(Row(null, new DateTime(2026, 8, 5, 14, 30, 0)));

        Assert.Contains(",2026-08-05 14:30:00\r\n", csv);
        Assert.StartsWith("Customer Name,Amount\r\n,", csv[..csv.IndexOf('2')] + "2");
    }

    [Fact]
    public void Decimal_formatting_is_invariant_culture()
    {
        var csv = WriteString(Row("x", 1234.56m));

        Assert.Contains("x,1234.56", csv);
        Assert.DoesNotContain("1234,56", csv);
    }

    [Fact]
    public void Formula_triggering_text_cells_get_the_apostrophe_guard_by_default()
    {
        // RFC 4180 quoting does not stop Excel from evaluating these; the leading
        // apostrophe (Excel's text marker) does.
        var csv = WriteString(
            Row("=SUM(A1:A9)", 1m),
            Row("+curse", 2m),
            Row("-minus", 3m),
            Row("@at", 4m),
            Row("\ttabbed", 5m),
            Row("\rreturned", 6m));

        Assert.Contains("'=SUM(A1:A9),1", csv);
        Assert.Contains("'+curse,2", csv);
        Assert.Contains("'-minus,3", csv);
        Assert.Contains("'@at,4", csv);
        Assert.Contains("'\ttabbed,5", csv);
        Assert.Contains("\"'\rreturned\",6", csv);   // CR still forces RFC 4180 quoting
    }

    [Fact]
    public void Non_text_values_keep_full_fidelity_under_the_safe_policy()
    {
        var csv = WriteString(Row("x", -1234.5m));

        Assert.Contains("x,-1234.5", csv);
        Assert.DoesNotContain("'-1234.5", csv);
    }

    [Fact]
    public void Dangerous_header_labels_are_guarded_too()
    {
        var columns = new List<ColumnInfo> { new("A", "=EvilLabel", "text", false) };

        var bytes = CsvWriter.Write(columns, []);
        var csv = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);

        Assert.StartsWith("'=EvilLabel\r\n", csv);
    }

    [Fact]
    public void Verbatim_policy_emits_text_exactly_as_stored()
    {
        var bytes = CsvWriter.Write(Columns, [Row("=SUM(A1)", 1m)], CsvCellPolicy.Verbatim);
        var csv = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);

        Assert.Contains("=SUM(A1),1", csv);
        Assert.DoesNotContain("'=SUM(A1)", csv);
    }

    [Fact]
    public void Report_presentation_emits_labels_masks_link_text_and_image_urls_without_browser_markup()
    {
        var available = new[]
        {
            new ColumnInfo("CUSTOMER", "Customer", "text", false),
            new ColumnInfo("AMOUNT", "Amount", "number", false),
            new ColumnInfo("PHOTO", "Photo", "text", false),
            new ColumnInfo("LINK_TEXT", "Link Text", "text", false),
            new ColumnInfo("LINK_URL", "Link Url", "text", false),
            new ColumnInfo("IMAGE_URL", "Image Url", "text", false),
        };
        var result = new ReportResult
        {
            ConfiguredLabels = new Dictionary<string, string> { ["CUSTOMER"] = "Configured customer" },
            AvailableColumns = available,
            Columns = available.Take(3).ToArray(),
            Rows =
            [
                new Dictionary<string, object?>
                {
                    ["CUSTOMER"] = "ignored customer value",
                    ["AMOUNT"] = 1234.5m,
                    ["PHOTO"] = "ignored photo value",
                    ["LINK_TEXT"] = "Acme",
                    ["LINK_URL"] = "/customers/acme",
                    ["IMAGE_URL"] = "/images/acme.png",
                },
            ],
            Page = new PageRequest { Index = 1, Size = 0 },
            TotalRows = 1,
            Ignored = [],
            Document = new ReportState
            {
                ActiveTable = "source",
                Tables = new Dictionary<string, ReportTable>
                {
                    ["source"] = new()
                    {
                        From = "definition",
                        Schema = available.ToList(),
                        Composables =
                        [
                            new TableComposable
                            {
                                Kind = "labels",
                                Labels = new Dictionary<string, string>
                                {
                                    ["CUSTOMER"] = "Client",
                                    ["AMOUNT"] = "Revenue",
                                    ["PHOTO"] = "Portrait",
                                },
                            },
                            new TableComposable
                            {
                                Kind = "formats",
                                Formats = new Dictionary<string, ColumnFormat>
                                {
                                    ["CUSTOMER"] = new()
                                    {
                                        DisplayAs = "link",
                                        TextColumn = "LINK_TEXT",
                                        UrlColumn = "LINK_URL",
                                    },
                                    ["AMOUNT"] = new() { Mask = "currency:USD" },
                                    ["PHOTO"] = new()
                                    {
                                        DisplayAs = "image",
                                        UrlColumn = "IMAGE_URL",
                                    },
                                },
                            },
                        ],
                    },
                },
            },
        };

        var table = CsvReportPresentation.Render(result);
        var bytes = CsvWriter.Write(table.Columns, table.Rows);
        var csv = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);

        Assert.Equal(
            "Client,Revenue,Portrait\r\nAcme,\"$1,234.50\",/images/acme.png\r\n",
            csv);
        Assert.DoesNotContain("<a", csv, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", csv, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LINK_URL", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("IMAGE_URL", csv, StringComparison.Ordinal);
    }
}
