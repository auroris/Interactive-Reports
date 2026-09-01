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
/// Describes one endpoint-facing authorization request. The service resolves the lightweight
/// authorization envelope, applies the definition gate, hydrates the executable
/// definition only after that succeeds, and finally evaluates the requested actions.
/// </summary>
public sealed record ReportAccessRequest
{
    /// <summary>Gets the case-insensitive report name to resolve and authorize.</summary>
    public required string ReportName { get; init; }
    /// <summary>Gets the operations that every registered application authorizer must grant.</summary>
    public required IReadOnlyCollection<InteractiveReportAction> Actions { get; init; }
    /// <summary>Gets optional resource details to pass to application authorization.</summary>
    public InteractiveReportAuthorizationResource? Resource { get; init; }
    /// <summary>Gets an optional callback that loads resource details after definition authorization but before operation authorization.</summary>
    public Func<ReportDefinition, CancellationToken, Task<ReportAccessResourcePreparation>>?
        PrepareResource { get; init; }
    /// <summary>Gets an optional callback that derives further administrator-only actions from the prepared resource.</summary>
    public Func<InteractiveReportAuthorizationResource, IEnumerable<InteractiveReportAction>>?
        AdditionalAdministratorActions { get; init; }
    /// <summary>Gets whether the requested operations require report-administrator access.</summary>
    public bool AdministratorRequired { get; init; }
    /// <summary>Gets whether operation denial should be returned as not found instead of forbidden.</summary>
    public bool HideDenied { get; init; }
    /// <summary>Gets optional caller-safe detail included with a visible forbidden response.</summary>
    public string? DenialDetail { get; init; }
}

/// <summary>
/// Contains deferred endpoint input needed by resource-based authorization. Preparation runs only after
/// the report-level gate and executable-definition hydration have succeeded.
/// </summary>
/// <param name="Resource">The hydrated resource to pass to operation authorization.</param>
/// <param name="Error">An endpoint result that stops authorization before the operation checks.</param>
/// <param name="AdministratorRequired">Whether the prepared resource requires administrator access.</param>
public sealed record ReportAccessResourcePreparation(
    InteractiveReportAuthorizationResource? Resource,
    IResult? Error = null,
    bool AdministratorRequired = false);

/// <summary>Contains either the authorized executable definition or the HTTP result that stopped access.</summary>
/// <param name="Definition">The authorized definition, present only on success.</param>
/// <param name="Error">The not-found, authentication, denial, or server-error result, present only on failure.</param>
public sealed record ReportAccessResult(ReportDefinition? Definition, IResult? Error);

/// <summary>
/// Describes authorization for a protected Interactive Reports endpoint that does not require a
/// report definition, such as authorization administration or user-directory lookup.
/// </summary>
public sealed record EndpointAccessRequest
{
    /// <summary>Gets the operations that every registered application authorizer must grant.</summary>
    public required IReadOnlyCollection<InteractiveReportAction> Actions { get; init; }
    /// <summary>Gets the resource supplied to application authorization.</summary>
    public required InteractiveReportAuthorizationResource Resource { get; init; }
    /// <summary>Gets whether the operation requires report-administrator access.</summary>
    public bool AdministratorRequired { get; init; }
    /// <summary>Gets whether denial should be returned as not found instead of forbidden.</summary>
    public bool HideDenied { get; init; }
    /// <summary>Gets optional caller-safe detail included with a visible forbidden response.</summary>
    public string? DenialDetail { get; init; }
}

/// <summary>
/// Defines the central access boundary for protected Interactive Reports transports. Endpoints make
/// one authorization call before protected execution, persistence, or provider work.
/// Host decision code plugs into this boundary through
/// <c>InteractiveReportBuilder.UseAuthorization</c> or
/// <c>InteractiveReportBuilder.UseAspNetCoreAuthorization</c>; host-owned endpoints can
/// resolve this service to apply the same report access contract.
/// </summary>
public interface IReportAccessService
{
    /// <summary>
    /// Evaluates the configured authorization rule for a report operation.
    /// </summary>
    /// <param name="request">The report, operations, and optional resource preparation to authorize.</param>
    /// <param name="context">The current HTTP request and response context.</param>
    /// <param name="ct">Cancels definition resolution, authorization, and resource preparation.</param>
    /// <returns>The authorized definition or an HTTP result explaining why processing must stop.</returns>
    Task<ReportAccessResult> Authorize(
        ReportAccessRequest request,
        HttpContext context,
        CancellationToken ct = default);

    /// <summary>
    /// Authorizes the current endpoint operation against its report resource.
    /// </summary>
    /// <param name="request">The actions, resource, and denial policy to authorize.</param>
    /// <param name="context">The current HTTP request and response context.</param>
    /// <param name="ct">Cancels administrator and application authorization.</param>
    /// <returns><see langword="null"/> when granted; otherwise, the HTTP result that must be returned.</returns>
    Task<IResult?> AuthorizeEndpoint(
        EndpointAccessRequest request,
        HttpContext context,
        CancellationToken ct = default);

    /// <summary>
    /// Requires an effective report feature before an authorized operation proceeds.
    /// </summary>
    /// <param name="definition">The authorized definition whose effective feature set should be checked.</param>
    /// <param name="feature">The report feature whose effective setting is being resolved.</param>
    /// <returns><see langword="null"/> when enabled; otherwise, a coded HTTP 403 result.</returns>
    IResult? RequireFeature(ReportDefinition definition, string feature);

    /// <summary>
    /// Determines whether the administration UI may offer controls to the current caller.
    /// </summary>
    /// <param name="context">The current HTTP request and response context.</param>
    /// <param name="ct">Cancels database administrator lookup.</param>
    /// <returns>A task whose result is <see langword="true"/> when the endpoint may request report-administrator authorization; otherwise, <see langword="false"/>.</returns>
    Task<bool> MayRequestAdministration(HttpContext context, CancellationToken ct = default);

    /// <summary>
    /// Resolves every configured context parameter for one request.
    /// </summary>
    /// <param name="definition">The authorized definition containing parameter specifications.</param>
    /// <param name="context">The current HTTP request and response context.</param>
    /// <param name="ct">Cancels application parameter resolution.</param>
    /// <returns>Resolved values keyed by the parameter names used in the base SQL.</returns>
    Task<IReadOnlyDictionary<string, object?>> ResolveContextParameters(
        ReportDefinition definition,
        HttpContext context,
        CancellationToken ct = default);
}

/// <summary>
/// Orders definition-level authentication, configured/database report gates, deferred resource loading,
/// administrator checks, and host application authorization so protected provider work occurs only after access succeeds.
/// </summary>
internal sealed class ReportAccessService : IReportAccessService
{
    /// <summary>
    /// Evaluates the configured authorization rule for a report operation.
    /// </summary>
    /// <param name="request">The report, operations, and optional resource preparation to authorize.</param>
    /// <param name="context">The current HTTP request and response context.</param>
    /// <param name="ct">Cancels definition resolution, authorization, and resource preparation.</param>
    /// <returns>The executable definition only when every required gate succeeds; otherwise, an HTTP result.</returns>
    /// <remarks>May read definition, saved-report, and authorization stores and emit diagnostic logs.</remarks>
    /// <exception cref="ArgumentException">Thrown when an argument does not satisfy the method's contract.</exception>
    /// <exception cref="ArgumentNullException">Thrown when a required argument is <see langword="null"/>.</exception>
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
            return new ReportAccessResult(null, EndpointExtensions.ReportNotFound());

        var suppliedResource = request.Resource;
        var preparedAdministratorRequired = false;
        if (request.PrepareResource is not null)
        {
            var prepared = await request.PrepareResource(definition, ct);
            if (prepared.Error is not null)
                return new ReportAccessResult(null, prepared.Error);
            suppliedResource = prepared.Resource;
            preparedAdministratorRequired = prepared.AdministratorRequired;
        }

        var resource = suppliedResource is null
            ? new InteractiveReportAuthorizationResource { ReportName = definition.Name }
            : suppliedResource with { ReportName = definition.Name };
        var denied = await AuthorizeOperations(
            context,
            request.Actions,
            resource,
            request.AdministratorRequired
                || preparedAdministratorRequired
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

    /// <summary>
    /// Authorizes the current endpoint operation against its report resource.
    /// </summary>
    /// <param name="request">The actions, resource, and denial policy to authorize.</param>
    /// <param name="context">The current HTTP request and response context.</param>
    /// <param name="ct">Cancels administrator and application authorization.</param>
    /// <returns><see langword="null"/> when granted; otherwise, the HTTP result that must be returned.</returns>
    /// <exception cref="ArgumentException">Thrown when an argument does not satisfy the method's contract.</exception>
    /// <exception cref="ArgumentNullException">Thrown when a required argument is <see langword="null"/>.</exception>
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
    /// lightweight authorization interface are gated before the executable definition is validated,
    /// connected, or hydrated from saved-report storage.
    /// </summary>
    /// <param name="store">The definition store, optionally supporting lightweight authorization envelopes.</param>
    /// <param name="name">The case-insensitive report name.</param>
    /// <param name="context">The current HTTP request and response context.</param>
    /// <param name="ct">Cancels metadata lookup, definition lookup, and report-level authorization.</param>
    /// <returns>The authorized definition, or a null definition paired with either no error for unknown names or a stopping HTTP result.</returns>
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
    /// Applies definition-level authentication, administrator-list, policy, restriction, and user-grant gates.
    /// Operation authorization is separate so mutation endpoints can hydrate the client-authored definition
    /// before passing it to the application authorizer.
    /// </summary>
    /// <param name="definition">The executable definition whose authorization block should be evaluated.</param>
    /// <param name="context">The current HTTP request and response context.</param>
    /// <param name="ct">Cancels policy and database authorization checks.</param>
    /// <returns><see langword="null"/> when the report-level gate succeeds; otherwise, an authentication, hidden-denial, or failure result.</returns>
    private static async Task<IResult?> AuthorizeDefinition(
        ReportDefinition definition,
        HttpContext context,
        CancellationToken ct)
        => await AuthorizeDefinition(
            new ReportDefinitionAuthorization(definition.Name, definition.Authorization),
            context,
            ct);

    /// <summary>
    /// Applies report-level gates using a lightweight authorization snapshot.
    /// </summary>
    /// <param name="definition">The canonical report name and detached authorization settings.</param>
    /// <param name="context">The current HTTP request and response context.</param>
    /// <param name="ct">Cancels ASP.NET policy and database authorization checks.</param>
    /// <returns><see langword="null"/> when access succeeds; otherwise, an authentication, hidden-denial, or failure result.</returns>
    /// <exception cref="InvalidOperationException">Thrown when a named ASP.NET policy is configured but authorization services are absent.</exception>
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
                return EndpointExtensions.AuthenticationRequired();
            var administrator = await AdministratorAccess(context, options, ct);
            if (administrator.Error is not null) return administrator.Error;
            if (administrator.Configured && !administrator.Granted)
                return EndpointExtensions.ReportNotFound();
        }

        if (authorization?.AllowAnonymous == true) return null;

        if (context.User.Identity?.IsAuthenticated != true)
            return EndpointExtensions.AuthenticationRequired();

        if (authorization?.Policy is { Length: > 0 } policy)
        {
            var service = context.RequestServices.GetService<IAuthorizationService>()
                ?? throw new InvalidOperationException(
                    $"Report '{definition.Name}' declares policy '{policy}' but the host has not registered authorization services (AddAuthorization).");
            var decision = await service.AuthorizeAsync(context.User, policy);
            if (!decision.Succeeded) return EndpointExtensions.ReportNotFound();
        }

        // Invariant: administrators-only definitions cannot also carry named-user restrictions.
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
            return EndpointExtensions.ReportNotFound();

        return null;
    }

    /// <summary>
    /// Determines whether two authorization snapshots grant the same effective access.
    /// </summary>
    /// <param name="left">The lightweight authorization snapshot.</param>
    /// <param name="right">The authorization attached to the hydrated definition.</param>
    /// <returns><see langword="true"/> when both snapshots are equivalent; otherwise, <see langword="false"/>.</returns>
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
    /// registered authorizer must grant every action. Administrator-required actions use the
    /// configured/database administrator union when nonempty; otherwise at least one application authorizer
    /// must affirmatively grant the operation.
    /// </summary>
    /// <param name="context">The current HTTP request and response context.</param>
    /// <param name="actions">The authorization actions that must all be granted for the request.</param>
    /// <param name="resource">The authorization or embedded resource being inspected or returned.</param>
    /// <param name="administratorRequired">Indicates whether the operation requires a report administrator.</param>
    /// <param name="hideDenied">Indicates whether access denial should be returned as not found.</param>
    /// <param name="denialDetail">The safe diagnostic detail included with an authorization denial.</param>
    /// <param name="ct">Cancels administrator lookup and host authorizers.</param>
    /// <returns><see langword="null"/> when every required check grants access; otherwise, the configured denial or failure result.</returns>
    /// <remarks>May query administrator persistence and invoke every registered application authorizer once per distinct action.</remarks>
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
                return EndpointExtensions.AuthenticationRequired();

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
                    return EndpointExtensions.Error(
                        InteractiveReportErrorCodes.AuthorizationFailed,
                        StatusCodes.Status500InternalServerError,
                        traceId: context.TraceIdentifier);
                }

                if (!allowed)
                    return Denied(context, hideDenied, denialDetail);
            }
        }

        return null;
    }

    /// <summary>
    /// Returns null when the feature is enabled. A disabled feature returns 403, not 404, because the caller already
    /// reached an existing, authorized report — only this capability is switched off.
    /// </summary>
    /// <param name="definition">The authorized definition whose effective feature set should be checked.</param>
    /// <param name="feature">The report feature whose effective setting is being resolved.</param>
    /// <returns><see langword="null"/> when enabled; otherwise, a coded HTTP 403 result.</returns>
    public IResult? RequireFeature(ReportDefinition definition, string feature)
        => ReportFeatures.IsEnabled(definition, feature)
            ? null
            : EndpointExtensions.Error(
                InteractiveReportErrorCodes.FeatureDisabled,
                StatusCodes.Status403Forbidden,
                $"'{feature}' is not enabled for this report");

    /// <summary>
    /// Computes a UI hint only. It never grants an operation: configured or database
    /// administrators may ask, and an application authorizer may make action-specific decisions when neither
    /// administrator source is populated.
    /// </summary>
    /// <param name="context">The current HTTP request and response context.</param>
    /// <param name="ct">Cancels database administrator lookup.</param>
    /// <returns>A task whose result is <see langword="true"/> when the endpoint may request report-administrator authorization; otherwise, <see langword="false"/>.</returns>
    /// <exception cref="InvalidOperationException">Thrown when administrator persistence fails.</exception>
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

    /// <summary>
    /// Evaluates the caller's report-administrator access.
    /// </summary>
    /// <param name="context">The current HTTP request and response context.</param>
    /// <param name="options">The current Interactive Reports authorization configuration.</param>
    /// <param name="ct">Cancels database administrator lookup.</param>
    /// <returns>Whether any administrator source is configured, whether this caller is granted, and any sanitized lookup error.</returns>
    /// <remarks>Queries the authorization store only when the caller lacks a source-controlled administrator grant.</remarks>
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
            // A source-controlled grant is independently sufficient. Do not make a known
            // administrator's identity check depend on persistence health; the requested
            // administration operation will still fail when it reaches an unavailable store.
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

    /// <summary>
    /// Logs an unexpected authorization exception and returns a sanitized authorization-failed result.
    /// </summary>
    /// <param name="context">The current HTTP request and response context.</param>
    /// <param name="reportName">The configured report name whose definition or saved reports are being addressed.</param>
    /// <param name="operation">The failing authorization operation included in the server log.</param>
    /// <param name="exception">The full exception retained in server diagnostics.</param>
    /// <returns>A coded HTTP 500 result containing only the request trace id.</returns>
    /// <remarks>Emits an error log when package logging is enabled.</remarks>
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
        return EndpointExtensions.Error(
            InteractiveReportErrorCodes.AuthorizationFailed,
            StatusCodes.Status500InternalServerError,
            traceId: context.TraceIdentifier);
    }

    /// <summary>Contains the combined configured/database administrator decision.</summary>
    /// <param name="Configured">Whether at least one authoritative administrator source contains grants.</param>
    /// <param name="Granted">Whether the current caller appears in either administrator source.</param>
    /// <param name="Error">A sanitized persistence failure result, if lookup failed.</param>
    private sealed record AdministratorDecision(bool Configured, bool Granted, IResult? Error);

    /// <summary>
    /// Creates the configured forbidden or hidden-not-found response.
    /// </summary>
    /// <param name="context">The current HTTP request and response context.</param>
    /// <param name="hide">Indicates whether the denied resource should be hidden as not found.</param>
    /// <param name="detail">The safe diagnostic detail included with an authorization denial.</param>
    /// <returns>The HTTP result to send to the client.</returns>
    private static IResult Denied(HttpContext context, bool hide, string? detail)
    {
        if (context.User.Identity?.IsAuthenticated != true)
            return EndpointExtensions.AuthenticationRequired();
        if (hide) return EndpointExtensions.ReportNotFound();
        return EndpointExtensions.Error(
            InteractiveReportErrorCodes.AuthorizationDenied,
            StatusCodes.Status403Forbidden,
            detail);
    }

    /// <summary>
    /// Resolves each configured context parameter against the current principal.
    /// </summary>
    /// <param name="definition">The authorized definition containing parameter specifications.</param>
    /// <param name="context">The current HTTP request and response context.</param>
    /// <param name="ct">Cancels application parameter resolution.</param>
    /// <returns>Resolved values keyed by the parameter names used in the base SQL, or an empty dictionary when none are configured.</returns>
    /// <remarks>Invokes the configured resolver once per parameter, in definition enumeration order.</remarks>
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
