using System.Collections.Concurrent;
using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Schema;

/// <summary>
/// Per-report schema cache. Discovery runs once per definition; Clear/Remove exist for
/// configuration-reload invalidation at the host layer.
/// </summary>
public sealed class SchemaCache
{
    private readonly ConcurrentDictionary<SchemaCacheKey, Lazy<Task<ReportSchema>>> _cache = new();

    public Task<ReportSchema> GetOrDiscover(
        ReportDefinition definition,
        Func<Task<ReportSchema>> discover)
    {
        var key = SchemaCacheKey.From(definition);
        var lazy = _cache.GetOrAdd(key, _ => new Lazy<Task<ReportSchema>>(
            () => WithEviction(key, discover)));
        return lazy.Value;
    }

    /// <summary>A failed discovery must not be cached forever — evict so the next request retries.</summary>
    private async Task<ReportSchema> WithEviction(
        SchemaCacheKey key,
        Func<Task<ReportSchema>> discover)
    {
        try
        {
            return await discover();
        }
        catch
        {
            _cache.TryRemove(key, out _);
            throw;
        }
    }

    public void Remove(string reportName)
    {
        foreach (var key in _cache.Keys.Where(
                     key => string.Equals(key.ReportName, reportName, StringComparison.OrdinalIgnoreCase)))
            _cache.TryRemove(key, out _);
    }

    public void Clear() => _cache.Clear();

    private sealed record SchemaCacheKey(
        string ReportName,
        string Connection,
        ReportDialect Dialect,
        string Sql,
        string ContextSignature)
    {
        public static SchemaCacheKey From(ReportDefinition definition) => new(
            definition.Name.ToUpperInvariant(),
            definition.Connection,
            definition.GetEffectiveDialect(),
            definition.Sql,
            string.Join(
                "\n",
                (definition.ContextParams ?? [])
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => $"{pair.Key.ToUpperInvariant()}\0{pair.Value?.Claim}")));
    }
}
