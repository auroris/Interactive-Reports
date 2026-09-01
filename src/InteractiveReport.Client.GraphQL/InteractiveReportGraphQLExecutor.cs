using GraphQL;
using InteractiveReport.AspNetCore;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Validation;
using Microsoft.AspNetCore.Http;

namespace InteractiveReport.Client.GraphQL;

/// <summary>
/// GraphQL client workflow: load an authorized report document, apply GraphQL paging
/// mutations to a detached copy, and submit it through the ordinary server query boundary.
/// </summary>
internal sealed class InteractiveReportGraphQLExecutor(
    IHttpContextAccessor httpContextAccessor,
    IInteractiveReportServer server)
{
    public async Task<ReportResult?> Query(
        long id,
        int? page,
        int? pageSize,
        CancellationToken ct)
    {
        if (page is < 1) throw Error("page must be at least 1.", "BAD_USER_INPUT");
        if (pageSize is < 0) throw Error("pageSize cannot be negative.", "BAD_USER_INPUT");

        var http = httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException("GraphQL report execution requires an active HTTP request.");
        var context = new InteractiveReportRequestContext
        {
            User = http.User,
            RequestServices = http.RequestServices,
            TraceIdentifier = http.TraceIdentifier,
        };

        var loaded = await server.LoadDocument(id, context, ct);
        if (loaded.Failure is not null) throw Failure(loaded.Failure);
        var document = loaded.Value!;
        var state = ReportStateResolver.Resolve(defaults: null, document.State);
        if (page.HasValue || pageSize.HasValue)
        {
            state.Page ??= new PageRequest();
            if (page.HasValue) state.Page.Index = page.Value;
            if (pageSize.HasValue) state.Page.Size = pageSize.Value;
        }

        var queried = await server.Query(document with { State = state }, context, ct);
        if (queried.Failure is not null) throw Failure(queried.Failure);
        return queried.Value;
    }

    private static ExecutionError Failure(InteractiveReportFailure failure)
    {
        var code = failure.Kind switch
        {
            InteractiveReportFailureKind.Unauthenticated => "UNAUTHENTICATED",
            InteractiveReportFailureKind.Forbidden => "FORBIDDEN",
            InteractiveReportFailureKind.NotFound => "NOT_FOUND",
            InteractiveReportFailureKind.Invalid => "REPORT_VALIDATION_FAILED",
            _ => "INTERNAL_SERVER_ERROR",
        };
        var message = failure.Kind switch
        {
            InteractiveReportFailureKind.Unauthenticated => "Authentication is required.",
            InteractiveReportFailureKind.Forbidden => "The caller is not allowed to execute this report.",
            InteractiveReportFailureKind.NotFound => "The saved report was not found.",
            InteractiveReportFailureKind.Invalid => "The saved report state failed validation.",
            _ => "Report execution failed.",
        };
        var error = Error(message, code);
        if (failure.Validation is not null) error.AddExtension("validation", failure.Validation);
        if (failure.TraceIdentifier is not null) error.AddExtension("traceId", failure.TraceIdentifier);
        return error;
    }

    private static ExecutionError Error(string message, string code)
        => new(message) { Code = code };
}
