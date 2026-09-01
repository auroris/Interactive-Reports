using System.Text.Json;
using InteractiveReport.AspNetCore;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Validation;
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
}

internal sealed class CsvInteractiveReportDownloadWriter : IInteractiveReportDownloadWriter
{
    private static readonly IReadOnlyCollection<string> Formats = Array.AsReadOnly(["csv"]);

    public IReadOnlyCollection<string> SupportedFormats => Formats;

    public InteractiveReportDownloadFile Write(
        string reportName,
        string format,
        ReportResult result)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportName);
        ArgumentException.ThrowIfNullOrWhiteSpace(format);
        ArgumentNullException.ThrowIfNull(result);
        if (!Formats.Contains(format, StringComparer.OrdinalIgnoreCase))
            throw new NotSupportedException($"Download format '{format}' is not supported.");

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
    /// <summary>Registers the built-in CSV download writer.</summary>
    public static IServiceCollection AddInteractiveReportFileDownload(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IInteractiveReportDownloadWriter, CsvInteractiveReportDownloadWriter>();
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
            .Produces<InteractiveReportError>(StatusCodes.Status400BadRequest)
            .Produces<InteractiveReportError>(StatusCodes.Status401Unauthorized)
            .Produces<InteractiveReportError>(StatusCodes.Status403Forbidden)
            .Produces<InteractiveReportError>(StatusCodes.Status404NotFound)
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

        // The detached copy that carries the paging override is made by the same deep copy the
        // executor uses, and that copy assumes the structural pass has run: a null table or a pair
        // of case-colliding table ids must be the documented per-path 400, not a copy failure.
        var structural = ReportStateResolver.CollectStructuralErrors(posted);
        if (structural.Count > 0)
            return Failure(InteractiveReportServer.Validation(new ReportValidationException(structural)), http);

        var document = ReportStateResolver.Resolve(defaults: null, posted);
        document.Page ??= new PageRequest();
        document.Page.Index = 1;
        document.Page.Size = 0;

        var request = InteractiveReportHttpRequest.Context(http);
        var queried = await server.QueryForDownload(name, document, request, ct);
        if (queried.Failure is not null)
            return Failure(queried.Failure, http);

        var file = writer.Write(name, format, queried.Value!);
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
