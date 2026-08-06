using InteractiveReport.Core.Schema;

namespace InteractiveReport.Core.Tests;

public sealed class ReportSchemaTests
{
    [Fact]
    public void Create_rejects_case_insensitive_duplicate_aliases()
    {
        var error = Assert.Throws<InvalidOperationException>(() => ReportSchema.Create(
            "orders",
            [TestFixtures.Col("TOTAL", typeof(decimal)), TestFixtures.Col("total", typeof(decimal))]));

        Assert.Contains("duplicate column alias 'total'", error.Message);
    }

    [Fact]
    public void Extend_preserves_order_and_uses_case_insensitive_lookup()
    {
        var schema = ReportSchema.Create("orders", TestFixtures.OrdersSchema)
            .Extend("orders", [TestFixtures.Col("c1", typeof(decimal))]);

        Assert.Equal("ORDER_ID", schema[0].Name);
        Assert.True(schema.TryGetValue("C1", out var computed));
        Assert.Equal("c1", computed.Name);
    }
}
