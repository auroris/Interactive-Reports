using InteractiveReport.Core.Planning;

namespace InteractiveReport.Core.Tests;

public sealed class DynamicPivotColumnIdentityRegistryTests
{
    [Fact]
    public void Identity_is_stable_across_registries_and_discovery_order()
    {
        var first = new DynamicPivotColumnIdentityRegistry(["ir1", "ir2"]);
        var shipped = first.Register("matrix", "ir2", ["SHIPPED", 2026]);
        _ = first.Register("matrix", "ir2", ["PENDING", 2026]);

        var second = new DynamicPivotColumnIdentityRegistry(["ir1", "ir2"]);
        _ = second.Register("matrix", "ir2", ["PENDING", 2026]);
        var rediscovered = second.Register("matrix", "ir2", ["SHIPPED", 2026]);

        Assert.Equal(shipped, rediscovered);
        Assert.Matches("^ir[1-9][0-9]*$", shipped);
    }

    [Fact]
    public void Adding_a_key_does_not_rename_existing_cells()
    {
        var before = new DynamicPivotColumnIdentityRegistry([]);
        var existing = before.Register("matrix", "__count", ["B"]);

        var after = new DynamicPivotColumnIdentityRegistry([]);
        _ = after.Register("matrix", "__count", ["A"]);
        Assert.Equal(existing, after.Register("matrix", "__count", ["B"]));
    }

    [Fact]
    public void Typed_keys_and_metric_families_have_distinct_identities()
    {
        var registry = new DynamicPivotColumnIdentityRegistry([]);

        var text = registry.Register("matrix", "ir1", ["1"]);
        var number = registry.Register("matrix", "ir1", [1]);
        var otherMetric = registry.Register("matrix", "ir2", ["1"]);

        Assert.Equal(3, new[] { text, number, otherMetric }.Distinct().Count());
        Assert.NotEqual(
            BoundPivotTypedKey.Create(["1"]).CanonicalIdentity,
            BoundPivotTypedKey.Create([1]).CanonicalIdentity);
    }

    [Fact]
    public void Owning_table_and_full_typed_key_are_part_of_identity()
    {
        var registry = new DynamicPivotColumnIdentityRegistry([]);
        var identities = new[]
        {
            registry.Register("north", "ir1", ["1"]),
            registry.Register("south", "ir1", ["1"]),
            registry.Register("north", "ir1", [null]),
            registry.Register("north", "ir1", [1.0m]),
            registry.Register("north", "ir1", [new DateOnly(2026, 8, 29)]),
            registry.Register("north", "ir1", [new byte[] { 1, 2, 3 }]),
            registry.Register("north", "ir1", ["1", 1.0m]),
        };

        Assert.Equal(identities.Length, identities.Distinct().Count());
    }

    [Fact]
    public void Authored_collision_is_resolved_deterministically()
    {
        var probe = new DynamicPivotColumnIdentityRegistry([]);
        var reserved = probe.Register("matrix", "ir1", ["SHIPPED"]);

        var first = new DynamicPivotColumnIdentityRegistry([reserved]);
        var second = new DynamicPivotColumnIdentityRegistry([reserved]);

        var reassigned = first.Register("matrix", "ir1", ["SHIPPED"]);
        Assert.NotEqual(reserved, reassigned);
        Assert.Equal(reassigned, second.Register("matrix", "ir1", ["SHIPPED"]));
    }
}
