using System.Security.Claims;

namespace InteractiveReport.Core.Identity;

/// <summary>
/// Resolves the single canonical identity value the engine uses for saved-report
/// ownership and administrator matching. The whoami endpoint exists so an operator can
/// see this exact value before putting it in the administrators list.
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
    /// Case-insensitive exact match: operator-friendly for emails and usernames, and
    /// GUID/sub-style values don't collide under case folding in practice.
    /// </summary>
    public static bool IsAdministrator(ClaimsPrincipal? user, string? identityClaim, IReadOnlyCollection<string> administrators)
    {
        if (administrators.Count == 0) return false;
        var identity = Resolve(user, identityClaim);
        return identity is not null && administrators.Contains(identity, StringComparer.OrdinalIgnoreCase);
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
