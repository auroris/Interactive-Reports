using System.Collections.Concurrent;
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
    /// Lists configured file references without probing the filesystem. Existing database
    /// identities are the optimistic catalogue authority; the file body is dereferenced only
    /// when a document is retrieved.
    /// </summary>
    internal IReadOnlyList<ConfiguredReportDocumentReference> ListReferences()
    {
        var references = new List<ConfiguredReportDocumentReference>();
        foreach (var (reportName, definition) in _options.CurrentValue.Reports)
        {
            if (definition.DocumentFiles is not { Count: > 0 }) continue;
            var paths = new HashSet<string>(PathComparer);
            foreach (var configuredPath in definition.DocumentFiles)
            {
                if (string.IsNullOrWhiteSpace(configuredPath))
                    throw new InvalidOperationException(
                        $"Report '{reportName}': documentFiles contains a blank path.");
                var sourceFile = configuredPath.Trim();
                if (!paths.Add(ResolvePath(sourceFile)))
                    throw new InvalidOperationException(
                        $"Report '{reportName}': documentFiles references '{configuredPath}' more than once.");
                references.Add(new ConfiguredReportDocumentReference(reportName, sourceFile));
            }
        }
        return references;
    }

    /// <summary>
    /// Finds a configured document by the report family and file reference persisted in the database.
    /// </summary>
    /// <param name="reportName">The configured report family.</param>
    /// <param name="sourceFile">The file reference copied from <c>documentFiles</c>.</param>
    /// <returns>The current loaded document, or <see langword="null"/> when the referenced file is absent.</returns>
    internal ConfiguredReportDocument? Find(string reportName, string sourceFile)
    {
        try
        {
            return LoadOne(reportName, sourceFile.Trim());
        }
        catch (ConfiguredReportDocumentMissingException)
        {
            return null;
        }
    }

    /// <summary>
    /// Validates and loads one report's configured document-file collection in declaration order.
    /// </summary>
    /// <param name="reportName">The configured report name used for family identity and diagnostics.</param>
    /// <param name="configuredPaths">The authoritative configured-document paths retained during reconciliation.</param>
    /// <returns>Loaded documents in configured path order.</returns>
    /// <exception cref="InvalidOperationException">Thrown for blank or duplicate paths, invalid files, or missing state.</exception>
    private IReadOnlyList<ConfiguredReportDocument> Load(
        string reportName,
        IReadOnlyCollection<string>? configuredPaths)
    {
        if (configuredPaths is null || configuredPaths.Count == 0) return [];

        var documents = new List<ConfiguredReportDocument>(configuredPaths.Count);
        var paths = new HashSet<string>(PathComparer);
        var hasDefault = false;

        foreach (var configuredPath in configuredPaths)
        {
            if (string.IsNullOrWhiteSpace(configuredPath))
                throw new InvalidOperationException(
                    $"Report '{reportName}': documentFiles contains a blank path.");

            var sourceFile = configuredPath.Trim();
            var fullPath = ResolvePath(sourceFile);
            if (!paths.Add(fullPath))
                throw new InvalidOperationException(
                    $"Report '{reportName}': documentFiles references '{configuredPath}' more than once.");

            var document = LoadOne(reportName, sourceFile);
            if (document.Default && hasDefault)
                throw new InvalidOperationException(
                    $"Report '{reportName}': only one configured report document may be marked as default.");
            hasDefault |= document.Default;
            documents.Add(document);
        }

        return documents;
    }

    /// <summary>Loads and parses one persisted source-file reference.</summary>
    private ConfiguredReportDocument LoadOne(string reportName, string sourceFile)
    {
        var source = Read(reportName, sourceFile, ResolvePath(sourceFile));
        var state = JsonSerializer.Deserialize<ReportState>(source.StateJson, IrJson.Options)
            ?? throw new InvalidOperationException(
                $"Report '{reportName}': document file '{sourceFile}' has no state.");
        return new ConfiguredReportDocument(
            reportName,
            sourceFile,
            source.Title,
            source.Default,
            state,
            source.StateJson,
            source.ModifiedUtc,
            source.Length);
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
            throw new ConfiguredReportDocumentMissingException(
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
        catch (FileNotFoundException ex)
        {
            throw new ConfiguredReportDocumentMissingException(
                $"Report '{reportName}': document file '{configuredPath}' was not found at '{fullPath}'.",
                ex);
        }
        catch (DirectoryNotFoundException ex)
        {
            throw new ConfiguredReportDocumentMissingException(
                $"Report '{reportName}': document file '{configuredPath}' was not found at '{fullPath}'.",
                ex);
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
            envelope.Default,
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
        bool Default,
        string StateJson,
        DateTime ModifiedUtc,
        long Length);

    private sealed class ConfiguredReportDocumentMissingException : InvalidOperationException
    {
        internal ConfiguredReportDocumentMissingException(string message, Exception? inner = null)
            : base(message, inner)
        {
        }
    }
}

/// <summary>Identifies one configured file body without reading it.</summary>
internal sealed record ConfiguredReportDocumentReference(string ReportName, string SourceFile);

/// <summary>Contains one loaded configured document and both its typed and canonical JSON state.</summary>
/// <param name="ReportName">The configured report that owns the document.</param>
/// <param name="SourceFile">The configured file reference persisted beside the generated database id.</param>
/// <param name="Title">The validated display title.</param>
/// <param name="Default">Whether this file is the report family's configured default.</param>
/// <param name="State">The detached typed report state.</param>
/// <param name="StateJson">The state serialized with the protocol options.</param>
/// <param name="ModifiedUtc">The source file's last-write timestamp.</param>
/// <param name="Length">The source file length used for cache invalidation.</param>
internal sealed record ConfiguredReportDocument(
    string ReportName,
    string SourceFile,
    string Title,
    bool Default,
    ReportState State,
    string StateJson,
    DateTime ModifiedUtc,
    long Length);
