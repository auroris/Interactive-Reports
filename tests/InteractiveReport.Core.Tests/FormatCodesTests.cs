using InteractiveReport.Core.Formatting;

namespace InteractiveReport.Core.Tests;

public class FormatCodesTests
{
    [Theory]
    [InlineData("#,##0", "1,235")]
    [InlineData("#,##0.0", "1,234.6")]
    [InlineData("#,##0.00", "1,234.57")]
    [InlineData("#,##0.000", "1,234.567")]
    [InlineData("#,##0.0000", "1,234.5670")]
    [InlineData("0.00", "1234.57")]
    [InlineData("0", "1235")]
    [InlineData("#", "1235")]
    [InlineData("#,##0.##", "1,234.57")]
    [InlineData("#,##0.#", "1,234.6")]
    [InlineData("0000000.0", "0001234.6")]
    [InlineData("#,###", "1,235")]
    public void Digit_placeholders_round_pad_and_group(string code, string expected)
        => Assert.Equal(expected, FormatCodes.FormatNumber(1234.567m, code));

    [Theory]
    [InlineData("0.5", "#.##", ".5")]
    [InlineData("0.5", "0.##", "0.5")]
    [InlineData("0", "#", "")]
    [InlineData("0", "0.00", "0.00")]
    [InlineData("7", "000", "007")]
    [InlineData("1234", "000,000", "001,234")]
    [InlineData("1234567.891", "#,##0.00", "1,234,567.89")]
    [InlineData("999.995", "0.00", "1000.00")]
    [InlineData("2.5", "0", "3")]
    [InlineData("-2.5", "0", "-3")]
    [InlineData("-0.004", "0.00", "0.00")]
    public void Edge_values_follow_excel(string value, string code, string expected)
        => Assert.Equal(expected, FormatCodes.FormatNumber(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture), code));

    [Theory]
    [InlineData("1234567", "#,##0,", "1,235")]
    [InlineData("1234567", "#,##0.0,", "1,234.6")]
    [InlineData("1234567", "#,##0.00,,", "1.23")]
    [InlineData("1234567", "#,##0,\"K\"", "1,235K")]
    [InlineData("1234567", "0.0,,\" M\"", "1.2 M")]
    public void Trailing_commas_scale_by_thousands(string value, string code, string expected)
        => Assert.Equal(expected, FormatCodes.FormatNumber(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture), code));

    [Theory]
    [InlineData("0.123456", "0%", "12%")]
    [InlineData("0.123456", "0.0%", "12.3%")]
    [InlineData("0.123456", "0.00%", "12.35%")]
    [InlineData("6590.01", "0.00%", "659001.00%")]
    [InlineData("6590.01", "#,##0.00\"%\"", "6,590.01%")]
    [InlineData("6590.01", "#,##0.00 %", "659,001.00 %")]
    [InlineData("0.5", "%0", "%50")]
    [InlineData("-0.25", "0.0%", "-25.0%")]
    public void Percent_scales_only_when_bare(string value, string code, string expected)
        => Assert.Equal(expected, FormatCodes.FormatNumber(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture), code));

    [Theory]
    [InlineData("$#,##0.00", "$1,234.57")]
    [InlineData("€#,##0.00", "€1,234.57")]
    [InlineData("£#,##0.00", "£1,234.57")]
    [InlineData("¥#,##0", "¥1,235")]
    [InlineData("#,##0.00 \"CAD\"", "1,234.57 CAD")]
    [InlineData("\"CA$\"#,##0.00", "CA$1,234.57")]
    [InlineData("0 \"units\"", "1235 units")]
    [InlineData("0\\h", "1235h")]
    [InlineData("_(0_)", " 1235 ")]
    [InlineData("*-0", "1235")]
    [InlineData("[$€-407]#,##0.00", "€1,234.57")]
    [InlineData("[$ CHF]#,##0", " CHF1,235")]
    [InlineData("[Red]#,##0", "1,235")]
    [InlineData("[Color 10]#,##0", "1,235")]
    [InlineData("0 \"a\" \"b\"", "1235 a b")]
    [InlineData("(0)", "(1235)")]
    [InlineData("+0", "+1235")]
    [InlineData("0/0", null)]
    [InlineData("0 \"x\" 0", null)]
    public void Literals_frame_the_digits(string code, string? expected)
        => Assert.Equal(expected, FormatCodes.FormatNumber(1234.567m, code));

    [Theory]
    [InlineData("1234.567", "#,##0.00;(#,##0.00)", "1,234.57")]
    [InlineData("-1234.567", "#,##0.00;(#,##0.00)", "(1,234.57)")]
    [InlineData("-1234.567", "#,##0.00", "-1,234.57")]
    [InlineData("-1234.567", "$#,##0.00", "-$1,234.57")]
    [InlineData("-1234.567", "#,##0.00;\"minus \"#,##0.00", "minus 1,234.57")]
    [InlineData("0", "#,##0.00;(#,##0.00);\"-\"", "-")]
    [InlineData("0", "#,##0.00;(#,##0.00)", "0.00")]
    [InlineData("5", "#,##0.00;(#,##0.00);\"-\"", "5.00")]
    [InlineData("-0.001", "0.00;(0.00)", "(0.00)")]
    [InlineData("1", "0;0;0;@", null)]
    [InlineData("1", "\"x\";0", null)]
    public void Sections_select_by_sign(string value, string code, string? expected)
        => Assert.Equal(expected, FormatCodes.FormatNumber(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture), code));

    [Theory]
    [InlineData("General")]
    [InlineData("#,##0.00E+00")]
    [InlineData("0.00e-0")]
    [InlineData("@")]
    [InlineData("0 0")]
    [InlineData("0\"unterminated")]
    [InlineData("0\\")]
    [InlineData("0_")]
    [InlineData("[>100]0")]
    [InlineData("[Red")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0.0,0")]
    [InlineData("0.0000000000000000000000000000000")]
    public void Unsupported_codes_are_rejected(string code)
        => Assert.Null(FormatCodes.FormatNumber(1234.567m, code));

    [Fact]
    public void Over_long_masks_are_rejected()
    {
        var code = "0" + new string('0', FormatCodes.MaxLength);
        Assert.Null(FormatCodes.FormatNumber(1m, code));
        Assert.NotNull(FormatCodes.FormatNumber(1m, code[..FormatCodes.MaxLength]));
    }

    [Fact]
    public void Scaled_overflow_yields_null()
        => Assert.Null(FormatCodes.FormatNumber(decimal.MaxValue, "0%"));

    private static readonly DateTime Sample = new(2026, 8, 7, 14, 30, 45);

    [Theory]
    [InlineData("yyyy-mm-dd", "2026-08-07")]
    [InlineData("yyyy-mm-dd hh:mm", "2026-08-07 14:30")]
    [InlineData("yyyy-mm-dd hh:mm:ss", "2026-08-07 14:30:45")]
    [InlineData("YYYY-MM-DD HH:MM:SS", "2026-08-07 14:30:45")]
    [InlineData("h:mm AM/PM", "2:30 PM")]
    [InlineData("hh:mm am/pm", "02:30 pm")]
    [InlineData("h:mm A/P", "2:30 P")]
    [InlineData("h:mm a/p", "2:30 p")]
    [InlineData("hh:mm:ss", "14:30:45")]
    [InlineData("h:m:s", "14:30:45")]
    [InlineData("mm/dd/yyyy", "08/07/2026")]
    [InlineData("dd/mm/yy", "07/08/26")]
    [InlineData("m/d/yyyy", "8/7/2026")]
    [InlineData("mmm d, yyyy", "Aug 7, 2026")]
    [InlineData("mmmm d, yyyy", "August 7, 2026")]
    [InlineData("mmmmm", "A")]
    [InlineData("ddd", "Fri")]
    [InlineData("dddd, mmmm d, yyyy", "Friday, August 7, 2026")]
    [InlineData("mmm d, yyyy h:mm AM/PM", "Aug 7, 2026 2:30 PM")]
    [InlineData("yyyy\"年\"m\"月\"d\"日\"", "2026年8月7日")]
    [InlineData("d\\.m\\.yyyy", "7.8.2026")]
    [InlineData("mm:ss", "30:45")]
    [InlineData("h \"h\" mm \"min\"", "14 h 30 min")]
    [InlineData("yyyy", "2026")]
    [InlineData("yy", "26")]
    public void Date_tokens_render(string code, string expected)
        => Assert.Equal(expected, FormatCodes.FormatDate(Sample, code));

    [Theory]
    [InlineData("h:mm AM/PM", "12:05 AM")]
    [InlineData("hh:mm", "00:05")]
    [InlineData("h:mm", "0:05")]
    public void Midnight_reads_as_twelve_only_on_a_twelve_hour_clock(string code, string expected)
        => Assert.Equal(expected, FormatCodes.FormatDate(new DateTime(2026, 1, 1, 0, 5, 0), code));

    [Theory]
    [InlineData("h:mm AM/PM", "12:15 PM")]
    public void Noon_is_twelve_pm(string code, string expected)
        => Assert.Equal(expected, FormatCodes.FormatDate(new DateTime(2026, 1, 1, 12, 15, 0), code));

    [Theory]
    [InlineData("[h]:mm")]
    [InlineData("yyyy-mm-dd Q")]
    [InlineData("yyyyy")]
    [InlineData("hhh")]
    [InlineData("mmmmmm")]
    [InlineData("\"only text\"")]
    [InlineData("")]
    [InlineData("yyyy\"open")]
    public void Unsupported_date_codes_are_rejected(string code)
        => Assert.Null(FormatCodes.FormatDate(Sample, code));
}
