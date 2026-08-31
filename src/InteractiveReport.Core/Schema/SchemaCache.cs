using System.Collections.Concurrent;
using InteractiveReport.Core.Model;
using Microsoft.Extensions.Logging;

namespace InteractiveReport.Core.Schema;

/// <summary>
/// Caches one discovery task per effective report definition. <see cref="Clear"/> and <see cref="Remove"/> support
/// configuration-reload invalidation at the host layer.
/// </summary>
public sealed class SchemaCache
{
    private readonly ConcurrentDictionary<SchemaCacheKey, Lazy<Task<ReportSchema>>> _cache = new();
    private readonly ILogger? _logger;

    /// <summary>
    /// Creates a schema cache with logging disabled.
    /// </summary>
    public SchemaCache() : this(logger: null)
    {
    }

    /// <summary>
    /// Creates a schema cache with an optional diagnostic logger.
    /// </summary>
    /// <param name="logger">The host-provided logger that receives diagnostic events; <see langword="null"/> disables logging.</param>
    public SchemaCache(ILogger<SchemaCache>? logger) => _logger = logger;

    /// <summary>
    /// Returns the shared discovery task for an effective definition, starting it on the first request.
    /// </summary>
    /// <param name="definition">The definition whose schema-affecting fields form the cache key.</param>
    /// <param name="discover">The asynchronous factory used to discover an uncached schema.</param>
    /// <returns>The existing or newly created schema-discovery task.</returns>
    /// <remarks>Stores a lazy task and emits a cache-hit or discovery log event.</remarks>
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

    /// <summary>
    /// Runs discovery and evicts the key on failure so the next request can retry.
    /// </summary>
    /// <param name="key">The definition-specific schema cache key.</param>
    /// <param name="discover">The asynchronous factory used to discover an uncached schema.</param>
    /// <returns>A task containing the discovered schema.</returns>
    /// <remarks>Removes <paramref name="key"/> and logs when <paramref name="discover"/> fails.</remarks>
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

    /// <summary>
    /// Removes every cached definition variant for one report name.
    /// </summary>
    /// <param name="reportName">The case-insensitive configured report name to invalidate.</param>
    /// <remarks>Mutates the cache and logs the number of removed entries.</remarks>
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

    /// <summary>
    /// Removes every cached schema-discovery task.
    /// </summary>
    /// <remarks>Mutates the cache and logs the number of removed entries.</remarks>
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
        /// <summary>Creates a key from every definition field that can change discovery SQL, bindings, or provider metadata.</summary>
        /// <param name="definition">The resolved report definition to fingerprint.</param>
        /// <returns>A case-stabilized key for schema-affecting definition content.</returns>
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
