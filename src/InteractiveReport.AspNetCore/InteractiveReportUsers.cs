using System.Collections.Concurrent;
using System.Security.Claims;
using InteractiveReport.Core.Identity;
using Microsoft.Extensions.Options;

namespace InteractiveReport.AspNetCore;

/// <summary>
/// One application account exposed to Interactive Reports administration. Display is
/// presentation text; Value is the canonical string ASP.NET Core and Interactive
/// Reports use to identify the account (the same kind of value shown by whoami).
/// </summary>
public sealed record InteractiveReportUser(string Display, string Value)
{
    /// <summary>
    /// Projects a .NET identity onto a directory entry. The value is resolved through the same
    /// claim chain a signed-in principal uses (the configured identity claim, otherwise
    /// NameIdentifier → "sub" → Name), so the value an administrator picks from the directory is
    /// exactly the value that account resolves to when it signs in. The display is the identity's
    /// name, then a <c>name</c>, <c>preferred_username</c>, UPN, or email claim, then the value.
    /// </summary>
    /// <param name="identity">The identity describing one application account; it need not be authenticated.</param>
    /// <param name="identityClaim">The configured <c>InteractiveReport:IdentityClaim</c>, or <see langword="null"/> for the default chain.</param>
    /// <returns>The directory entry, or <see langword="null"/> when the identity carries no usable identity claim.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="identity"/> is <see langword="null"/>.</exception>
    public static InteractiveReportUser? FromIdentity(ClaimsIdentity identity, string? identityClaim = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var value = ReportIdentity.Resolve(identity, identityClaim)?.Trim();
        if (string.IsNullOrEmpty(value)) return null;

        var display = new[]
        {
            identity.Name,
            identity.FindFirst(ClaimTypes.Name)?.Value,
            identity.FindFirst("name")?.Value,
            identity.FindFirst("preferred_username")?.Value,
            identity.FindFirst(ClaimTypes.Upn)?.Value,
            identity.FindFirst(ClaimTypes.Email)?.Value,
            identity.FindFirst("email")?.Value,
        }.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate))?.Trim() ?? value;
        return new InteractiveReportUser(display, value);
    }
}

/// <summary>Describes one administration lookup against the application user directory.</summary>
public sealed record InteractiveReportUserSearch
{
    /// <summary>Gets the authenticated administrator performing the lookup.</summary>
    public required ClaimsPrincipal Administrator { get; init; }

    /// <summary>
    /// Gets the trimmed search text, or <see langword="null"/> when the administrator is browsing
    /// without a search. Match it as a case-insensitive partial match on display names and
    /// identity values; the engine applies the same match to whatever is returned.
    /// </summary>
    public string? Search { get; init; }

    /// <summary>
    /// Gets the most entries the administration UI shows for one lookup. Return at most this many
    /// best matches. A directory that returns this many is presented as truncated, so the
    /// administrator is asked to narrow the search.
    /// </summary>
    public required int Limit { get; init; }

    /// <summary>Gets the current request scope for resolving scoped application services.</summary>
    public required IServiceProvider RequestServices { get; init; }
}

/// <summary>Contains one bounded administration user lookup.</summary>
/// <param name="Items">Application-directory entries in directory order, then identities already known to Interactive Reports.</param>
/// <param name="Truncated">Whether more matching accounts exist than <paramref name="Items"/> carries.</param>
public sealed record InteractiveReportUserList(IReadOnlyList<InteractiveReportUser> Items, bool Truncated);

/// <summary>
/// Optional application user directory for administration account pickers. Returning null or
/// an empty collection leaves the picker with the identities the engine already knows plus
/// free-form identity entry. This directory supplies choices only; it does not grant access.
/// </summary>
public interface IInteractiveReportUserProvider
{
    /// <summary>
    /// Answers one administration lookup with the accounts matching its search text, at most
    /// its limit. The engine applies the same match and limit again, so a directory may also
    /// answer with more than it was asked for.
    /// </summary>
    /// <param name="search">The administrator, optional search text, result limit, and request scope.</param>
    /// <param name="cancellationToken">Signals that the operation should be canceled; defaults to <c>default</c>.</param>
    /// <returns>A task containing matching accounts, or <see langword="null"/> when the directory has none.</returns>
    ValueTask<IReadOnlyCollection<InteractiveReportUser>?> SearchUsers(
        InteractiveReportUserSearch search,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A direct integrator user-directory callback. It receives the administrator, the optional
/// search text, and the result limit, and answers with .NET identities describing matching
/// application accounts. Each identity is projected through
/// <see cref="InteractiveReportUser.FromIdentity"/>, so the values offered to administrators
/// are exactly what those accounts resolve to when they sign in. Return <see langword="null"/>
/// or an empty sequence when nothing matches.
/// </summary>
/// <param name="search">The administrator, optional search text, result limit, and request scope.</param>
/// <param name="cancellationToken">Signals that the lookup should be canceled.</param>
/// <returns>A task containing the matching identities.</returns>
public delegate ValueTask<IEnumerable<ClaimsIdentity>?> InteractiveReportUserDirectoryCallback(
    InteractiveReportUserSearch search,
    CancellationToken cancellationToken);

/// <summary>Adapts a host-supplied directory callback to the provider contract.</summary>
internal sealed class CallbackInteractiveReportUserProvider(
    InteractiveReportUserDirectoryCallback callback,
    IOptionsMonitor<InteractiveReportOptions> options) : IInteractiveReportUserProvider
{
    /// <summary>Invokes the callback and projects its identities onto directory entries.</summary>
    /// <exception cref="InvalidOperationException">Thrown when an identity carries no usable identity claim.</exception>
    public async ValueTask<IReadOnlyCollection<InteractiveReportUser>?> SearchUsers(
        InteractiveReportUserSearch search,
        CancellationToken cancellationToken = default)
    {
        var identities = await callback(search, cancellationToken);
        if (identities is null) return null;

        var identityClaim = options.CurrentValue.IdentityClaim;
        var users = new List<InteractiveReportUser>();
        foreach (var identity in identities)
        {
            if (identity is null) continue;
            users.Add(InteractiveReportUser.FromIdentity(identity, identityClaim)
                ?? throw new InvalidOperationException(
                    "The Interactive Reports user directory returned an identity with no usable identity claim. "
                    + (string.IsNullOrWhiteSpace(identityClaim)
                        ? "Each identity needs a NameIdentifier, sub, or Name claim."
                        : $"Each identity needs a '{identityClaim}' claim (InteractiveReport:IdentityClaim).")));
        }
        return users;
    }
}

/// <summary>
/// Memoizes the application directory's answer to a no-search browse per administrator, so
/// opening several account pickers in a row does not repeat a possibly remote directory query.
/// Searched lookups are never memoized, and the identities the engine knows from its own
/// storage are merged fresh on every request.
/// </summary>
internal sealed class UserDirectoryCache
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>Returns the unexpired directory answer stored under <paramref name="key"/>.</summary>
    public bool TryGet(string key, out IReadOnlyList<InteractiveReportUser> users)
    {
        if (_entries.TryGetValue(key, out var entry) && entry.ExpiresUtc > DateTime.UtcNow)
        {
            users = entry.Users;
            return true;
        }
        users = [];
        return false;
    }

    /// <summary>Stores a directory answer for <paramref name="lifetime"/>, evicting expired entries opportunistically.</summary>
    public void Set(string key, IReadOnlyList<InteractiveReportUser> users, TimeSpan lifetime)
    {
        var now = DateTime.UtcNow;
        foreach (var (existingKey, existing) in _entries)
        {
            if (existing.ExpiresUtc <= now) _entries.TryRemove(existingKey, out _);
        }
        _entries[key] = new Entry(now + lifetime, users);
    }

    private sealed record Entry(DateTime ExpiresUtc, IReadOnlyList<InteractiveReportUser> Users);
}
