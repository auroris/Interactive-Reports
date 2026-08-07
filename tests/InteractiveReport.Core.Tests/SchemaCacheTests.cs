using InteractiveReport.Core.Model;
using InteractiveReport.Core.Schema;

namespace InteractiveReport.Core.Tests;

public sealed class SchemaCacheTests
{
    [Fact]
    public async Task Same_definition_discovers_once_but_changed_sql_gets_a_new_schema()
    {
        var cache = new SchemaCache();
        var definition = TestFixtures.OrdersDefinition(ReportDialect.Sqlite);
        var discoveries = 0;

        Task<ReportSchema> Discover()
        {
            discoveries++;
            return Task.FromResult(ReportSchema.Create(
                definition.Name,
                [TestFixtures.Col($"COL_{discoveries}", typeof(string))]));
        }

        var first = await cache.GetOrDiscover(definition, Discover);
        var cached = await cache.GetOrDiscover(definition, Discover);
        definition.Sql += " WHERE 1 = 1";
        var changed = await cache.GetOrDiscover(definition, Discover);

        Assert.Same(first, cached);
        Assert.NotSame(first, changed);
        Assert.Equal(2, discoveries);
    }

    [Fact]
    public async Task Failed_discovery_is_evicted_for_retry()
    {
        var cache = new SchemaCache();
        var definition = TestFixtures.OrdersDefinition(ReportDialect.Sqlite);
        var attempts = 0;

        async Task<ReportSchema> Discover()
        {
            await Task.Yield();
            if (++attempts == 1) throw new InvalidOperationException("database unavailable");
            return ReportSchema.Create(definition.Name, TestFixtures.OrdersSchema);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetOrDiscover(definition, Discover));
        var schema = await cache.GetOrDiscover(definition, Discover);

        Assert.Equal(TestFixtures.OrdersSchema.Count, schema.Count);
        Assert.Equal(2, attempts);
    }
}
