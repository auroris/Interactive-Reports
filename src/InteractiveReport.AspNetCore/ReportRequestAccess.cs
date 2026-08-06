using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace InteractiveReport.AspNetCore;

/// <summary>
/// Applies report-level authorization and resolves trusted request context before an
/// endpoint enters the report engine.
/// </summary>
internal static class ReportRequestAccess
{
    /// <summary>
    /// Null means access is granted. Unauthenticated callers receive 401; authenticated
    /// callers failing a report policy receive 404 so the definition is not disclosed.
    /// </summary>
    public static async Task<IResult?> Authorize(ReportDefinition definition, HttpContext context)
    {
        var authorization = definition.Authorization;
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

        return null;
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
