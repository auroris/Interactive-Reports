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
    /// <summary>
    /// Lists the application accounts an administrator may select in authorization controls.
    /// </summary>
    /// <param name="administrator">The authenticated administrator requesting the directory.</param>
    /// <param name="cancellationToken">Signals that the operation should be canceled; defaults to <c>default</c>.</param>
    /// <returns>A task containing available accounts, or <see langword="null"/> to keep free-form identity entry.</returns>
    ValueTask<IReadOnlyCollection<InteractiveReportUser>?> GetUsers(
        ClaimsPrincipal administrator,
        CancellationToken cancellationToken = default);
}
