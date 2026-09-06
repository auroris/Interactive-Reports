using System.Security.Claims;

namespace InteractiveReport.AspNetCore;

/// <summary>Contains one administrator question put to the integrating application.</summary>
public sealed record InteractiveReportAdministratorRequest
{
    /// <summary>Gets the authenticated principal whose administrator authority is in question.</summary>
    public required ClaimsPrincipal User { get; init; }

    /// <summary>
    /// Gets the current request scope. It can resolve any scoped application service without
    /// capturing a startup service provider.
    /// </summary>
    public required IServiceProvider RequestServices { get; init; }
}

/// <summary>
/// The integrating application's answer to "does this caller administer Interactive Reports?".
/// Registering it makes the application the authority: the configured administrator list and the
/// database grants are then ignored. Not registering it, and not configuring
/// <c>InteractiveReport:AdministratorPolicy</c>, leaves those built-in fallbacks in charge.
/// </summary>
/// <param name="request">The authenticated principal and request scope.</param>
/// <param name="cancellationToken">Signals that the decision should be canceled.</param>
/// <returns>A task containing <see langword="true"/> for an administrator.</returns>
public delegate ValueTask<bool> InteractiveReportAdministratorCallback(
    InteractiveReportAdministratorRequest request,
    CancellationToken cancellationToken);

/// <summary>Names the source that granted a caller administrator authority, or <see cref="None"/>.</summary>
public enum InteractiveReportAdministratorSource
{
    /// <summary>The caller is not an administrator.</summary>
    None,
    /// <summary>The application's administrator callback answered yes.</summary>
    Application,
    /// <summary>The configured <c>AdministratorPolicy</c> succeeded.</summary>
    Policy,
    /// <summary>The caller is listed in <c>InteractiveReport:Administrators</c>.</summary>
    Configuration,
    /// <summary>The caller holds a database grant made in the administration center.</summary>
    Database,
}

/// <summary>Describes how a caller's administrator authority was decided.</summary>
/// <param name="IsAdministrator">Whether the caller administers Interactive Reports.</param>
/// <param name="Source">The source that granted authority, or <see cref="InteractiveReportAdministratorSource.None"/>.</param>
/// <param name="ManagedByApplication">Whether the application decides administrators, leaving the configured list and database grants inert.</param>
/// <param name="Failure">A lookup failure that prevented a decision, or <see langword="null"/>.</param>
public sealed record InteractiveReportAdministratorDecision(
    bool IsAdministrator,
    InteractiveReportAdministratorSource Source,
    bool ManagedByApplication,
    ReportAuthorizationFailure? Failure = null);

/// <summary>Internal contract for the application's registered administrator answer.</summary>
internal interface IInteractiveReportAdministrators
{
    /// <summary>
    /// Answers the administrator question for one authenticated caller.
    /// </summary>
    /// <param name="request">The authenticated principal and request scope.</param>
    /// <param name="cancellationToken">Signals that the decision should be canceled.</param>
    /// <returns>A task containing <see langword="true"/> for an administrator.</returns>
    ValueTask<bool> IsAdministrator(
        InteractiveReportAdministratorRequest request,
        CancellationToken cancellationToken);
}

/// <summary>Adapts a host-supplied administrator callback to the internal contract.</summary>
/// <param name="callback">The callback to invoke for every administrator question.</param>
internal sealed class CallbackInteractiveReportAdministrators(
    InteractiveReportAdministratorCallback callback) : IInteractiveReportAdministrators
{
    /// <inheritdoc />
    public ValueTask<bool> IsAdministrator(
        InteractiveReportAdministratorRequest request,
        CancellationToken cancellationToken)
        => callback(request, cancellationToken);
}
