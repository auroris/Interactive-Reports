using System.Text.Json;
using InteractiveReport.AspNetCore;
using Microsoft.AspNetCore.Http;

namespace InteractiveReport.Client.Json;

/// <summary>
/// Transport adapters for the administrator-only authorization surface. Authorization, validation,
/// persistence, and failure classification all live on <see cref="IInteractiveReportServer"/>; these
/// methods only read the request body and shape the response.
/// </summary>
internal static class AuthorizationEndpoints
{
    /// <summary>Lists configured and database-authored authorization state.</summary>
    internal static async Task<IResult> List(HttpContext context, CancellationToken ct)
    {
        var listed = await EndpointExtensions.Server(context).ListAuthorizationState(
            EndpointExtensions.Context(context), ct);
        return listed.Failure is not null
            ? EndpointExtensions.Failure(listed.Failure, context)
            : Results.Json(listed.Value, IrJson.Options);
    }

    /// <summary>Grants database-authored administrator access to the identity in the request body.</summary>
    internal static Task<IResult> GrantAdministrator(HttpContext context, CancellationToken ct)
        => Mutate(context, (server, read, requestContext, token)
            => server.GrantAdministrator(read, requestContext, token), ct);

    /// <summary>Revokes database-authored administrator access from the identity in the request body.</summary>
    internal static Task<IResult> RevokeAdministrator(HttpContext context, CancellationToken ct)
        => Mutate(context, (server, read, requestContext, token)
            => server.RevokeAdministrator(read, requestContext, token), ct);

    /// <summary>Enables or disables the database-authored restriction for one configured report.</summary>
    internal static async Task<IResult> SetReportRestriction(
        string name,
        HttpContext context,
        CancellationToken ct)
    {
        var updated = await EndpointExtensions.Server(context).SetReportRestriction(
            name,
            async token => (await JsonSerializer.DeserializeAsync<ReportRestrictionRequest>(
                context.Request.Body, IrJson.Options, token))?.Restricted,
            EndpointExtensions.Context(context),
            ct);
        return updated.Failure is not null
            ? EndpointExtensions.Failure(updated.Failure, context)
            : Results.NoContent();
    }

    /// <summary>Grants the request-body identity database-authored access to one configured report.</summary>
    internal static Task<IResult> GrantReportUser(string name, HttpContext context, CancellationToken ct)
        => Mutate(context, (server, read, requestContext, token)
            => server.GrantReportUser(name, read, requestContext, token), ct);

    /// <summary>Revokes the request-body identity's database-authored access to one configured report.</summary>
    internal static Task<IResult> RevokeReportUser(string name, HttpContext context, CancellationToken ct)
        => Mutate(context, (server, read, requestContext, token)
            => server.RevokeReportUser(name, read, requestContext, token), ct);

    /// <summary>
    /// Runs one identity mutation. The body is read through a deferred callback so the server
    /// authorizes administration before the request is parsed.
    /// </summary>
    private static async Task<IResult> Mutate(
        HttpContext context,
        Func<
            IInteractiveReportServer,
            Func<CancellationToken, Task<string?>>,
            InteractiveReportRequestContext,
            CancellationToken,
            Task<InteractiveReportServerResult<bool>>> operation,
        CancellationToken ct)
    {
        var mutated = await operation(
            EndpointExtensions.Server(context),
            async token => (await JsonSerializer.DeserializeAsync<AuthorizationIdentityRequest>(
                context.Request.Body, IrJson.Options, token))?.Identity,
            EndpointExtensions.Context(context),
            ct);
        return mutated.Failure is not null
            ? EndpointExtensions.Failure(mutated.Failure, context)
            : Results.NoContent();
    }
}
