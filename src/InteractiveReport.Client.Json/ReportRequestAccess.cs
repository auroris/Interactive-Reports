using InteractiveReport.Core.Model;
using InteractiveReport.AspNetCore;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace InteractiveReport.Client.Json;

/// <summary>Describes one endpoint-facing authorization request.</summary>
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

/// <summary>Contains deferred endpoint input needed by resource-based authorization.</summary>
public sealed record ReportAccessResourcePreparation(
    InteractiveReportAuthorizationResource? Resource,
    IResult? Error = null,
    bool AdministratorRequired = false);

/// <summary>Contains either the authorized definition or the HTTP result that stopped access.</summary>
public sealed record ReportAccessResult(ReportDefinition? Definition, IResult? Error);

/// <summary>Describes authorization for an endpoint without a report definition.</summary>
public sealed record EndpointAccessRequest
{
    public required IReadOnlyCollection<InteractiveReportAction> Actions { get; init; }
    public required InteractiveReportAuthorizationResource Resource { get; init; }
    public bool AdministratorRequired { get; init; }
    public bool HideDenied { get; init; }
    public string? DenialDetail { get; init; }
}

/// <summary>
/// HTTP adapter over the central transport-neutral authorization service. Client packages
/// use this contract when endpoint preparation itself can produce an HTTP result.
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

internal sealed class ReportAccessService(
    IReportAuthorizationService authorization) : IReportAccessService
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

        var requestContext = Context(context);
        var resolved = await authorization.ResolveDefinition(request.ReportName, requestContext, ct);
        if (resolved.Failure is not null)
            return new(null, Failure(resolved.Failure));
        if (resolved.Definition is null)
            return new(null, EndpointExtensions.ReportNotFound());
        var definition = resolved.Definition;

        var resource = request.Resource;
        var preparedAdministratorRequired = false;
        if (request.PrepareResource is not null)
        {
            var prepared = await request.PrepareResource(definition, ct);
            if (prepared.Error is not null) return new(null, prepared.Error);
            resource = prepared.Resource;
            preparedAdministratorRequired = prepared.AdministratorRequired;
        }

        var denied = await authorization.AuthorizeActions(
            definition,
            request.Actions,
            resource,
            request.AdministratorRequired || preparedAdministratorRequired,
            request.HideDenied,
            request.DenialDetail,
            requestContext,
            ct);
        if (denied is not null)
        {
            EndpointExtensions.Log(context)?.LogDebug(
                "Authorization denied for report {Report} actions {Actions} (traceId {TraceId})",
                definition.Name,
                string.Join(",", request.Actions),
                context.TraceIdentifier);
            return new(null, Failure(denied));
        }

        if (request.AdditionalAdministratorActions is not null)
        {
            var canonicalResource = resource is null
                ? new InteractiveReportAuthorizationResource { ReportName = definition.Name }
                : resource with { ReportName = definition.Name };
            var authorized = request.Actions.ToHashSet();
            while (true)
            {
                var next = request.AdditionalAdministratorActions(canonicalResource)
                    .Where(action => !authorized.Contains(action))
                    .Select(action => (InteractiveReportAction?)action)
                    .FirstOrDefault();
                if (!next.HasValue) break;

                denied = await authorization.AuthorizeActions(
                    definition,
                    [next.Value],
                    canonicalResource,
                    administratorRequired: true,
                    hideDenied: request.HideDenied,
                    denialDetail: request.DenialDetail,
                    requestContext,
                    ct);
                if (denied is not null) return new(null, Failure(denied));
                authorized.Add(next.Value);
            }
        }

        EndpointExtensions.Log(context)?.LogDebug(
            "Authorization granted for report {Report} actions {Actions} (traceId {TraceId})",
            definition.Name,
            string.Join(",", request.Actions),
            context.TraceIdentifier);
        return new(definition, null);
    }

    public async Task<IResult?> AuthorizeEndpoint(
        EndpointAccessRequest request,
        HttpContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        var denied = await authorization.AuthorizeEndpoint(
            request.Actions,
            request.Resource,
            request.AdministratorRequired,
            request.HideDenied,
            request.DenialDetail,
            Context(context),
            ct);
        return denied is null ? null : Failure(denied);
    }

    public IResult? RequireFeature(ReportDefinition definition, string feature)
    {
        var failure = authorization.CheckFeature(definition, feature);
        return failure is null ? null : Failure(failure);
    }

    public Task<bool> MayRequestAdministration(
        HttpContext context,
        CancellationToken ct = default)
        => authorization.MayRequestAdministration(Context(context), ct);

    public Task<IReadOnlyDictionary<string, object?>> ResolveContextParameters(
        ReportDefinition definition,
        HttpContext context,
        CancellationToken ct = default)
        => authorization.ResolveContextParameters(definition, Context(context), ct);

    private static InteractiveReportRequestContext Context(HttpContext context)
        => new()
        {
            User = context.User,
            RequestServices = context.RequestServices,
            TraceIdentifier = context.TraceIdentifier,
        };

    internal static IResult Failure(ReportAuthorizationFailure failure)
        => failure.Kind switch
        {
            ReportAuthorizationFailureKind.Unauthenticated => EndpointExtensions.AuthenticationRequired(),
            ReportAuthorizationFailureKind.NotFound => EndpointExtensions.ReportNotFound(),
            ReportAuthorizationFailureKind.Forbidden => EndpointExtensions.Error(
                failure.Code,
                StatusCodes.Status403Forbidden,
                failure.Details,
                failure.TraceIdentifier),
            _ => EndpointExtensions.Error(
                failure.Code,
                StatusCodes.Status500InternalServerError,
                failure.Details,
                failure.TraceIdentifier),
        };
}
