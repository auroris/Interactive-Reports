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
    /// Explicit claim type wins when configured; otherwise NameIdentifier → "sub" →
    /// Identity.Name. Null when unauthenticated or no usable claim exists.
    /// </summary>
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
    /// Ordinal exact match. Identity-provider subject values are opaque identifiers;
    /// changing their case can identify a different principal.
    /// </summary>
    public static bool IsAdministrator(ClaimsPrincipal? user, string? identityClaim, IReadOnlyCollection<string> administrators)
    {
        if (administrators.Count == 0) return false;
        var identity = Resolve(user, identityClaim);
        return identity is not null && administrators.Any(candidate => string.Equals(
            candidate?.Trim(), identity, StringComparison.Ordinal));
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
