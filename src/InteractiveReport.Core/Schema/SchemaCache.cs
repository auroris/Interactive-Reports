using System.Collections.Concurrent;
using InteractiveReport.Core.Model;
using Microsoft.Extensions.Logging;

namespace InteractiveReport.Core.Schema;

/// <summary>
/// Per-report schema cache. Discovery runs once per definition; Clear/Remove exist for
/// configuration-reload invalidation at the host layer.
/// </summary>
public sealed class SchemaCache
{
    private readonly ConcurrentDictionary<SchemaCacheKey, Lazy<Task<ReportSchema>>> _cache = new();
    private readonly ILogger? _logger;

    public SchemaCache() : this(logger: null)
    {
    }

    public SchemaCache(ILogger<SchemaCache>? logger) => _logger = logger;

    public Task<ReportSchema> GetOrDiscover(
        ReportDefinition definition,
        Func<Task<ReportSchema>> discover)
    {
        var key = SchemaCacheKey.From(definition);
        var candidate = new Lazy<Task<ReportSchema>>(
            () => WithEviction(key, discover));
        var lazy = _cache.GetOrAdd(key, candidate);
        _logger?.LogDebug(
            ReferenceEquals(lazy, candidate)
                ? "Discovering schema for report {Report}"
                : "Using cached schema for report {Report}",
            definition.Name);
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
            _logger?.LogDebug("Evicted failed schema discovery for report {Report}", key.ReportName);
            throw;
        }
    }

    public void Remove(string reportName)
    {
        var removed = 0;
        foreach (var key in _cache.Keys.Where(
                     key => string.Equals(key.ReportName, reportName, StringComparison.OrdinalIgnoreCase)))
            if (_cache.TryRemove(key, out _)) removed++;
        _logger?.LogDebug(
            "Removed {SchemaCount} cached schemas for report {Report}",
            removed,
            reportName);
    }

    public void Clear()
    {
        var count = _cache.Count;
        _cache.Clear();
        _logger?.LogDebug("Cleared {SchemaCount} cached report schemas", count);
    }

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
