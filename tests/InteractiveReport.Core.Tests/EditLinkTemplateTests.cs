using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Tests;

public class EditLinkTemplateTests
{
    [Fact]
    public void Placeholders_extract_in_order_and_dedupe_case_insensitively()
    {
        var names = EditLinkTemplate.Parse("/orders/{ORDER_ID}/edit?region={REGION}&again={order_id}", out var error);

        Assert.Null(error);
        Assert.Equal(["ORDER_ID", "REGION"], names);
    }

    [Fact]
    public void A_template_without_placeholders_parses_to_an_empty_list()
    {
        var names = EditLinkTemplate.Parse("/orders/edit", out var error);

        Assert.Null(error);
        Assert.Empty(names!);
    }

    [Theory]
    [InlineData("/orders/{ORDER_ID/edit", "'{' without a matching '}'")]
    [InlineData("/orders/ORDER_ID}/edit", "'}' without a matching '{'")]
    [InlineData("/orders/{}/edit", "empty placeholder")]
    [InlineData("/orders/{  }/edit", "empty placeholder")]
    [InlineData("/orders/{A{B}}/edit", "nested '{'")]
    public void Malformed_templates_return_a_precise_error(string template, string expected)
    {
        var names = EditLinkTemplate.Parse(template, out var error);

        Assert.Null(names);
        Assert.Contains(expected, error);
    }

    [Fact]
    public void Rewrite_maps_each_placeholder_and_keeps_literal_text()
    {
        var rewritten = EditLinkTemplate.Rewrite(
            "/orders/{order_id}/edit?r={region}",
            name => name.ToUpperInvariant());

        Assert.Equal("/orders/{ORDER_ID}/edit?r={REGION}", rewritten);
    }
}
