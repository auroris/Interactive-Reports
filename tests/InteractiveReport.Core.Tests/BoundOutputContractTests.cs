using System.Collections.Immutable;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Planning;
using InteractiveReport.Core.Schema;

namespace InteractiveReport.Core.Tests;

public sealed class BoundOutputContractTests
{
    private static readonly ReportSchema Schema = ReportSchema.Create(
        "orders",
        [
            new ColumnModel
            {
                Name = "CUSTOMER",
                Label = "Customer",
                ClrType = typeof(string),
                IsNullable = false,
            },
            new ColumnModel
            {
                Name = "AMOUNT",
                Label = "Amount",
                ClrType = typeof(decimal),
            },
        ]);

    [Fact]
    public void Contract_preserves_order_and_rejects_case_insensitive_duplicates()
    {
        var contract = BoundOutputContract.FromSchema("orders", Schema);

        Assert.Equal(["CUSTOMER", "AMOUNT"],
            contract.Columns.Select(column => column.LogicalId));
        Assert.Equal("AMOUNT", contract.GetRequired("amount").LogicalId);

        var duplicate = contract.Columns.Add(
            BoundColumnContract.FromColumn(new ColumnModel
            {
                Name = "amount",
                Label = "Other amount",
                ClrType = typeof(decimal),
            }));
        var error = Assert.Throws<InvalidOperationException>(() =>
            BoundOutputContract.Create("invalid", duplicate));
        Assert.Contains("duplicate logical id", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Metadata_and_child_boundary_are_immutable_and_channel_aware()
    {
        var original = BoundOutputContract.FromSchema(
            "orders",
            Schema,
            labels: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["AMOUNT"] = "Inherited amount",
            },
            formats: new Dictionary<string, CanonicalColumnFormat>(StringComparer.OrdinalIgnoreCase)
            {
                ["AMOUNT"] = Format(
                    mask: "currency:USD",
                    bold: true,
                    displayAs: "link",
                    urlColumn: "CUSTOMER"),
            },
            formatSources: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["AMOUNT"] = "AMOUNT",
            });
        var metadata = new CanonicalMetadata(
            ClearsInheritedLabels: true,
            Labels: ImmutableDictionary.CreateRange(
                StringComparer.OrdinalIgnoreCase,
                [new KeyValuePair<string, string>("CUSTOMER", "Account")]),
            ClearsInheritedFormats: false,
            Formats: ImmutableDictionary.CreateRange(
                StringComparer.OrdinalIgnoreCase,
                [new KeyValuePair<string, CanonicalColumnFormat>(
                    "AMOUNT",
                    Format("decimal-2", italic: true))]));

        var effective = original.ApplyMetadata(metadata);
        var child = effective.ForChild("child");

        Assert.Equal("Inherited amount", original.GetRequired("AMOUNT").EffectiveLabel);
        Assert.True(original.GetRequired("AMOUNT").LocalFormat!.Bold);
        Assert.Equal("Account", effective.GetRequired("CUSTOMER").EffectiveLabel);
        Assert.Equal("Amount", effective.GetRequired("AMOUNT").EffectiveLabel);
        Assert.True(effective.GetRequired("AMOUNT").LocalFormat!.Italic);
        Assert.Equal("decimal-2", effective.GetRequired("AMOUNT").ExportedMask);
        Assert.Equal("AMOUNT", effective.GetRequired("AMOUNT").FormatSourceLogicalId);

        var inherited = child.GetRequired("AMOUNT").LocalFormat!;
        Assert.Equal("decimal-2", inherited.Mask);
        Assert.Null(inherited.Italic);
        Assert.Null(inherited.Bold);
        Assert.Null(inherited.DisplayAs);
        Assert.Null(inherited.UrlColumn);
        Assert.Equal("child", child.Name);
    }

    [Fact]
    public void Metadata_clear_removes_mask_and_format_lineage()
    {
        var original = BoundOutputContract.FromSchema(
            "orders",
            Schema,
            formats: new Dictionary<string, CanonicalColumnFormat>(StringComparer.OrdinalIgnoreCase)
            {
                ["AMOUNT"] = Format("currency:USD"),
            },
            formatSources: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["AMOUNT"] = "AMOUNT",
            });
        var cleared = original.ApplyMetadata(new CanonicalMetadata(
            false,
            ImmutableDictionary.Create<string, string>(StringComparer.OrdinalIgnoreCase),
            true,
            ImmutableDictionary.Create<string, CanonicalColumnFormat>(StringComparer.OrdinalIgnoreCase)));

        var amount = cleared.GetRequired("AMOUNT");
        Assert.Null(amount.LocalFormat);
        Assert.Null(amount.ExportedMask);
        Assert.Null(amount.FormatSourceLogicalId);
        Assert.Equal("currency:USD", original.GetRequired("AMOUNT").ExportedMask);
    }

    [Fact]
    public void Typed_pivot_key_equality_is_type_aware_and_binary_content_based()
    {
        var first = BoundPivotTypedKey.Create([1, "1", new byte[] { 1, 2, 3 }]);
        var equal = BoundPivotTypedKey.Create([1, "1", new byte[] { 1, 2, 3 }]);
        var differentType = BoundPivotTypedKey.Create(["1", "1", new byte[] { 1, 2, 3 }]);

        Assert.Equal(first, equal);
        Assert.Equal(first.GetHashCode(), equal.GetHashCode());
        Assert.NotEqual(first, differentType);
        Assert.Equal(first.CanonicalIdentity, equal.CanonicalIdentity);
    }

    private static CanonicalColumnFormat Format(
        string? mask,
        bool? bold = null,
        bool? italic = null,
        string? displayAs = null,
        string? urlColumn = null)
        => new(
            mask,
            null,
            bold,
            italic,
            null,
            null,
            [],
            displayAs,
            urlColumn,
            null,
            null,
            null);
}
