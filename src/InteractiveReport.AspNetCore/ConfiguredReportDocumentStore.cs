using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InteractiveReport.Core.Model;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace InteractiveReport.AspNetCore;

/// <summary>
/// Loads source-controlled report documents referenced by configured definitions.
/// File contents are cached until their length or last-write timestamp changes.
/// </summary>
public sealed class ConfiguredReportDocumentStore : IDisposable
{
    internal const string IdPrefix = "cfg_";

    private readonly IOptionsMonitor<InteractiveReportOptions> _options;
    private readonly string _contentRoot;
    private readonly ConcurrentDictionary<string, CachedFile> _cache;
    private readonly IDisposable? _reloadSubscription;

    public ConfiguredReportDocumentStore(
        IOptionsMonitor<InteractiveReportOptions> options,
        IHostEnvironment environment)
    {
        _options = options;
        _contentRoot = environment.ContentRootPath;
        _cache = new ConcurrentDictionary<string, CachedFile>(PathComparer);
        _reloadSubscription = options.OnChange(_ => _cache.Clear());
    }

    internal IReadOnlyList<ConfiguredReportDocument> List(ReportDefinition definition)
        => Load(definition.Name, definition.DocumentFiles);

    internal IReadOnlyList<ConfiguredReportDocument> List(string reportName)
        => _options.CurrentValue.Reports.TryGetValue(reportName, out var definition)
            ? Load(reportName, definition.DocumentFiles)
            : [];

    internal IReadOnlyList<ConfiguredReportDocument> ListAll()
    {
        var documents = new List<ConfiguredReportDocument>();
        foreach (var (reportName, definition) in _options.CurrentValue.Reports)
            documents.AddRange(Load(reportName, definition.DocumentFiles));
        return documents;
    }

    internal ConfiguredReportDocument? Find(string id)
    {
        if (!id.StartsWith(IdPrefix, StringComparison.Ordinal)) return null;

        foreach (var (reportName, definition) in _options.CurrentValue.Reports)
        {
            foreach (var configuredPath in definition.DocumentFiles ?? [])
            {
                if (string.IsNullOrWhiteSpace(configuredPath)) continue;
                var fullPath = ResolvePath(configuredPath);
                if (!string.Equals(DocumentId(reportName, fullPath), id, StringComparison.Ordinal)) continue;
                return Load(reportName, definition.DocumentFiles).Single(document => document.Id == id);
            }
        }

        return null;
    }

    private IReadOnlyList<ConfiguredReportDocument> Load(
        string reportName,
        IReadOnlyCollection<string>? configuredPaths)
    {
        if (configuredPaths is null || configuredPaths.Count == 0) return [];

        var documents = new List<ConfiguredReportDocument>(configuredPaths.Count);
        var paths = new HashSet<string>(PathComparer);
        var titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var configuredPath in configuredPaths)
        {
            if (string.IsNullOrWhiteSpace(configuredPath))
                throw new InvalidOperationException(
                    $"Report '{reportName}': documentFiles contains a blank path.");

            var fullPath = ResolvePath(configuredPath);
            if (!paths.Add(fullPath))
                throw new InvalidOperationException(
                    $"Report '{reportName}': documentFiles references '{configuredPath}' more than once.");

            var source = Read(reportName, configuredPath, fullPath);
            if (!titles.Add(source.Title))
                throw new InvalidOperationException(
                    $"Report '{reportName}': configured report document title '{source.Title}' is duplicated (titles are case-insensitive).");
            var state = JsonSerializer.Deserialize<ReportState>(source.StateJson, IrJson.Options)
                ?? throw new InvalidOperationException(
                    $"Report '{reportName}': document file '{configuredPath}' has no state.");
            documents.Add(new ConfiguredReportDocument(
                DocumentId(reportName, fullPath),
                reportName,
                source.Title,
                source.Primary,
                state,
                source.StateJson,
                source.ModifiedUtc,
                source.Length));
        }

        return documents;
    }

    private CachedFile Read(string reportName, string configuredPath, string fullPath)
    {
        var info = new FileInfo(fullPath);
        if (!info.Exists)
            throw new InvalidOperationException(
                $"Report '{reportName}': document file '{configuredPath}' was not found at '{fullPath}'.");

        var modifiedUtc = info.LastWriteTimeUtc;
        var length = info.Length;
        if (_cache.TryGetValue(fullPath, out var cached)
            && cached.ModifiedUtc == modifiedUtc
            && cached.Length == length)
            return cached;

        ReportDocumentFile? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<ReportDocumentFile>(File.ReadAllText(fullPath), IrJson.Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Report '{reportName}': document file '{configuredPath}' is not valid JSON: {ex.Message}", ex);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                $"Report '{reportName}': document file '{configuredPath}' could not be read.", ex);
        }

        if (string.IsNullOrWhiteSpace(envelope?.Title) || envelope.Title.Trim().Length > 200)
            throw new InvalidOperationException(
                $"Report '{reportName}': document file '{configuredPath}' requires a title of 1–200 characters.");
        if (envelope.State is null)
            throw new InvalidOperationException(
                $"Report '{reportName}': document file '{configuredPath}' requires a state object.");

        var loaded = new CachedFile(
            envelope.Title.Trim(),
            envelope.Primary,
            JsonSerializer.Serialize(envelope.State, IrJson.Options),
            modifiedUtc,
            length);
        _cache[fullPath] = loaded;
        return loaded;
    }

    private string ResolvePath(string configuredPath)
        => Path.GetFullPath(Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(_contentRoot, configuredPath));

    private static string DocumentId(string reportName, string fullPath)
    {
        var pathIdentity = OperatingSystem.IsWindows() ? fullPath.ToUpperInvariant() : fullPath;
        var bytes = Encoding.UTF8.GetBytes(reportName.ToUpperInvariant() + "\n" + pathIdentity);
        return IdPrefix + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static StringComparer PathComparer
        => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public void Dispose() => _reloadSubscription?.Dispose();

    private sealed record CachedFile(
        string Title,
        bool Primary,
        string StateJson,
        DateTime ModifiedUtc,
        long Length);
}

internal sealed record ConfiguredReportDocument(
    string Id,
    string ReportName,
    string Title,
    bool Primary,
    ReportState State,
    string StateJson,
    DateTime ModifiedUtc,
    long Length);
