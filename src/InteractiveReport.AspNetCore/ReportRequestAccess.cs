using InteractiveReport.Core.Authorization;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Identity;
using InteractiveReport.Core.Model;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InteractiveReport.AspNetCore;

/// <summary>
/// Applies report-level authorization and resolves trusted request context before an
/// endpoint enters the report engine.
/// </summary>
internal static class ReportRequestAccess
{
    /// <summary>
    /// Applies definition-level authentication, administrator-list, and policy gates.
    /// Operation authorization is separate so mutation endpoints can hydrate the
    /// client-authored definition before passing it to the application authorizer.
    /// </summary>
    public static async Task<IResult?> AuthorizeDefinition(
        ReportDefinition definition,
        HttpContext context)
    {
        var authorization = definition.Authorization;
        var options = context.RequestServices
            .GetRequiredService<IOptionsMonitor<InteractiveReportOptions>>().CurrentValue;

        if (authorization?.AdministratorsOnly == true)
        {
            if (context.User.Identity?.IsAuthenticated != true)
                return Results.Unauthorized();
            var administrator = await AdministratorAccess(context, options, context.RequestAborted);
            if (administrator.Error is not null) return administrator.Error;
            if (administrator.Configured && !administrator.Granted)
                return Results.NotFound();
        }

        if (authorization?.AllowAnonymous == true) return null;

        if (context.User.Identity?.IsAuthenticated != true)
            return Results.Unauthorized();

        if (authorization?.Policy is { Length: > 0 } policy)
        {
            var service = context.RequestServices.GetService<IAuthorizationService>()
                ?? throw new InvalidOperationException(
                    $"Report '{definition.Name}' declares policy '{policy}' but the host has not registered authorization services (AddAuthorization).");
            var decision = await service.AuthorizeAsync(context.User, policy);
            if (!decision.Succeeded) return Results.NotFound();
        }

        var identity = ReportIdentity.Resolve(context.User, options.IdentityClaim);
        var storageConfigured = ReportConnectionRegistry.IsStoreConfigured(options.SavedReports);
        var databaseAccess = new DatabaseReportAccess(false, false);
        if (storageConfigured)
        {
            try
            {
                databaseAccess = await context.RequestServices
                    .GetRequiredService<IReportAuthorizationStore>()
                    .GetReportAccess(definition.Name, identity, context.RequestAborted);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return AuthorizationFailure(context, definition.Name, "report access lookup", ex);
            }
        }

        var restricted = authorization?.Restricted == true || databaseAccess.Restricted;
        var configuredGrant = identity is not null
            && authorization?.Users?.Any(user => string.Equals(
                user.Trim(), identity, StringComparison.OrdinalIgnoreCase)) == true;
        if (restricted && !configuredGrant && !databaseAccess.UserGranted)
            return Results.NotFound();

        return null;
    }

    /// <summary>
    /// Applies one or more application actions after definition authorization. Every
    /// registered authorizer must grant every action. Administrator-required actions
    /// use the configured/database administrator union when nonempty; otherwise at
    /// least one application authorizer must affirmatively grant the operation.
    /// </summary>
    public static async Task<IResult?> AuthorizeOperations(
        ReportDefinition definition,
        HttpContext context,
        IReadOnlyCollection<InteractiveReportAction> actions,
        InteractiveReportAuthorizationResource resource,
        bool administratorRequired,
        bool hideDenied,
        string? denialDetail,
        CancellationToken ct)
    {
        if (actions.Count == 0)
            throw new ArgumentException("At least one authorization action is required.", nameof(actions));

        var options = context.RequestServices
            .GetRequiredService<IOptionsMonitor<InteractiveReportOptions>>().CurrentValue;
        administratorRequired |= definition.Authorization?.AdministratorsOnly == true;

        var authorizers = context.RequestServices
            .GetServices<IInteractiveReportAuthorizer>()
            .ToArray();

        if (administratorRequired)
        {
            if (context.User.Identity?.IsAuthenticated != true)
                return Results.Unauthorized();

            var administrator = await AdministratorAccess(context, options, ct);
            if (administrator.Error is not null) return administrator.Error;
            if (administrator.Configured && !administrator.Granted)
                return Denied(context, hideDenied, denialDetail);
            if (!administrator.Configured && authorizers.Length == 0)
                return Denied(context, hideDenied, denialDetail);
        }

        if (authorizers.Length == 0) return null;

        foreach (var action in actions.Distinct())
        {
            var request = new InteractiveReportAuthorizationRequest
            {
                User = context.User,
                Action = action,
                Resource = resource,
                RequestServices = context.RequestServices,
            };

            foreach (var authorizer in authorizers)
            {
                bool allowed;
                try
                {
                    allowed = await authorizer.Authorize(request, ct);
                }
                catch (InteractiveReportAuthorizationDeniedException)
                {
                    allowed = false;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
                        .CreateLogger("InteractiveReport.Authorization");
                    logger.LogError(
                        ex,
                        "Report {Report}: authorization for {Action} failed (traceId {TraceId})",
                        definition.Name,
                        action,
                        context.TraceIdentifier);
                    return Results.Problem(
                        title: "Report authorization failed",
                        statusCode: StatusCodes.Status500InternalServerError,
                        extensions: new Dictionary<string, object?>
                        {
                            ["traceId"] = context.TraceIdentifier,
                        });
                }

                if (!allowed)
                    return Denied(context, hideDenied, denialDetail);
            }
        }

        return null;
    }

    /// <summary>Definition and operation authorization for endpoints with no body.</summary>
    public static async Task<IResult?> Authorize(
        ReportDefinition definition,
        HttpContext context,
        IReadOnlyCollection<InteractiveReportAction> actions,
        InteractiveReportAuthorizationResource resource,
        bool administratorRequired,
        bool hideDenied,
        string? denialDetail,
        CancellationToken ct)
    {
        if (await AuthorizeDefinition(definition, context) is { } denied) return denied;
        return await AuthorizeOperations(
            definition,
            context,
            actions,
            resource,
            administratorRequired,
            hideDenied,
            denialDetail,
            ct);
    }

    /// <summary>
    /// Null when the feature is whitelisted. 403 (not 404) because the caller already
    /// reached an existing, authorized report — only this capability is switched off.
    /// </summary>
    public static IResult? RequireFeature(ReportDefinition definition, string feature)
        => ReportFeatures.IsEnabled(definition, feature)
            ? null
            : Results.Problem(
                title: "Feature disabled",
                detail: $"'{feature}' is not enabled for this report",
                statusCode: StatusCodes.Status403Forbidden);

    /// <summary>
    /// UI hint only. It never grants an operation: configured or database
    /// administrators may ask, and an application authorizer may make action-specific
    /// decisions when neither administrator source is populated.
    /// </summary>
    public static async Task<bool> MayRequestAdministration(
        HttpContext context,
        CancellationToken ct = default)
    {
        if (context.User.Identity?.IsAuthenticated != true) return false;
        var options = context.RequestServices
            .GetRequiredService<IOptionsMonitor<InteractiveReportOptions>>().CurrentValue;
        if (!ReportConnectionRegistry.IsStoreConfigured(options.SavedReports)) return false;
        var administrator = await AdministratorAccess(context, options, ct);
        if (administrator.Error is not null)
            throw new InvalidOperationException("Administration access lookup failed.");
        if (administrator.Configured) return administrator.Granted;
        return context.RequestServices.GetServices<IInteractiveReportAuthorizer>().Any();
    }

    private static async Task<AdministratorDecision> AdministratorAccess(
        HttpContext context,
        InteractiveReportOptions options,
        CancellationToken ct)
    {
        var identity = ReportIdentity.Resolve(context.User, options.IdentityClaim);
        try
        {
            var database = await context.RequestServices
                .GetRequiredService<IReportAuthorizationStore>()
                .GetAdministratorAccess(identity, ct);
            return new AdministratorDecision(
                Configured: options.Administrators.Count > 0 || database.Configured,
                Granted: ReportIdentity.IsAdministrator(
                    context.User, options.IdentityClaim, options.Administrators)
                    || database.UserGranted,
                Error: null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new AdministratorDecision(
                Configured: false,
                Granted: false,
                Error: AuthorizationFailure(
                    context,
                    SavedReportsListingDefinition.Name,
                    "administrator access lookup",
                    ex));
        }
    }

    private static IResult AuthorizationFailure(
        HttpContext context,
        string reportName,
        string operation,
        Exception exception)
    {
        context.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("InteractiveReport.Authorization")
            .LogError(
                exception,
                "Report {Report}: {Operation} failed (traceId {TraceId})",
                reportName,
                operation,
                context.TraceIdentifier);
        return Results.Problem(
            title: "Report authorization failed",
            statusCode: StatusCodes.Status500InternalServerError,
            extensions: new Dictionary<string, object?>
            {
                ["traceId"] = context.TraceIdentifier,
            });
    }

    private sealed record AdministratorDecision(bool Configured, bool Granted, IResult? Error);

    private static IResult Denied(HttpContext context, bool hide, string? detail)
    {
        if (context.User.Identity?.IsAuthenticated != true)
            return Results.Unauthorized();
        if (hide) return Results.NotFound();
        return Results.Problem(
            title: "Authorization denied",
            detail: detail ?? "The caller is not allowed to perform this operation.",
            statusCode: StatusCodes.Status403Forbidden);
    }

    public static async Task<IReadOnlyDictionary<string, object?>> ResolveContextParameters(
        ReportDefinition definition,
        HttpContext context,
        CancellationToken ct)
    {
        if (definition.ContextParams is null || definition.ContextParams.Count == 0)
            return new Dictionary<string, object?>();

        var resolver = context.RequestServices.GetRequiredService<IContextParameterResolver>();
        var result = new Dictionary<string, object?>();
        foreach (var (parameterName, specification) in definition.ContextParams)
        {
            result[parameterName] = await resolver.Resolve(
                parameterName,
                specification,
                context.User,
                ct);
        }
        return result;
    }
}
