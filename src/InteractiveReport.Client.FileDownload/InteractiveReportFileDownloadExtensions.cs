using System.Text.Json;
using System.Text;
using InteractiveReport.AspNetCore;
using InteractiveReport.Core.Model;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace InteractiveReport.Client.FileDownload;

/// <summary>
/// A download the server is ready to produce. The content is a callback rather than a buffer: an
/// export is unpaged, so materializing it would hold the whole rendered document in memory — and on
/// the large object heap — before a single byte reached the client.
/// </summary>
/// <param name="WriteContent">Writes the complete file to the response body.</param>
/// <param name="FileName">The name offered to the client.</param>
/// <param name="ContentType">The content type, including charset where it applies.</param>
public sealed record InteractiveReportDownloadFile(
    Func<Stream, CancellationToken, Task> WriteContent,
    string FileName,
    string ContentType);

/// <summary>Turns an ordinary unpaged query result into a downloadable representation.</summary>
public interface IInteractiveReportDownloadWriter
{
    IReadOnlyCollection<string> SupportedFormats { get; }

    InteractiveReportDownloadFile Write(
        string reportName,
        string format,
        ReportResult result);

    /// <summary>Provides an optional display title while preserving the canonical report name for the filename.</summary>
    InteractiveReportDownloadFile Write(string reportName, string format, ReportResult result, string? title)
        => Write(reportName, format, result);
}

internal sealed class InteractiveReportDownloadWriter : IInteractiveReportDownloadWriter
{
    private static readonly IReadOnlyCollection<string> Formats = Array.AsReadOnly(["csv", "html"]);

    public IReadOnlyCollection<string> SupportedFormats => Formats;

    public InteractiveReportDownloadFile Write(
        string reportName,
        string format,
        ReportResult result)
        => Write(reportName, format, result, null);

    public InteractiveReportDownloadFile Write(string reportName, string format, ReportResult result, string? title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportName);
        ArgumentException.ThrowIfNullOrWhiteSpace(format);
        ArgumentNullException.ThrowIfNull(result);
        if (!Formats.Contains(format, StringComparer.OrdinalIgnoreCase))
            throw new NotSupportedException($"Download format '{format}' is not supported.");

        if (format.Equals("html", StringComparison.OrdinalIgnoreCase))
        {
            var html = HtmlReportPresentation.Render(title ?? reportName, result);
            return new(async (stream, token) => {
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true);
                await writer.WriteAsync(html.AsMemory(), token);
                await writer.FlushAsync(token);
            }, InteractiveReportHttpRequest.SafeFileName(reportName, ".html"), "text/html; charset=utf-8");
        }

        var table = CsvReportPresentation.Render(result);
        return new(
            (stream, token) => CsvWriter.WriteToAsync(
                stream, table.Columns, table.Rows, CsvCellPolicy.SafeText, token),
            InteractiveReportHttpRequest.SafeFileName(reportName, ".csv"),
            "text/csv; charset=utf-8");
    }
}

/// <summary>Registers and maps the Interactive Reports file-download client.</summary>
public static class InteractiveReportFileDownloadExtensions
{
    /// <summary>Registers the built-in CSV and self-contained HTML download writer.</summary>
    public static IServiceCollection AddInteractiveReportFileDownload(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IInteractiveReportDownloadWriter, InteractiveReportDownloadWriter>();
        return services;
    }

    /// <summary>
    /// Maps <c>POST {prefix}/{name}/{format}</c>. The posted report document is detached,
    /// changed to request every row, submitted through the server query boundary, and rendered.
    /// </summary>
    public static RouteGroupBuilder MapInteractiveReportFileDownload(
        this IEndpointRouteBuilder endpoints,
        string prefix = "/api/download")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var group = endpoints.MapGroup(prefix);
        group.AddEndpointFilter(InteractiveReportLogging.LogRequest);
        group.AddEndpointFilter(static async (invocation, next) =>
        {
            invocation.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(invocation);
        });
        group.MapPost("/{name}/{format}", Download)
            .WithTags("Interactive Reports - File Downloads")
            .WithSummary("Download a report file")
            .WithDescription(
                "Executes the posted report document without paging and returns it in the requested format.")
            .Accepts<ReportState>("application/json")
            .Produces(StatusCodes.Status200OK, contentType: "text/csv")
            .Produces(StatusCodes.Status200OK, contentType: "text/html")
            .Produces<InteractiveReportError>(StatusCodes.Status400BadRequest)
            .Produces<InteractiveReportError>(StatusCodes.Status401Unauthorized)
            .Produces<InteractiveReportError>(StatusCodes.Status403Forbidden)
            .Produces<InteractiveReportError>(StatusCodes.Status404NotFound)
            .Produces<InteractiveReportError>(StatusCodes.Status422UnprocessableEntity)
            .Produces<InteractiveReportError>(StatusCodes.Status500InternalServerError);
        return group;
    }

    private static async Task<IResult> Download(
        string name,
        string format,
        HttpContext http,
        [FromServices] IInteractiveReportServer server,
        [FromServices] IInteractiveReportDownloadWriter writer,
        CancellationToken ct)
    {
        format = format.Trim();
        if (!writer.SupportedFormats.Contains(format, StringComparer.OrdinalIgnoreCase))
            return InteractiveReportHttpResult.Error(
                InteractiveReportErrorCodes.UnsupportedExportFormat,
                StatusCodes.Status400BadRequest,
                $"format '{format}' is not supported; supported formats: "
                + string.Join(", ", writer.SupportedFormats));

        ReportState posted;
        try
        {
            posted = await JsonSerializer.DeserializeAsync<ReportState>(http.Request.Body, IrJson.Options, ct)
                ?? new ReportState();
        }
        catch (JsonException ex)
        {
            return InteractiveReportHttpResult.Error(
                InteractiveReportErrorCodes.MalformedReportState,
                StatusCodes.Status400BadRequest,
                ex.Message);
        }

        // This adapter owns the deserialized request. The executor validates and copies it
        // after authorization, including malformed table entries and case-colliding names.
        posted.Page = new PageRequest { Index = 1, Size = 0 };

        var request = InteractiveReportHttpRequest.Context(http);
        var queried = await server.QueryForDownload(name, posted, request, ct);
        if (queried.Failure is not null)
            return Failure(queried.Failure, http);

        string? title = null;
        if (format.Equals("html", StringComparison.OrdinalIgnoreCase))
        {
            if (queried.Truncated) return InteractiveReportHttpResult.Error(
                InteractiveReportErrorCodes.ExportLimitExceeded, StatusCodes.Status422UnprocessableEntity,
                "This report exceeds the export row limit. Narrow the filters and try again.");
            var schema = await server.GetSchema(name, request, ct);
            if (schema.Failure is not null) return Failure(schema.Failure, http);
            title = schema.Value!.Title;
        }
        InteractiveReportDownloadFile file;
        try { file = writer.Write(name, format, queried.Value!, title); }
        catch (HtmlReportSizeLimitException ex)
        {
            return InteractiveReportHttpResult.Error(InteractiveReportErrorCodes.ExportLimitExceeded,
                StatusCodes.Status422UnprocessableEntity, ex.Message);
        }
        http.Response.Headers["X-IR-Truncated"] = queried.Truncated ? "true" : "false";
        // Streamed rather than buffered, so the response carries no Content-Length: an unpaged
        // export is exactly the case where holding the whole body to announce its size is the
        // cost worth avoiding.
        return Results.Stream(
            stream => file.WriteContent(stream, ct),
            file.ContentType,
            file.FileName);
    }

    private static IResult Failure(InteractiveReportFailure failure, HttpContext http)
        => InteractiveReportHttpResult.Failure(failure, http);
}
