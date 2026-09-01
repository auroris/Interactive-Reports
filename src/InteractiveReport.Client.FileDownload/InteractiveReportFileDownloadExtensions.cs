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

/// <summary>A complete generated file returned by a download writer.</summary>
public sealed record InteractiveReportDownloadFile(
    byte[] Bytes,
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
            CsvWriter.Write(table.Columns, table.Rows),
            $"{SafeName(reportName)}.csv",
            "text/csv; charset=utf-8");
    }

    private static string SafeName(string reportName)
    {
        var safe = new string(reportName.Select(character =>
            char.IsLetterOrDigit(character) || character is '.' or '-' or '_'
                ? character
                : '-').ToArray()).Trim('.', '-');
        return safe.Length == 0 ? "report" : safe;
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
            return Error(
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
            return Error(
                InteractiveReportErrorCodes.MalformedReportState,
                StatusCodes.Status400BadRequest,
                ex.Message);
        }

        var document = ReportStateResolver.Resolve(defaults: null, posted);
        document.Page ??= new PageRequest();
        document.Page.Index = 1;
        document.Page.Size = 0;

        var request = new InteractiveReportRequestContext
        {
            User = http.User,
            RequestServices = http.RequestServices,
            TraceIdentifier = http.TraceIdentifier,
        };
        var queried = await server.QueryForDownload(name, document, request, ct);
        if (queried.Failure is not null)
            return Failure(queried.Failure, http);

        var file = writer.Write(name, format, queried.Value!);
        http.Response.Headers["X-IR-Truncated"] = queried.Truncated ? "true" : "false";
        return Results.File(file.Bytes, file.ContentType, file.FileName);
    }

    private static IResult Failure(InteractiveReportFailure failure, HttpContext http)
    {
        var status = failure.Kind switch
        {
            InteractiveReportFailureKind.Invalid => StatusCodes.Status400BadRequest,
            InteractiveReportFailureKind.Unauthenticated => StatusCodes.Status401Unauthorized,
            InteractiveReportFailureKind.Forbidden => StatusCodes.Status403Forbidden,
            InteractiveReportFailureKind.NotFound => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status500InternalServerError,
        };
        if (status == StatusCodes.Status401Unauthorized)
            http.Response.Headers.WWWAuthenticate = "InteractiveReport";
        return Error(failure.Code, status, failure.Details, failure.TraceIdentifier);
    }

    private static IResult Error(
        string code,
        int status,
        string? details = null,
        string? traceIdentifier = null)
    {
        var (title, description) = InteractiveReportErrorCatalog.Find(code);
        return Results.Json(
            new InteractiveReportError(code, description, title, details, traceIdentifier),
            IrJson.Options,
            statusCode: status);
    }
}
