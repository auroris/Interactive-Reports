using System.Collections.Concurrent;
using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Schema;

/// <summary>
/// Per-report schema cache. Discovery runs once per definition; Clear/Remove exist for
/// configuration-reload invalidation at the host layer.
/// </summary>
public sealed class SchemaCache
{
    private readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlyList<ColumnModel>>>> _cache
        = new(StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<ColumnModel>> GetOrDiscover(
        string reportName,
        Func<Task<IReadOnlyList<ColumnModel>>> discover)
    {
        var lazy = _cache.GetOrAdd(reportName, _ => new Lazy<Task<IReadOnlyList<ColumnModel>>>(
            () => WithEviction(reportName, discover)));
        return lazy.Value;
    }

    /// <summary>A failed discovery must not be cached forever — evict so the next request retries.</summary>
    private async Task<IReadOnlyList<ColumnModel>> WithEviction(
        string reportName,
        Func<Task<IReadOnlyList<ColumnModel>>> discover)
    {
        try
        {
            return await discover();
        }
        catch
        {
            _cache.TryRemove(reportName, out _);
            throw;
        }
    }

    public void Remove(string reportName) => _cache.TryRemove(reportName, out _);

    public void Clear() => _cache.Clear();
}
