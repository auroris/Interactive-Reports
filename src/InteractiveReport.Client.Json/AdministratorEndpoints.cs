using System.Text.Json;
using InteractiveReport.AspNetCore;
using Microsoft.AspNetCore.Http;

namespace InteractiveReport.Client.Json;

/// <summary>
/// Transport adapters for the administrator list. Authorization, validation, persistence, and
/// failure classification all live on <see cref="IInteractiveReportServer"/>; these methods only
/// read the request body and shape the response.
/// </summary>
internal static class AdministratorEndpoints
{
    /// <summary>Lists configured and database-authored administrators and who decides them.</summary>
    internal static async Task<IResult> List(HttpContext context, CancellationToken ct)
    {
        var listed = await EndpointExtensions.Server(context).ListAdministrators(
            EndpointExtensions.Context(context), ct);
        return listed.Failure is not null
            ? EndpointExtensions.Failure(listed.Failure, context)
            : Results.Json(listed.Value, IrJson.Options);
    }

    /// <summary>
    /// Replaces the database-authored administrator list with the identities in the request body.
    /// The body is read through a deferred callback so the server authorizes administration before
    /// the request is parsed.
    /// </summary>
    internal static async Task<IResult> Set(HttpContext context, CancellationToken ct)
    {
        var replaced = await EndpointExtensions.Server(context).SetAdministrators(
            async token => (await JsonSerializer.DeserializeAsync<AuthorizationIdentitiesRequest>(
                context.Request.Body, IrJson.Options, token))?.Identities,
            EndpointExtensions.Context(context),
            ct);
        return replaced.Failure is not null
            ? EndpointExtensions.Failure(replaced.Failure, context)
            : Results.NoContent();
    }
}
