using InteractiveReport.Core.Definitions;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Export;

/// <summary>
/// A rendered report file. <see cref="Bytes"/> is ready for the host to store, attach,
/// stream, or return through its own transport.
/// </summary>
public sealed record ReportExportFile(
    byte[] Bytes,
    string FileName,
    string ContentType,
    bool Truncated);

/// <summary>
/// Renders report state into a file without involving HTTP authorization or endpoint
/// feature gates. The host application is the trust boundary and supplies any context
/// parameters explicitly.
/// </summary>
public interface IReportFileExporter
{
    /// <summary>Format tokens accepted by this exporter.</summary>
    IReadOnlyCollection<string> SupportedFormats { get; }

    /// <summary>Resolves a configured report by name and renders it.</summary>
    Task<ReportExportFile> Export(
        string reportName,
        ReportState state,
        IReadOnlyDictionary<string, object?>? contextParams = null,
        string format = "csv",
        CancellationToken ct = default);

    /// <summary>Renders an already-resolved definition, for transport adapters and custom stores.</summary>
    Task<ReportExportFile> Export(
        ReportDefinition definition,
        ReportState state,
        IReadOnlyDictionary<string, object?>? contextParams = null,
        string format = "csv",
        CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class ReportFileExporter : IReportFileExporter
{
    private static readonly IReadOnlyCollection<string> Formats = Array.AsReadOnly(["csv"]);
    private static readonly IReadOnlyDictionary<string, object?> NoContextParams =
        new Dictionary<string, object?>();

    private readonly IReportDefinitionStore _definitions;
    private readonly ReportExecutor _executor;

    public ReportFileExporter(IReportDefinitionStore definitions, ReportExecutor executor)
    {
        _definitions = definitions;
        _executor = executor;
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string> SupportedFormats => Formats;

    /// <inheritdoc />
    public async Task<ReportExportFile> Export(
        string reportName,
        ReportState state,
        IReadOnlyDictionary<string, object?>? contextParams = null,
        string format = "csv",
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportName);
        ArgumentNullException.ThrowIfNull(state);

        var definition = await _definitions.Find(reportName, ct)
            ?? throw new KeyNotFoundException(
                $"Interactive report definition '{reportName}' was not found.");
        return await Export(definition, state, contextParams, format, ct);
    }

    /// <inheritdoc />
    public async Task<ReportExportFile> Export(
        ReportDefinition definition,
        ReportState state,
        IReadOnlyDictionary<string, object?>? contextParams = null,
        string format = "csv",
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(format);

        if (!SupportedFormats.Contains(format.Trim(), StringComparer.OrdinalIgnoreCase))
            throw new NotSupportedException(
                $"Export format '{format}' is not supported. Supported formats: "
                + string.Join(", ", SupportedFormats) + ".");

        var export = await _executor.Export(
            definition,
            state,
            contextParams ?? NoContextParams,
            ct);
        return new ReportExportFile(
            CsvWriter.Write(export.Columns, export.Rows),
            $"{definition.Name}.csv",
            "text/csv; charset=utf-8",
            export.Truncated);
    }
}
