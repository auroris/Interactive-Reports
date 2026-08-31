using InteractiveReport.Core.Definitions;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Export;

/// <summary>
/// A rendered report file. <see cref="Bytes"/> is ready for the host to store, attach,
/// stream, or return through its own transport.
/// </summary>
/// <param name="Bytes">The complete file payload.</param>
/// <param name="FileName">The suggested download file name.</param>
/// <param name="ContentType">The HTTP media type, including charset when applicable.</param>
/// <param name="Truncated">Whether execution stopped at the definition's maximum export rows.</param>
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
    /// <summary>Gets the case-insensitive format tokens accepted by this exporter.</summary>
    IReadOnlyCollection<string> SupportedFormats { get; }

    /// <summary>
    /// Resolves a configured report by name, executes the supplied state, and renders it.
    /// </summary>
    /// <param name="reportName">The case-insensitive configured report name.</param>
    /// <param name="state">The report-state document to execute.</param>
    /// <param name="contextParams">Trusted server-side context parameter values; <see langword="null"/> supplies an empty set.</param>
    /// <param name="format">The case-insensitive output format token; defaults to <c>csv</c>.</param>
    /// <param name="ct">Signals that the operation should be canceled; defaults to <c>default</c>.</param>
    /// <returns>A task whose result is the report export file.</returns>
    Task<ReportExportFile> Export(
        string reportName,
        ReportState state,
        IReadOnlyDictionary<string, object?>? contextParams = null,
        string format = "csv",
        CancellationToken ct = default);

    /// <summary>
    /// Executes and renders an already-resolved definition for transport adapters and custom stores.
    /// </summary>
    /// <param name="definition">The resolved executable report definition.</param>
    /// <param name="state">The report-state document to execute.</param>
    /// <param name="contextParams">Trusted server-side context parameter values; <see langword="null"/> supplies an empty set.</param>
    /// <param name="format">The case-insensitive output format token; defaults to <c>csv</c>.</param>
    /// <param name="ct">Signals that the operation should be canceled; defaults to <c>default</c>.</param>
    /// <returns>A task whose result is the report export file.</returns>
    Task<ReportExportFile> Export(
        ReportDefinition definition,
        ReportState state,
        IReadOnlyDictionary<string, object?>? contextParams = null,
        string format = "csv",
        CancellationToken ct = default);
}

/// <summary>Default CSV implementation of <see cref="IReportFileExporter"/>.</summary>
public sealed class ReportFileExporter : IReportFileExporter
{
    private static readonly IReadOnlyCollection<string> Formats = Array.AsReadOnly(["csv"]);
    private static readonly IReadOnlyDictionary<string, object?> NoContextParams =
        new Dictionary<string, object?>();

    private readonly IReportDefinitionStore _definitions;
    private readonly ReportExecutor _executor;

    /// <summary>
    /// Initializes the report file exporter for server-side report export.
    /// </summary>
    /// <param name="definitions">The store used to resolve report names.</param>
    /// <param name="executor">The engine used to execute report state for export.</param>
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
