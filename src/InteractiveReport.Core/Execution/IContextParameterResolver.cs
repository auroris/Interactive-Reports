using System.Security.Claims;
using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Execution;

/// <summary>
/// Resolves a definition's context parameters server-side from the authenticated user.
/// Client-supplied values never reach these — they are the row-level security mechanism.
/// </summary>
public interface IContextParameterResolver
{
    ValueTask<object?> Resolve(string name, ContextParamSpec spec, ClaimsPrincipal? user, CancellationToken ct = default);
}

public sealed class ClaimContextParameterResolver : IContextParameterResolver
{
    public ValueTask<object?> Resolve(string name, ContextParamSpec spec, ClaimsPrincipal? user, CancellationToken ct = default)
    {
        if (spec.Claim is null)
            throw new InvalidOperationException(
                $"Context parameter '{name}' has no claim configured and no custom IContextParameterResolver is registered.");

        return ValueTask.FromResult<object?>(user?.FindFirst(spec.Claim)?.Value);
    }
}
