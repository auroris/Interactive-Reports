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
/// administrator, ownership-resource, application-authorizer, feature, and trusted-context
/// decisions, but contains no route or response types. Report access itself is the
/// integrating application's business: the engine only distinguishes public reports from
/// reports that need an authenticated caller, and lets the application narrow from there.
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

    /// <summary>
    /// Decides whether the caller administers Interactive Reports and where that authority comes
    /// from: the application's registered callback, the configured administrator policy, or the
    /// built-in fallbacks (the configured list, then the administration center's database grants).
    /// The first implemented source answers; an unauthenticated caller is never an administrator.
    /// </summary>
    Task<InteractiveReportAdministratorDecision> ResolveAdministrator(
        InteractiveReportRequestContext context,
        CancellationToken ct = default);

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
        // The built-in saved-reports listing is the one report that belongs to administrators.
        var listing = SavedReportsListingDefinition.Matches(definition.Name);
        return AuthorizeOperations(
            actions,
            canonicalResource,
            administratorRequired || listing,
            hideDenied || listing,
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

    public async Task<InteractiveReportAdministratorDecision> ResolveAdministrator(
        InteractiveReportRequestContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var options = context.RequestServices
            .GetRequiredService<IOptionsMonitor<InteractiveReportOptions>>().CurrentValue;
        var application = context.RequestServices.GetService<IInteractiveReportAdministrators>();
        var policy = string.IsNullOrWhiteSpace(options.AdministratorPolicy)
            ? null
            : options.AdministratorPolicy.Trim();
        var managed = application is not null || policy is not null;

        if (context.User.Identity?.IsAuthenticated != true)
            return new(false, InteractiveReportAdministratorSource.None, managed);

        // The first implemented source answers. Registering a callback, or naming a policy, is
        // how the application takes the question over; not doing either leaves the fallbacks.
        if (application is not null)
        {
            try
            {
                var granted = await application.IsAdministrator(
                    new InteractiveReportAdministratorRequest
                    {
                        User = context.User,
                        RequestServices = context.RequestServices,
                    },
                    ct);
                return new(
                    granted,
                    granted ? InteractiveReportAdministratorSource.Application : InteractiveReportAdministratorSource.None,
                    ManagedByApplication: true);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new(false, InteractiveReportAdministratorSource.None, true, Internal(
                    SavedReportsListingDefinition.Name, "application administrator decision", context, ex));
            }
        }

        if (policy is not null)
        {
            try
            {
                var service = context.RequestServices.GetService<IAuthorizationService>()
                    ?? throw new InvalidOperationException(
                        $"InteractiveReport:AdministratorPolicy names policy '{policy}' but the host has not registered authorization services (AddAuthorization).");
                var decision = await service.AuthorizeAsync(context.User, policy);
                return new(
                    decision.Succeeded,
                    decision.Succeeded ? InteractiveReportAdministratorSource.Policy : InteractiveReportAdministratorSource.None,
                    ManagedByApplication: true);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new(false, InteractiveReportAdministratorSource.None, true, Internal(
                    SavedReportsListingDefinition.Name, "administrator policy evaluation", context, ex));
            }
        }

        if (ReportIdentity.IsAdministrator(context.User, options.IdentityClaim, options.Administrators))
            return new(true, InteractiveReportAdministratorSource.Configuration, false);

        var identity = ReportIdentity.Resolve(context.User, options.IdentityClaim);
        if (identity is null || !ReportConnectionRegistry.IsStoreConfigured(options.SavedReports))
            return new(false, InteractiveReportAdministratorSource.None, false);

        try
        {
            var granted = await context.RequestServices
                .GetRequiredService<IAdministratorStore>()
                .IsAdministrator(identity, ct);
            return new(
                granted,
                granted ? InteractiveReportAdministratorSource.Database : InteractiveReportAdministratorSource.None,
                false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new(false, InteractiveReportAdministratorSource.None, false, Internal(
                SavedReportsListingDefinition.Name, "administrator grant lookup", context, ex));
        }
    }

    public async Task<bool> MayRequestAdministration(
        InteractiveReportRequestContext context,
        CancellationToken ct = default)
    {
        if (context.User.Identity?.IsAuthenticated != true) return false;
        var options = context.RequestServices
            .GetRequiredService<IOptionsMonitor<InteractiveReportOptions>>().CurrentValue;
        if (!ReportConnectionRegistry.IsStoreConfigured(options.SavedReports)) return false;
        var administrator = await ResolveAdministrator(context, ct);
        // The hint only shapes presentation. A failed lookup is logged by the resolver and reads
        // as "not offered" here; it must not turn every schema request into a failure.
        return administrator.Failure is null && administrator.IsAdministrator;
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

    /// <summary>
    /// Applies the report-level gate: a public report admits anyone, any other report needs an
    /// authenticated caller, an optional policy narrows further, and the built-in listing is
    /// hidden from everyone but administrators. Who else may see a report is decided by the
    /// application's own authorizers, not here.
    /// </summary>
    private async Task<ReportAuthorizationFailure?> AuthorizeDefinition(
        ReportDefinitionAuthorization definition,
        InteractiveReportRequestContext context,
        CancellationToken ct)
    {
        var authorization = definition.Authorization;

        if (SavedReportsListingDefinition.Matches(definition.Name))
        {
            if (context.User.Identity?.IsAuthenticated != true)
            {
                logging.Logger?.LogDebug(
                    "Access denied for report '{Report}': caller is not authenticated for the administrators-only listing (traceId {TraceId})",
                    definition.Name,
                    context.TraceIdentifier);
                return Unauthenticated();
            }
            var administrator = await ResolveAdministrator(context, ct);
            if (administrator.Failure is not null) return administrator.Failure;
            if (!administrator.IsAdministrator)
            {
                logging.Logger?.LogDebug(
                    "Access denied for report '{Report}': caller '{Identity}' is not an administrator (traceId {TraceId})",
                    definition.Name,
                    CallerForLog(context),
                    context.TraceIdentifier);
                return Hidden();
            }
            return null;
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

        var authorizers = context.RequestServices
            .GetServices<IInteractiveReportAuthorizer>()
            .ToArray();

        // Administrator authority is decided once, by the first implemented source. The
        // application's operation authorizers then keep their veto: they can narrow an
        // administrator's actions, never widen a non-administrator's.
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
            var administrator = await ResolveAdministrator(context, ct);
            if (administrator.Failure is not null) return administrator.Failure;
            if (!administrator.IsAdministrator)
            {
                logging.Logger?.LogDebug(
                    "Actions {Actions} on resource '{Resource}' denied: caller '{Identity}' is not an administrator (traceId {TraceId})",
                    string.Join(",", actions),
                    resource.ReportName,
                    CallerForLog(context),
                    context.TraceIdentifier);
                return Denied(context, resource, hideDenied, denialDetail);
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
                    logging.Logger?.LogDebug(
                        "Action '{Action}' on resource '{Resource}' was denied by authorizer '{AuthorizerType}' for caller '{Identity}' (traceId {TraceId})",
                        action,
                        resource.ReportName,
                        authorizer.GetType().Name,
                        CallerForLog(context),
                        context.TraceIdentifier);
                    return Denied(context, resource, hideDenied, denialDetail);
                }
            }
        }
        return null;
    }

    private static string CallerForLog(InteractiveReportRequestContext context)
    {
        var options = context.RequestServices
            .GetRequiredService<IOptionsMonitor<InteractiveReportOptions>>().CurrentValue;
        return ReportIdentity.Resolve(context.User, options.IdentityClaim) ?? "anonymous";
    }

    private static bool AuthorizationEquivalent(
        ReportAuthorization? left,
        ReportAuthorization? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null) return false;
        return string.Equals(left.Policy, right.Policy, StringComparison.Ordinal)
               && left.AllowAnonymous == right.AllowAnonymous;
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
                diagnosis.RemediationHint ?? "Check database connection and administrator table permissions.");
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
        InteractiveReportAuthorizationResource resource,
        bool hide,
        string? detail)
        => context.User.Identity?.IsAuthenticated != true
            ? Unauthenticated()
            : hide
                ? Hidden(resource)
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

    // A hidden denial reads exactly like the thing not existing. A document the caller may not
    // see therefore answers with the saved-report code: the report-level code would tell a
    // probing caller that the id is taken.
    private static ReportAuthorizationFailure Hidden(InteractiveReportAuthorizationResource resource)
        => resource.SavedReport is null
            ? Hidden()
            : new(
                ReportAuthorizationFailureKind.NotFound,
                InteractiveReportErrorCodes.SavedReportNotFound);
}
