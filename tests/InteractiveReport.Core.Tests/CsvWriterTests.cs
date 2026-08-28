using System.Text;
using InteractiveReport.Core.Export;
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
}
