using System.Text.Json;
using GraphQL;
using InteractiveReport.AspNetCore;
using InteractiveReport.Core.Definitions;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Identity;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.SavedReports;
using InteractiveReport.Core.Validation;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InteractiveReport.GraphQL;

internal sealed class InteractiveReportGraphQLExecutor(
    IHttpContextAccessor httpContextAccessor,
    ConfiguredReportDocumentSynchronizer synchronizer,
    ISavedReportStore savedReports,
    IReportDefinitionStore definitions,
    ReportExecutor executor,
    IOptionsMonitor<InteractiveReportOptions> options,
    ILogger<InteractiveReportGraphQLExecutor> logger)
{
    public async Task<ReportResult?> Query(
        string id,
        int? page,
        int? pageSize,
        CancellationToken ct)
    {
        if (page is < 1)
            throw Error("page must be at least 1.", "BAD_USER_INPUT");
        if (pageSize is < 0)
            throw Error("pageSize cannot be negative.", "BAD_USER_INPUT");

        var context = httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException("GraphQL report execution requires an active HTTP request.");

        await synchronizer.EnsureSynced(ct);
        var saved = await savedReports.Get(id, ct);
        if (saved is null) throw NotFound();

        var (definition, resolutionError) = await ReportRequestAccess.ResolveDefinition(
            definitions, saved.ReportName, context, ct);
        if (resolutionError is not null)
            throw AuthorizationError(resolutionError, context);
        if (definition is null) throw NotFound();

        var identity = ReportIdentity.Resolve(context.User, options.CurrentValue.IdentityClaim);
        var access = SavedReportAccessPolicy.Read(saved, identity, administrator: false);
        var resource = new InteractiveReportAuthorizationResource
        {
            ReportName = saved.ReportName,
            SavedReport = new SavedReportAuthorizationResource(
                saved.Id,
                saved.Title,
                saved.Owner,
                saved.IsGlobal,
                saved.IsPrimary,
                saved.Origin),
        };
        if (await ReportRequestAccess.AuthorizeOperations(
                definition,
                context,
                [InteractiveReportAction.ReadSavedReport, InteractiveReportAction.Query],
                resource,
                administratorRequired: access != SavedReportAccess.Allowed,
                hideDenied: true,
                denialDetail: null,
                ct) is { } denied)
            throw AuthorizationError(denied, context);

        try
        {
            var state = JsonSerializer.Deserialize<ReportState>(saved.StateJson, IrJson.Options)
                ?? throw new JsonException("state is null");
            if (page.HasValue || pageSize.HasValue)
            {
                state.Page ??= new PageRequest();
                if (page.HasValue) state.Page.Index = page.Value;
                if (pageSize.HasValue) state.Page.Size = pageSize.Value;
            }

            var contextParameters = await ReportRequestAccess.ResolveContextParameters(definition, context, ct);
            return await executor.Query(definition, state, contextParameters, ct);
        }
        catch (ReportValidationException ex)
        {
            var error = Error("The saved report state failed validation.", "REPORT_VALIDATION_FAILED");
            error.AddExtension(
                "validation",
                ex.Errors.GroupBy(item => item.Path).ToDictionary(
                    group => group.Key,
                    group => group.Select(item => item.Message).ToArray()));
            throw error;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ExecutionError)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Saved report {SavedReportId}: GraphQL execution failed (traceId {TraceId})",
                saved.Id,
                context.TraceIdentifier);
            var error = Error("Report execution failed.", "INTERNAL_SERVER_ERROR");
            error.AddExtension("traceId", context.TraceIdentifier);
            throw error;
        }
    }

    private static ExecutionError AuthorizationError(IResult denied, HttpContext context)
    {
        var statusCode = (denied as IStatusCodeHttpResult)?.StatusCode;
        return statusCode switch
        {
            StatusCodes.Status401Unauthorized => Error("Authentication is required.", "UNAUTHENTICATED"),
            StatusCodes.Status403Forbidden => Error("The caller is not allowed to execute this report.", "FORBIDDEN"),
            StatusCodes.Status500InternalServerError => InternalAuthorizationError(context),
            _ => NotFound(),
        };
    }

    private static ExecutionError InternalAuthorizationError(HttpContext context)
    {
        var error = Error("Report authorization failed.", "INTERNAL_SERVER_ERROR");
        error.AddExtension("traceId", context.TraceIdentifier);
        return error;
    }

    private static ExecutionError NotFound()
        => Error("The saved report was not found.", "NOT_FOUND");

    private static ExecutionError Error(string message, string code)
        => new(message) { Code = code };
}
