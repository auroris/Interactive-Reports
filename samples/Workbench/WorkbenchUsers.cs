using System.Security.Claims;
using InteractiveReport.AspNetCore;

namespace Workbench;

/// <summary>
/// SAMPLE-ONLY user directory behind the administration page's account pickers. A real host
/// queries its identity store (ASP.NET Core Identity, a directory, an HR system) in the
/// callback; the engine only ever sees the identities it returns. Account values match what
/// <see cref="DevAuthHandler"/> issues as NameIdentifier, so a picked account is the same
/// identity that account signs in with.
/// </summary>
public static class WorkbenchUsers
{
    /// <summary>The sample accounts, as (display name, account) pairs.</summary>
    public static readonly IReadOnlyList<(string Name, string Account)> All =
    [
        ("Workbench Developer", DevAuthHandler.DefaultUser),
        ("Ada Lovelace", "ada-lovelace"),
        ("Grace Hopper", "grace-hopper"),
        ("Margaret Hamilton", "margaret-hamilton"),
        ("Katherine Johnson", "katherine-johnson"),
        ("Radia Perlman", "radia-perlman"),
    ];

    /// <summary>
    /// Answers one administration lookup with the sample accounts matching the search text.
    /// </summary>
    /// <param name="search">The administrator, optional search text, and result limit.</param>
    /// <param name="cancellationToken">Signals that the lookup should be canceled.</param>
    /// <returns>Matching accounts as .NET identities, at most the requested limit.</returns>
    public static ValueTask<IEnumerable<ClaimsIdentity>?> Find(
        InteractiveReportUserSearch search,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var matches = All
            .Where(user => search.Search is null
                || user.Name.Contains(search.Search, StringComparison.OrdinalIgnoreCase)
                || user.Account.Contains(search.Search, StringComparison.OrdinalIgnoreCase))
            .Take(search.Limit)
            .Select(user => new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, user.Account),
                new Claim(ClaimTypes.Name, user.Name),
            ]))
            .ToArray();
        return ValueTask.FromResult<IEnumerable<ClaimsIdentity>?>(matches);
    }
}
