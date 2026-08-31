using System.Security.Claims;

namespace InteractiveReport.Core.Identity;

/// <summary>
/// Resolves the single canonical identity value the engine uses for saved-report
/// ownership and administrator matching. The whoami endpoint exists so an operator can
/// see this exact value before using it in configuration or database grants.
/// </summary>
public static class ReportIdentity
{
    /// <summary>
    /// Resolves an explicit claim type when configured; otherwise uses NameIdentifier → "sub" → Identity.Name.
    /// Null when unauthenticated or no usable claim exists.
    /// </summary>
    /// <param name="user">The principal whose stable identity is required.</param>
    /// <param name="identityClaim">The configured claim type from which to resolve the report identity.</param>
    /// <returns>The resolved stable identity, or <see langword="null"/> when none is available.</returns>
    public static string? Resolve(ClaimsPrincipal? user, string? identityClaim)
    {
        if (user?.Identity?.IsAuthenticated != true) return null;

        if (!string.IsNullOrWhiteSpace(identityClaim))
            return NonEmpty(user.FindFirst(identityClaim)?.Value);

        return NonEmpty(user.FindFirst(ClaimTypes.NameIdentifier)?.Value)
            ?? NonEmpty(user.FindFirst("sub")?.Value)
            ?? NonEmpty(user.Identity.Name);
    }

    /// <summary>
    /// Determines whether the resolved identity exactly matches a configured administrator. Identity-provider subject values are opaque identifiers;
    /// changing their case can identify a different principal.
    /// </summary>
    /// <param name="user">The principal whose stable identity is required.</param>
    /// <param name="identityClaim">The configured claim type from which to resolve the report identity.</param>
    /// <param name="administrators">The configured administrator identity values.</param>
    /// <returns><see langword="true"/> when the resolved identity exactly matches a configured value; otherwise, <see langword="false"/>.</returns>
    public static bool IsAdministrator(ClaimsPrincipal? user, string? identityClaim, IReadOnlyCollection<string> administrators)
    {
        if (administrators.Count == 0) return false;
        var identity = Resolve(user, identityClaim);
        return identity is not null && administrators.Any(candidate => string.Equals(
            candidate?.Trim(), identity, StringComparison.Ordinal));
    }

    /// <summary>
    /// Normalizes a value to the non-empty identifier required by stable report identity.
    /// </summary>
    /// <param name="value">The optional identity text to trim and test for content.</param>
    /// <returns>The trimmed value, or <see langword="null"/> when it is empty.</returns>
    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
