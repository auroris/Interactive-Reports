using System.Security.Claims;

namespace InteractiveReport.AspNetCore;

/// <summary>
/// One application account exposed to Interactive Reports administration. Display is
/// presentation text; Value is the canonical string ASP.NET Core and Interactive
/// Reports use to identify the account (the same kind of value shown by whoami).
/// </summary>
public sealed record InteractiveReportUser(string Display, string Value);

/// <summary>
/// Optional application user directory for administration list-of-values controls.
/// Returning null or an empty collection keeps the administration UI's free-form
/// identity entry. This directory supplies choices only; it does not grant access.
/// </summary>
public interface IInteractiveReportUserProvider
{
    ValueTask<IReadOnlyCollection<InteractiveReportUser>?> GetUsers(
        ClaimsPrincipal administrator,
        CancellationToken cancellationToken = default);
}
