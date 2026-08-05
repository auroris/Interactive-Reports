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
}
