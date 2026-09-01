using System.Security.Claims;
using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Execution;

/// <summary>
/// Resolves a definition's context parameters server-side from trusted request context.
/// Client-supplied values never reach these — they are the row-level security mechanism.
/// </summary>
/// <example>
/// <code><![CDATA[
/// public ValueTask<object?> Resolve(
///     string name, ContextParamSpec spec, ClaimsPrincipal? user, CancellationToken ct = default)
///     => name == "tenantId"
///         ? ValueTask.FromResult<object?>(user?.FindFirst("tenant_id")?.Value)
///         : throw new InvalidOperationException($"Unknown context parameter '{name}'.");
/// ]]></code>
/// </example>
public interface IContextParameterResolver
{
    /// <summary>
    /// Resolves one named context parameter from trusted server-side request context.
    /// </summary>
    /// <param name="name">The context-parameter name used in SQL bindings.</param>
    /// <param name="spec">The trusted source specification for the parameter.</param>
    /// <param name="user">The authenticated principal from which trusted values may be read.</param>
    /// <param name="ct">Signals that the operation should be canceled; defaults to <c>default</c>.</param>
    /// <returns>A task whose result is the resolved parameter value.</returns>
    ValueTask<object?> Resolve(string name, ContextParamSpec spec, ClaimsPrincipal? user, CancellationToken ct = default);
}

/// <summary>Default resolver that reads a configured claim from the authenticated principal.</summary>
public sealed class ClaimContextParameterResolver : IContextParameterResolver
{
    /// <summary>
    /// Resolves a context parameter from the configured claim on the authenticated principal.
    /// </summary>
    /// <param name="name">The context-parameter name used in validation errors.</param>
    /// <param name="spec">The specification containing the required claim type.</param>
    /// <param name="user">The authenticated principal from which the claim is read.</param>
    /// <param name="ct">Accepted for interface compatibility; claim lookup completes synchronously.</param>
    /// <returns>A task whose result is the claim value, or <see langword="null"/> when the claim is absent.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no claim type is configured for the parameter.</exception>
    public ValueTask<object?> Resolve(string name, ContextParamSpec spec, ClaimsPrincipal? user, CancellationToken ct = default)
    {
        if (spec.Claim is null)
            throw new InvalidOperationException(
                $"Context parameter '{name}' has no claim configured and no custom IContextParameterResolver is registered.");

        return ValueTask.FromResult<object?>(user?.FindFirst(spec.Claim)?.Value);
    }
}
