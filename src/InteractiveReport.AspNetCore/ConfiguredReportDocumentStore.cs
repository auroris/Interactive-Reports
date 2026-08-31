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
    /// <summary>The prefix that distinguishes deterministic configured-document ids from user saved-report ids.</summary>
    internal const string IdPrefix = "cfg_";

    private readonly IOptionsMonitor<InteractiveReportOptions> _options;
    private readonly string _contentRoot;
    private readonly ConcurrentDictionary<string, CachedFile> _cache;
    private readonly IDisposable? _reloadSubscription;

    /// <summary>
    /// Creates a document store rooted at the host content directory and invalidated by option reloads.
    /// </summary>
    /// <param name="options">The monitored Interactive Reports configuration that declares document files.</param>
    /// <param name="environment">The host environment used to locate packaged or configured files.</param>
    /// <remarks>Subscribes to option changes and clears the file cache whenever configuration reloads.</remarks>
    public ConfiguredReportDocumentStore(
        IOptionsMonitor<InteractiveReportOptions> options,
        IHostEnvironment environment)
    {
        _options = options;
        _contentRoot = environment.ContentRootPath;
        _cache = new ConcurrentDictionary<string, CachedFile>(PathComparer);
        _reloadSubscription = options.OnChange(_ => _cache.Clear());
    }

    /// <summary>
    /// Loads the configured documents associated with a report definition.
    /// </summary>
    /// <param name="definition">The report definition containing document-file declarations.</param>
    /// <returns>The configured documents in declaration order.</returns>
    internal IReadOnlyList<ConfiguredReportDocument> List(ReportDefinition definition)
        => Load(definition.Name, definition.DocumentFiles);

    /// <summary>
    /// Loads the configured documents associated with a report name.
    /// </summary>
    /// <param name="reportName">The configured report name whose definition or saved reports are being addressed.</param>
    /// <returns>The configured documents in declaration order, or an empty list for an unknown report.</returns>
    internal IReadOnlyList<ConfiguredReportDocument> List(string reportName)
        => _options.CurrentValue.Reports.TryGetValue(reportName, out var definition)
            ? Load(reportName, definition.DocumentFiles)
            : [];

    /// <summary>
    /// Lists every configured report document in deterministic order.
    /// </summary>
    /// <returns>Every configured document ordered first by report configuration and then file declaration.</returns>
    internal IReadOnlyList<ConfiguredReportDocument> ListAll()
    {
        var documents = new List<ConfiguredReportDocument>();
        foreach (var (reportName, definition) in _options.CurrentValue.Reports)
            documents.AddRange(Load(reportName, definition.DocumentFiles));
        return documents;
    }

    /// <summary>
    /// Finds a configured document by its stable <c>cfg_</c> identifier.
    /// </summary>
    /// <param name="id">The case-sensitive deterministic configured-document id.</param>
    /// <returns>The loaded document, or <see langword="null"/> when no configured path produces the id.</returns>
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

    /// <summary>
    /// Validates and loads one report's configured document-file collection in declaration order.
    /// </summary>
    /// <param name="reportName">The configured report name used for ids and diagnostics.</param>
    /// <param name="configuredPaths">The authoritative configured-document paths retained during reconciliation.</param>
    /// <returns>Loaded documents in configured path order.</returns>
    /// <exception cref="InvalidOperationException">Thrown for blank or duplicate paths, duplicate titles, invalid files, or missing state.</exception>
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

    /// <summary>
    /// Reads and deserializes one configured report document.
    /// </summary>
    /// <param name="reportName">The configured report name used in diagnostics.</param>
    /// <param name="configuredPath">The path as written in configuration.</param>
    /// <param name="fullPath">The absolute path used for file access and cache identity.</param>
    /// <returns>A cached or newly parsed immutable file snapshot.</returns>
    /// <remarks>Reads the file and replaces its cache entry when length or last-write time changes.</remarks>
    /// <exception cref="InvalidOperationException">Thrown when the file is absent, unreadable, malformed, or lacks a valid title or state.</exception>
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

    /// <summary>
    /// Resolves a configured path against the host content root and normalizes it to an absolute path.
    /// </summary>
    /// <param name="configuredPath">The absolute or content-root-relative configured path.</param>
    /// <returns>The absolute normalized document path.</returns>
    private string ResolvePath(string configuredPath)
        => Path.GetFullPath(Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(_contentRoot, configuredPath));

    /// <summary>
    /// Builds the stable identifier for a configured report document.
    /// </summary>
    /// <param name="reportName">The configured report name.</param>
    /// <param name="fullPath">The normalized absolute path.</param>
    /// <returns>The stable identifier assigned to the configured document.</returns>
    private static string DocumentId(string reportName, string fullPath)
    {
        var pathIdentity = OperatingSystem.IsWindows() ? fullPath.ToUpperInvariant() : fullPath;
        var bytes = Encoding.UTF8.GetBytes(reportName.ToUpperInvariant() + "\n" + pathIdentity);
        return IdPrefix + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    /// <summary>Gets the platform-appropriate comparer used for normalized file paths.</summary>
    private static StringComparer PathComparer
        => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// Unsubscribes from configuration reload notifications.
    /// </summary>
    public void Dispose() => _reloadSubscription?.Dispose();

    /// <summary>Contains a parsed configured-document file plus the metadata used to validate its cache entry.</summary>
    private sealed record CachedFile(
        string Title,
        bool Primary,
        string StateJson,
        DateTime ModifiedUtc,
        long Length);
}

/// <summary>Contains one loaded configured document and both its typed and canonical JSON state.</summary>
/// <param name="Id">The deterministic id derived from report name and normalized path.</param>
/// <param name="ReportName">The configured report that owns the document.</param>
/// <param name="Title">The validated display title.</param>
/// <param name="Primary">The source-controlled initial primary flag.</param>
/// <param name="State">The detached typed report state.</param>
/// <param name="StateJson">The state serialized with the protocol options.</param>
/// <param name="ModifiedUtc">The source file's last-write timestamp.</param>
/// <param name="Length">The source file length used for cache invalidation.</param>
internal sealed record ConfiguredReportDocument(
    string Id,
    string ReportName,
    string Title,
    bool Primary,
    ReportState State,
    string StateJson,
    DateTime ModifiedUtc,
    long Length);
