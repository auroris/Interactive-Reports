// GraphQL execution entrypoint: resolves saved reports and reuses the HTTP layer's
// authorization, context, validation, and execution policies. The transport changes the
// response shape, not the engine's trust boundary.

using System.Text.Json;
using GraphQL;
using InteractiveReport.AspNetCore;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Identity;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.SavedReports;
using InteractiveReport.Core.Validation;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InteractiveReport.GraphQL;

/// <summary>
/// Executes one saved report for the GraphQL resolver while preserving the HTTP adapter's
/// authorization, context-parameter, validation, and error-sanitization boundaries.
/// </summary>
internal sealed class InteractiveReportGraphQLExecutor(
    IHttpContextAccessor httpContextAccessor,
    ConfiguredReportDocumentSynchronizer synchronizer,
    ConfiguredReportDocumentStore configuredDocuments,
    DefaultReportDocumentService defaultDocuments,
    ISavedReportStore savedReports,
    IReportAccessService reportAccess,
    ReportExecutor executor,
    IOptionsMonitor<InteractiveReportOptions> options,
    InteractiveReportLogging logging)
{
    /// <summary>
    /// Loads, authorizes, optionally repages, and executes a saved report for the active HTTP request.
    /// </summary>
    /// <param name="id">The saved-report identifier.</param>
    /// <param name="page">The requested one-based page number; <see langword="null"/> preserves the saved value.</param>
    /// <param name="pageSize">The requested page size; <see langword="null"/> preserves the saved value.</param>
    /// <param name="ct">Signals that the operation should be canceled.</param>
    /// <returns>A task containing the executed report result.</returns>
    /// <remarks>Trusts persisted document identities, reads a configured file body only after authorization, may query the report database, and logs unexpected failures. Transport failures are returned as sanitized <see cref="ExecutionError"/> instances.</remarks>
    /// <exception cref="ExecutionError">Thrown for invalid arguments, missing or denied reports, invalid saved state, and sanitized execution failures.</exception>
    /// <exception cref="InvalidOperationException">Thrown when no active HTTP request is available.</exception>
    public async Task<ReportResult?> Query(
        long id,
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

        var saved = await savedReports.Get(id, ct);
        if (saved is null) throw NotFound();
        var metadata = saved.Metadata();

        var identity = ReportIdentity.Resolve(context.User, options.CurrentValue.IdentityClaim);
        var savedReportAccess = SavedReportAccessPolicy.Read(metadata, identity, administrator: false);
        var resource = new InteractiveReportAuthorizationResource
        {
            ReportName = metadata.ReportName,
            SavedReport = new SavedReportAuthorizationResource(
                metadata.Id,
                metadata.Title,
                metadata.Owner,
                metadata.IsGlobal,
                metadata.IsPrimary,
                metadata.Origin),
        };
        var authorization = await reportAccess.Authorize(
            new ReportAccessRequest
            {
                ReportName = metadata.ReportName,
                Actions = [InteractiveReportAction.ReadSavedReport, InteractiveReportAction.Query],
                Resource = resource,
                AdministratorRequired = savedReportAccess != SavedReportAccess.Allowed,
                HideDenied = true,
            },
            context,
            ct);
        if (authorization.Error is not null)
            throw AuthorizationError(authorization.Error, context);
        var definition = authorization.Definition ?? throw NotFound();

        try
        {
            var contextParameters = await reportAccess.ResolveContextParameters(definition, context, ct);
            ReportState state;
            if (saved.Origin == SavedReportOrigin.Configured)
            {
                if (saved.SourceFile is null
                    || configuredDocuments.Find(saved.ReportName, saved.SourceFile) is not { } file)
                {
                    await synchronizer.RemoveMissing(saved, ct);
                    await defaultDocuments.CreateMissing(definition, ct);
                    throw NotFound();
                }
                state = file.State;
            }
            else if (saved.IsDefault)
            {
                (_, state) = await defaultDocuments.LoadState(
                    saved, definition, executor, contextParameters, ct);
            }
            else
            {
                state = JsonSerializer.Deserialize<ReportState>(
                        saved.StateJson ?? throw new JsonException("state is null"),
                        IrJson.Options)
                    ?? throw new JsonException("state is null");
            }
            if (page.HasValue || pageSize.HasValue)
            {
                state.Page ??= new PageRequest();
                if (page.HasValue) state.Page.Index = page.Value;
                if (pageSize.HasValue) state.Page.Size = pageSize.Value;
            }

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
            logging.Logger?.LogError(
                ex,
                "Saved report {SavedReportId}: GraphQL execution failed (traceId {TraceId})",
                saved.Id,
                context.TraceIdentifier);
            var error = Error("Report execution failed.", "INTERNAL_SERVER_ERROR");
            error.AddExtension("traceId", context.TraceIdentifier);
            throw error;
        }
    }

    /// <summary>
    /// Creates a GraphQL error for a denied authorization decision.
    /// </summary>
    /// <param name="denied">The authorization result that explains why access was denied.</param>
    /// <param name="context">The current HTTP request and response context.</param>
    /// <returns>The execution error.</returns>
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

    /// <summary>
    /// Creates the GraphQL error used when authorization fails unexpectedly.
    /// </summary>
    /// <param name="context">The current HTTP request and response context.</param>
    /// <returns>The execution error.</returns>
    private static ExecutionError InternalAuthorizationError(HttpContext context)
    {
        var error = Error("Report authorization failed.", "INTERNAL_SERVER_ERROR");
        error.AddExtension("traceId", context.TraceIdentifier);
        return error;
    }

    /// <summary>
    /// Creates the GraphQL error for an unknown report.
    /// </summary>
    /// <returns>The execution error.</returns>
    private static ExecutionError NotFound()
        => Error("The saved report was not found.", "NOT_FOUND");

    /// <summary>
    /// Creates a GraphQL execution error with the supplied protocol code.
    /// </summary>
    /// <param name="message">The client-safe GraphQL error message.</param>
    /// <param name="code">The stable protocol or diagnostic code to return.</param>
    /// <returns>The execution error.</returns>
    private static ExecutionError Error(string message, string code)
        => new(message) { Code = code };
}
