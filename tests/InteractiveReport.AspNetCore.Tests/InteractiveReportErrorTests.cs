using System.Reflection;
using System.Text.RegularExpressions;

namespace InteractiveReport.AspNetCore.Tests;

public sealed class InteractiveReportErrorTests
{
    [Fact]
    public void Public_error_codes_are_unique_ORA_style_message_identities()
    {
        var codes = typeof(InteractiveReportErrorCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => Assert.IsType<string>(field.GetRawConstantValue()))
            .ToArray();

        Assert.NotEmpty(codes);
        Assert.Equal(codes.Length, codes.Distinct(StringComparer.Ordinal).Count());
        Assert.All(codes, code =>
        {
            Assert.Matches(new Regex("^IR-[0-9]{4}$", RegexOptions.CultureInvariant), code);
            var (title, description) = InteractiveReportErrorCatalog.Find(code);
            Assert.False(string.IsNullOrWhiteSpace(title));
            Assert.False(string.IsNullOrWhiteSpace(description));
        });
    }
}
