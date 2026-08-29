using InteractiveReport.Core.Authorization;
using InteractiveReport.Core.Definitions;
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
/// One endpoint-facing authorization request. The service resolves the lightweight
/// authorization envelope, applies the definition gate, hydrates the executable
/// definition only after that succeeds, and finally evaluates the requested actions.
/// </summary>
public sealed record ReportAccessRequest
{
    public required string ReportName { get; init; }
    public required IReadOnlyCollection<InteractiveReportAction> Actions { get; init; }
    public InteractiveReportAuthorizationResource? Resource { get; init; }
    public Func<ReportDefinition, CancellationToken, Task<ReportAccessResourcePreparation>>?
        PrepareResource { get; init; }
    public Func<InteractiveReportAuthorizationResource, IEnumerable<InteractiveReportAction>>?
        AdditionalAdministratorActions { get; init; }
    public bool AdministratorRequired { get; init; }
    public bool HideDenied { get; init; }
    public string? DenialDetail { get; init; }
}

/// <summary>
/// Deferred endpoint input needed by resource-based authorization. It runs only after
/// the report-level gate and executable-definition hydration have succeeded.
/// </summary>
public sealed record ReportAccessResourcePreparation(
    InteractiveReportAuthorizationResource? Resource,
    IResult? Error = null);

/// <summary>The authorized executable definition, or the HTTP result denying access.</summary>
public sealed record ReportAccessResult(ReportDefinition? Definition, IResult? Error);

/// <summary>
/// Authorization for a protected Interactive Reports endpoint that does not require a
/// report definition, such as authorization administration or user-directory lookup.
/// </summary>
public sealed record EndpointAccessRequest
{
    public required IReadOnlyCollection<InteractiveReportAction> Actions { get; init; }
    public required InteractiveReportAuthorizationResource Resource { get; init; }
    public bool AdministratorRequired { get; init; }
    public bool HideDenied { get; init; }
    public string? DenialDetail { get; init; }
}

/// <summary>
/// Central access boundary for protected Interactive Reports transports. Endpoints make
/// one authorization call before protected execution, persistence, or provider work.
/// Host decision code plugs into this boundary through
/// <c>InteractiveReportBuilder.UseAuthorization</c> or
/// <c>InteractiveReportBuilder.UseAspNetCoreAuthorization</c>; host-owned endpoints can
/// resolve this service to apply the same report access contract.
/// </summary>
public interface IReportAccessService
{
    Task<ReportAccessResult> Authorize(
        ReportAccessRequest request,
        HttpContext context,
        CancellationToken ct = default);

    Task<IResult?> AuthorizeEndpoint(
        EndpointAccessRequest request,
        HttpContext context,
        CancellationToken ct = default);

    IResult? RequireFeature(ReportDefinition definition, string feature);

    Task<bool> MayRequestAdministration(HttpContext context, CancellationToken ct = default);

    Task<IReadOnlyDictionary<string, object?>> ResolveContextParameters(
        ReportDefinition definition,
        HttpContext context,
        CancellationToken ct = default);
}

internal sealed class ReportAccessService : IReportAccessService
{
    public async Task<ReportAccessResult> Authorize(
        ReportAccessRequest request,
        HttpContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        if (request.Actions.Count == 0)
            throw new ArgumentException("At least one authorization action is required.", nameof(request));

        EndpointExtensions.Log(context)?.LogDebug(
            "Authorizing report {Report} actions {Actions} (traceId {TraceId})",
            request.ReportName,
            string.Join(",", request.Actions),
            context.TraceIdentifier);

        var store = context.RequestServices.GetRequiredService<IReportDefinitionStore>();
        var (definition, resolutionError) = await ResolveDefinition(
            store, request.ReportName, context, ct);
        if (resolutionError is not null)
            return new ReportAccessResult(null, resolutionError);
        if (definition is null)
            return new ReportAccessResult(null, Results.NotFound());

        var suppliedResource = request.Resource;
        if (request.PrepareResource is not null)
        {
            var prepared = await request.PrepareResource(definition, ct);
            if (prepared.Error is not null)
                return new ReportAccessResult(null, prepared.Error);
            suppliedResource = prepared.Resource;
        }

        var resource = suppliedResource is null
            ? new InteractiveReportAuthorizationResource { ReportName = definition.Name }
            : suppliedResource with { ReportName = definition.Name };
        var denied = await AuthorizeOperations(
            context,
            request.Actions,
            resource,
            request.AdministratorRequired
                || definition.Authorization?.AdministratorsOnly == true,
            request.HideDenied || definition.Authorization?.AdministratorsOnly == true,
            request.DenialDetail,
            ct);
        if (denied is not null)
        {
            EndpointExtensions.Log(context)?.LogDebug(
                "Authorization denied for report {Report} actions {Actions} (traceId {TraceId})",
                definition.Name,
                string.Join(",", request.Actions),
                context.TraceIdentifier);
            return new ReportAccessResult(null, denied);
        }

        if (request.AdditionalAdministratorActions is not null)
        {
            var authorized = request.Actions.ToHashSet();
            while (true)
            {
                var next = request.AdditionalAdministratorActions(resource)
                    .Where(action => !authorized.Contains(action))
                    .Select(action => (InteractiveReportAction?)action)
                    .FirstOrDefault();
                if (!next.HasValue) break;

                denied = await AuthorizeOperations(
                    context,
                    [next.Value],
                    resource,
                    administratorRequired: true,
                    hideDenied: request.HideDenied
                        || definition.Authorization?.AdministratorsOnly == true,
                    denialDetail: request.DenialDetail,
                    ct: ct);
                if (denied is not null)
                    return new ReportAccessResult(null, denied);
                authorized.Add(next.Value);
            }
        }

        EndpointExtensions.Log(context)?.LogDebug(
            "Authorization granted for report {Report} actions {Actions} (traceId {TraceId})",
            definition.Name,
            string.Join(",", request.Actions),
            context.TraceIdentifier);
        return new ReportAccessResult(definition, null);
    }

    public Task<IResult?> AuthorizeEndpoint(
        EndpointAccessRequest request,
        HttpContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        if (request.Actions.Count == 0)
            throw new ArgumentException("At least one authorization action is required.", nameof(request));

        return AuthorizeOperations(
            context,
            request.Actions,
            request.Resource,
            request.AdministratorRequired,
            request.HideDenied,
            request.DenialDetail,
            ct);
    }

    /// <summary>
    /// Resolves and authorizes one report definition. Stores that implement the
    /// lightweight authorization interface are gated before the executable definition
    /// is validated, connected, or hydrated from saved-report storage.
    /// </summary>
    private static async Task<(ReportDefinition? Definition, IResult? Error)> ResolveDefinition(
        IReportDefinitionStore store,
        string name,
        HttpContext context,
        CancellationToken ct)
    {
        ReportDefinitionAuthorization? authorization = null;
        if (store is IReportDefinitionAuthorizationStore authorizationStore)
        {
            try
            {
                authorization = await authorizationStore.FindAuthorization(name, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return (null, EndpointExtensions.ServerError(
                    context, name, "authorization metadata resolution", ex));
            }

            if (authorization is null) return (null, null);
            try
            {
                if (await AuthorizeDefinition(authorization, context, ct) is { } denied)
                    return (null, denied);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return (null, AuthorizationFailure(
                    context, authorization.Name, "definition authorization", ex));
            }
        }

        ReportDefinition? definition;
        try
        {
            definition = await store.Find(name, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, EndpointExtensions.ServerError(context, name, "definition resolution", ex));
        }

        if (definition is null) return (null, null);
        if (authorization is null
            || !string.Equals(authorization.Name, definition.Name, StringComparison.OrdinalIgnoreCase)
            || !AuthorizationEquivalent(authorization.Authorization, definition.Authorization))
        {
            try
            {
                if (await AuthorizeDefinition(definition, context, ct) is { } denied)
                    return (null, denied);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return (null, AuthorizationFailure(
                    context, definition.Name, "definition authorization", ex));
            }
        }

        return (definition, null);
    }

    /// <summary>
    /// Applies definition-level authentication, administrator-list, and policy gates.
    /// Operation authorization is separate so mutation endpoints can hydrate the
    /// client-authored definition before passing it to the application authorizer.
    /// </summary>
    private static async Task<IResult?> AuthorizeDefinition(
        ReportDefinition definition,
        HttpContext context,
        CancellationToken ct)
        => await AuthorizeDefinition(
            new ReportDefinitionAuthorization(definition.Name, definition.Authorization),
            context,
            ct);

    private static async Task<IResult?> AuthorizeDefinition(
        ReportDefinitionAuthorization definition,
        HttpContext context,
        CancellationToken ct)
    {
        var authorization = definition.Authorization;
        var options = context.RequestServices
            .GetRequiredService<IOptionsMonitor<InteractiveReportOptions>>().CurrentValue;

        if (authorization?.AdministratorsOnly == true)
        {
            if (context.User.Identity?.IsAuthenticated != true)
                return Results.Unauthorized();
            var administrator = await AdministratorAccess(context, options, ct);
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

        // Administrators-only definitions cannot also carry named-user restrictions.
        // Once the administrator and optional ASP.NET policy gates pass, there is no
        // report-user authorization row to consult.
        if (authorization?.AdministratorsOnly == true) return null;

        var identity = ReportIdentity.Resolve(context.User, options.IdentityClaim);
        var storageConfigured = ReportConnectionRegistry.IsStoreConfigured(options.SavedReports);
        var databaseAccess = new DatabaseReportAccess(false, false);
        if (storageConfigured)
        {
            try
            {
                databaseAccess = await context.RequestServices
                    .GetRequiredService<IReportAuthorizationStore>()
                    .GetReportAccess(definition.Name, identity, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
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
                user.Trim(), identity, StringComparison.Ordinal)) == true;
        if (restricted && !configuredGrant && !databaseAccess.UserGranted)
            return Results.NotFound();

        return null;
    }

    private static bool AuthorizationEquivalent(
        ReportAuthorization? left,
        ReportAuthorization? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null) return false;
        return string.Equals(left.Policy, right.Policy, StringComparison.Ordinal)
               && left.AllowAnonymous == right.AllowAnonymous
               && left.Restricted == right.Restricted
               && left.AdministratorsOnly == right.AdministratorsOnly
               && (left.Users ?? []).SequenceEqual(right.Users ?? [], StringComparer.Ordinal);
    }

    /// <summary>
    /// Applies one or more application actions after definition authorization. Every
    /// registered authorizer must grant every action. Administrator-required actions
    /// use the configured/database administrator union when nonempty; otherwise at
    /// least one application authorizer must affirmatively grant the operation.
    /// </summary>
    private static async Task<IResult?> AuthorizeOperations(
        HttpContext context,
        IReadOnlyCollection<InteractiveReportAction> actions,
        InteractiveReportAuthorizationResource resource,
        bool administratorRequired,
        bool hideDenied,
        string? denialDetail,
        CancellationToken ct)
    {
        var options = context.RequestServices
            .GetRequiredService<IOptionsMonitor<InteractiveReportOptions>>().CurrentValue;
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
                    EndpointExtensions.Log(context)?.LogError(
                        ex,
                        "Report {Report}: authorization for {Action} failed (traceId {TraceId})",
                        resource.ReportName,
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

    /// <summary>
    /// Null when the feature is whitelisted. 403 (not 404) because the caller already
    /// reached an existing, authorized report — only this capability is switched off.
    /// </summary>
    public IResult? RequireFeature(ReportDefinition definition, string feature)
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
    public async Task<bool> MayRequestAdministration(
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
        var configuredGrant = ReportIdentity.IsAdministrator(
            context.User, options.IdentityClaim, options.Administrators);
        if (configuredGrant)
        {
            // A source-controlled grant is independently sufficient. Do not make a
            // known administrator's identity check depend on persistence health; the
            // requested administration operation will still fail when it reaches an
            // unavailable store.
            return new AdministratorDecision(Configured: true, Granted: true, Error: null);
        }
        try
        {
            var database = await context.RequestServices
                .GetRequiredService<IReportAuthorizationStore>()
                .GetAdministratorAccess(identity, ct);
            return new AdministratorDecision(
                Configured: options.Administrators.Count > 0 || database.Configured,
                Granted: database.UserGranted,
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
        EndpointExtensions.Log(context)?.LogError(
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

    public async Task<IReadOnlyDictionary<string, object?>> ResolveContextParameters(
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
