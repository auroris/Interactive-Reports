using System.Security.Claims;
using InteractiveReport.Core.Authorization;
using InteractiveReport.Core.Definitions;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Identity;
using InteractiveReport.Core.Model;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InteractiveReport.AspNetCore;

/// <summary>
/// Transport-neutral information about the caller and request scope. Client adapters create
/// this value from their transport and the server never needs an <c>HttpContext</c>.
/// </summary>
public sealed record InteractiveReportRequestContext
{
    public required ClaimsPrincipal User { get; init; }
    public required IServiceProvider RequestServices { get; init; }
    public required string TraceIdentifier { get; init; }
}

/// <summary>Classifies an authorization failure without assigning transport status semantics.</summary>
public enum ReportAuthorizationFailureKind
{
    Unauthenticated,
    Forbidden,
    NotFound,
    Internal,
}

/// <summary>A stable, transport-neutral authorization failure.</summary>
public sealed record ReportAuthorizationFailure(
    ReportAuthorizationFailureKind Kind,
    string Code,
    string? Details = null,
    string? TraceIdentifier = null);

/// <summary>Contains the result of resolving and definition-authorizing one report.</summary>
public sealed record ReportDefinitionAccessResult(
    ReportDefinition? Definition,
    ReportAuthorizationFailure? Failure = null);

/// <summary>
/// Central authorization service shared by every client adapter. It owns definition,
/// administrator, stored-grant, ownership-resource, application-authorizer, feature, and
/// trusted-context decisions, but contains no route or response types.
/// </summary>
public interface IReportAuthorizationService
{
    Task<ReportDefinitionAccessResult> ResolveDefinition(
        string reportName,
        InteractiveReportRequestContext context,
        CancellationToken ct = default);

    Task<ReportAuthorizationFailure?> AuthorizeActions(
        ReportDefinition definition,
        IReadOnlyCollection<InteractiveReportAction> actions,
        InteractiveReportAuthorizationResource? resource,
        bool administratorRequired,
        bool hideDenied,
        string? denialDetail,
        InteractiveReportRequestContext context,
        CancellationToken ct = default);

    Task<ReportAuthorizationFailure?> AuthorizeEndpoint(
        IReadOnlyCollection<InteractiveReportAction> actions,
        InteractiveReportAuthorizationResource resource,
        bool administratorRequired,
        bool hideDenied,
        string? denialDetail,
        InteractiveReportRequestContext context,
        CancellationToken ct = default);

    ReportAuthorizationFailure? CheckFeature(ReportDefinition definition, string feature);

    Task<bool> MayRequestAdministration(
        InteractiveReportRequestContext context,
        CancellationToken ct = default);

    Task<IReadOnlyDictionary<string, object?>> ResolveContextParameters(
        ReportDefinition definition,
        InteractiveReportRequestContext context,
        CancellationToken ct = default);
}

internal sealed class ReportAuthorizationService(
    InteractiveReportLogging logging) : IReportAuthorizationService
{
    public async Task<ReportDefinitionAccessResult> ResolveDefinition(
        string reportName,
        InteractiveReportRequestContext context,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportName);
        ArgumentNullException.ThrowIfNull(context);

        var store = context.RequestServices.GetRequiredService<IReportDefinitionStore>();
        ReportDefinitionAuthorization? authorization = null;
        if (store is IReportDefinitionAuthorizationStore authorizationStore)
        {
            try
            {
                authorization = await authorizationStore.FindAuthorization(reportName, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new(null, Internal(
                    reportName,
                    "authorization metadata resolution",
                    context,
                    ex,
                    InteractiveReportErrorCodes.ReportExecutionFailed));
            }

            if (authorization is null) return new(null);
            try
            {
                if (await AuthorizeDefinition(authorization, context, ct) is { } denied)
                    return new(null, denied);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new(null, Internal(
                    authorization.Name, "definition authorization", context, ex));
            }
        }

        ReportDefinition? definition;
        try
        {
            definition = await store.Find(reportName, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new(null, Internal(
                reportName,
                "definition resolution",
                context,
                ex,
                InteractiveReportErrorCodes.ReportExecutionFailed));
        }

        if (definition is null) return new(null);
        if (authorization is null
            || !string.Equals(authorization.Name, definition.Name, StringComparison.OrdinalIgnoreCase)
            || !AuthorizationEquivalent(authorization.Authorization, definition.Authorization))
        {
            try
            {
                if (await AuthorizeDefinition(definition, context, ct) is { } denied)
                    return new(null, denied);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new(null, Internal(
                    definition.Name, "definition authorization", context, ex));
            }
        }

        return new(definition);
    }

    public Task<ReportAuthorizationFailure?> AuthorizeActions(
        ReportDefinition definition,
        IReadOnlyCollection<InteractiveReportAction> actions,
        InteractiveReportAuthorizationResource? resource,
        bool administratorRequired,
        bool hideDenied,
        string? denialDetail,
        InteractiveReportRequestContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var canonicalResource = resource is null
            ? new InteractiveReportAuthorizationResource { ReportName = definition.Name }
            : resource with { ReportName = definition.Name };
        return AuthorizeOperations(
            actions,
            canonicalResource,
            administratorRequired || definition.Authorization?.AdministratorsOnly == true,
            hideDenied || definition.Authorization?.AdministratorsOnly == true,
            denialDetail,
            context,
            ct);
    }

    public Task<ReportAuthorizationFailure?> AuthorizeEndpoint(
        IReadOnlyCollection<InteractiveReportAction> actions,
        InteractiveReportAuthorizationResource resource,
        bool administratorRequired,
        bool hideDenied,
        string? denialDetail,
        InteractiveReportRequestContext context,
        CancellationToken ct = default)
        => AuthorizeOperations(
            actions,
            resource,
            administratorRequired,
            hideDenied,
            denialDetail,
            context,
            ct);

    public ReportAuthorizationFailure? CheckFeature(ReportDefinition definition, string feature)
        => ReportFeatures.IsEnabled(definition, feature)
            ? null
            : new ReportAuthorizationFailure(
                ReportAuthorizationFailureKind.Forbidden,
                InteractiveReportErrorCodes.FeatureDisabled,
                $"'{feature}' is not enabled for this report");

    public async Task<bool> MayRequestAdministration(
        InteractiveReportRequestContext context,
        CancellationToken ct = default)
    {
        if (context.User.Identity?.IsAuthenticated != true) return false;
        var options = context.RequestServices
            .GetRequiredService<IOptionsMonitor<InteractiveReportOptions>>().CurrentValue;
        if (!ReportConnectionRegistry.IsStoreConfigured(options.SavedReports)) return false;
        var administrator = await AdministratorAccess(context, options, ct);
        if (administrator.Failure is not null)
            throw new InvalidOperationException("Administration access lookup failed.");
        if (administrator.Configured) return administrator.Granted;
        return context.RequestServices.GetServices<IInteractiveReportAuthorizer>().Any();
    }

    public async Task<IReadOnlyDictionary<string, object?>> ResolveContextParameters(
        ReportDefinition definition,
        InteractiveReportRequestContext context,
        CancellationToken ct = default)
    {
        if (definition.ContextParams is null || definition.ContextParams.Count == 0)
            return new Dictionary<string, object?>();

        var resolver = context.RequestServices.GetRequiredService<IContextParameterResolver>();
        var result = new Dictionary<string, object?>();
        foreach (var (parameterName, specification) in definition.ContextParams)
        {
            try
            {
                result[parameterName] = await resolver.Resolve(
                    parameterName,
                    specification,
                    context.User,
                    ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logging.Logger?.LogError(
                    ex,
                    "Failed to resolve context parameter '{Parameter}' for report '{Report}' (traceId {TraceId})",
                    parameterName,
                    definition.Name,
                    context.TraceIdentifier);
                throw;
            }
        }
        return result;
    }

    private async Task<ReportAuthorizationFailure?> AuthorizeDefinition(
        ReportDefinition definition,
        InteractiveReportRequestContext context,
        CancellationToken ct)
        => await AuthorizeDefinition(
            new ReportDefinitionAuthorization(definition.Name, definition.Authorization),
            context,
            ct);

    private async Task<ReportAuthorizationFailure?> AuthorizeDefinition(
        ReportDefinitionAuthorization definition,
        InteractiveReportRequestContext context,
        CancellationToken ct)
    {
        var authorization = definition.Authorization;
        var options = context.RequestServices
            .GetRequiredService<IOptionsMonitor<InteractiveReportOptions>>().CurrentValue;

        if (authorization?.AdministratorsOnly == true)
        {
            if (context.User.Identity?.IsAuthenticated != true)
            {
                logging.Logger?.LogDebug(
                    "Access denied for report '{Report}': caller is not authenticated for administrators-only report (traceId {TraceId})",
                    definition.Name,
                    context.TraceIdentifier);
                return Unauthenticated();
            }
            var administrator = await AdministratorAccess(context, options, ct);
            if (administrator.Failure is not null) return administrator.Failure;
            if (administrator.Configured && !administrator.Granted)
            {
                var id = ReportIdentity.Resolve(context.User, options.IdentityClaim);
                logging.Logger?.LogDebug(
                    "Access denied for report '{Report}': caller '{Identity}' is not an administrator (traceId {TraceId})",
                    definition.Name,
                    id ?? "anonymous",
                    context.TraceIdentifier);
                return Hidden();
            }
        }

        if (authorization?.AllowAnonymous == true) return null;
        if (context.User.Identity?.IsAuthenticated != true)
        {
            logging.Logger?.LogDebug(
                "Access denied for report '{Report}': caller is not authenticated (traceId {TraceId})",
                definition.Name,
                context.TraceIdentifier);
            return Unauthenticated();
        }

        if (authorization?.Policy is { Length: > 0 } policy)
        {
            var service = context.RequestServices.GetService<IAuthorizationService>()
                ?? throw new InvalidOperationException(
                    $"Report '{definition.Name}' declares policy '{policy}' but the host has not registered authorization services (AddAuthorization).");
            var decision = await service.AuthorizeAsync(context.User, policy);
            if (!decision.Succeeded)
            {
                logging.Logger?.LogDebug(
                    "Access denied for report '{Report}': ASP.NET Core authorization policy '{Policy}' failed for user '{User}' (traceId {TraceId})",
                    definition.Name,
                    policy,
                    context.User.Identity?.Name ?? "anonymous",
                    context.TraceIdentifier);
                return Hidden();
            }
        }

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
                return Internal(definition.Name, "report access lookup", context, ex);
            }
        }

        var restricted = authorization?.Restricted == true || databaseAccess.Restricted;
        var configuredGrant = identity is not null
            && authorization?.Users?.Any(user => string.Equals(
                user.Trim(), identity, StringComparison.Ordinal)) == true;
        if (restricted && !configuredGrant && !databaseAccess.UserGranted)
        {
            logging.Logger?.LogDebug(
                "Access denied for report '{Report}': report is restricted and caller '{Identity}' lacks a configured or database user grant (traceId {TraceId})",
                definition.Name,
                identity ?? "anonymous",
                context.TraceIdentifier);
            return Hidden();
        }
        return null;
    }

    private async Task<ReportAuthorizationFailure?> AuthorizeOperations(
        IReadOnlyCollection<InteractiveReportAction> actions,
        InteractiveReportAuthorizationResource resource,
        bool administratorRequired,
        bool hideDenied,
        string? denialDetail,
        InteractiveReportRequestContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(context);
        if (actions.Count == 0)
            throw new ArgumentException("At least one authorization action is required.", nameof(actions));

        var options = context.RequestServices
            .GetRequiredService<IOptionsMonitor<InteractiveReportOptions>>().CurrentValue;
        var authorizers = context.RequestServices
            .GetServices<IInteractiveReportAuthorizer>()
            .ToArray();

        if (administratorRequired)
        {
            if (context.User.Identity?.IsAuthenticated != true)
            {
                logging.Logger?.LogDebug(
                    "Actions {Actions} on resource '{Resource}' denied: caller is not authenticated (traceId {TraceId})",
                    string.Join(",", actions),
                    resource.ReportName,
                    context.TraceIdentifier);
                return Unauthenticated();
            }
            var administrator = await AdministratorAccess(context, options, ct);
            if (administrator.Failure is not null) return administrator.Failure;
            if (administrator.Configured && !administrator.Granted)
            {
                var id = ReportIdentity.Resolve(context.User, options.IdentityClaim);
                logging.Logger?.LogDebug(
                    "Actions {Actions} on resource '{Resource}' denied: caller '{Identity}' is not an administrator (traceId {TraceId})",
                    string.Join(",", actions),
                    resource.ReportName,
                    id ?? "anonymous",
                    context.TraceIdentifier);
                return Denied(context, hideDenied, denialDetail);
            }
            if (!administrator.Configured && authorizers.Length == 0)
            {
                logging.Logger?.LogDebug(
                    "Actions {Actions} on resource '{Resource}' denied: no administrators configured and no custom authorizers registered (traceId {TraceId})",
                    string.Join(",", actions),
                    resource.ReportName,
                    context.TraceIdentifier);
                return Denied(context, hideDenied, denialDetail);
            }
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
                    return Internal(resource.ReportName, $"authorization for {action}", context, ex);
                }

                if (!allowed)
                {
                    var id = ReportIdentity.Resolve(context.User, options.IdentityClaim);
                    logging.Logger?.LogDebug(
                        "Action '{Action}' on resource '{Resource}' was denied by authorizer '{AuthorizerType}' for caller '{Identity}' (traceId {TraceId})",
                        action,
                        resource.ReportName,
                        authorizer.GetType().Name,
                        id ?? "anonymous",
                        context.TraceIdentifier);
                    return Denied(context, hideDenied, denialDetail);
                }
            }
        }
        return null;
    }

    private async Task<AdministratorDecision> AdministratorAccess(
        InteractiveReportRequestContext context,
        InteractiveReportOptions options,
        CancellationToken ct)
    {
        var identity = ReportIdentity.Resolve(context.User, options.IdentityClaim);
        var configuredGrant = ReportIdentity.IsAdministrator(
            context.User, options.IdentityClaim, options.Administrators);
        if (configuredGrant) return new(true, true, null);

        try
        {
            var database = await context.RequestServices
                .GetRequiredService<IReportAuthorizationStore>()
                .GetAdministratorAccess(identity, ct);
            return new(
                options.Administrators.Count > 0 || database.Configured,
                database.UserGranted,
                null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new(false, false, Internal(
                SavedReportsListingDefinition.Name,
                "administrator access lookup",
                context,
                ex));
        }
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

    private ReportAuthorizationFailure Internal(
        string reportName,
        string operation,
        InteractiveReportRequestContext context,
        Exception exception,
        string code = InteractiveReportErrorCodes.AuthorizationFailed)
    {
        var dbEx = DbErrorClassifier.UnwrapDbException(exception);
        if (dbEx is not null || exception is System.Net.Sockets.SocketException || exception is TimeoutException)
        {
            var diagnosis = DbErrorClassifier.Classify(ReportDialect.SqlServer, exception);
            logging.Logger?.LogError(
                exception,
                "Report {Report}: {Operation} failed with database error (Category: {Category}, Code: {ProviderCode}, traceId {TraceId}): {Summary}. Hint: {Hint}",
                reportName,
                operation,
                diagnosis.Category,
                diagnosis.ProviderCode ?? "none",
                context.TraceIdentifier,
                diagnosis.Summary,
                diagnosis.RemediationHint ?? "Check database connection and authorization store table permissions.");
        }
        else
        {
            logging.Logger?.LogError(
                exception,
                "Report {Report}: {Operation} failed (traceId {TraceId})",
                reportName,
                operation,
                context.TraceIdentifier);
        }

        return new(
            ReportAuthorizationFailureKind.Internal,
            code,
            TraceIdentifier: context.TraceIdentifier);
    }

    private static ReportAuthorizationFailure Denied(
        InteractiveReportRequestContext context,
        bool hide,
        string? detail)
        => context.User.Identity?.IsAuthenticated != true
            ? Unauthenticated()
            : hide
                ? Hidden()
                : new(
                    ReportAuthorizationFailureKind.Forbidden,
                    InteractiveReportErrorCodes.AuthorizationDenied,
                    detail);

    private static ReportAuthorizationFailure Unauthenticated()
        => new(
            ReportAuthorizationFailureKind.Unauthenticated,
            InteractiveReportErrorCodes.AuthenticationRequired);

    private static ReportAuthorizationFailure Hidden()
        => new(
            ReportAuthorizationFailureKind.NotFound,
            InteractiveReportErrorCodes.ReportNotFound);

    private sealed record AdministratorDecision(
        bool Configured,
        bool Granted,
        ReportAuthorizationFailure? Failure);
}
