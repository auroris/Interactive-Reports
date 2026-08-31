using System.Security.Claims;
using InteractiveReport.AspNetCore.Definitions;
using InteractiveReport.Core.SavedReports;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace InteractiveReport.AspNetCore;

/// <summary>Identifies the operation an Interactive Reports caller is attempting.</summary>
public enum InteractiveReportAction
{
    /// <summary>Open the report viewer and read schema metadata.</summary>
    ViewReport,
    /// <summary>Execute a report query.</summary>
    Query,
    /// <summary>Export report results.</summary>
    Export,
    /// <summary>List saved reports visible to the caller.</summary>
    ListSavedReports,
    /// <summary>Read one saved report.</summary>
    ReadSavedReport,
    /// <summary>Create a saved report.</summary>
    CreateSavedReport,
    /// <summary>Change saved-report metadata or state.</summary>
    UpdateSavedReport,
    /// <summary>Delete a saved report.</summary>
    DeleteSavedReport,
    /// <summary>Publish or unpublish a global report.</summary>
    PublishGlobalReport,
    /// <summary>Publish or unpublish a primary report.</summary>
    PublishPrimaryReport,
    /// <summary>Reassign saved-report ownership.</summary>
    ChangeSavedReportOwner,
    /// <summary>List every saved report for administration.</summary>
    ListAllSavedReports,
    /// <summary>List application accounts for authorization controls.</summary>
    ListAuthorizationUsers,
    /// <summary>Modify administrator, restriction, or report-user grants.</summary>
    ManageAuthorization,
    /// <summary>Download a report-document envelope.</summary>
    DownloadReportDocument,
    /// <summary>Upload a report-document envelope.</summary>
    UploadReportDocument,
}

/// <summary>
/// Immutable current saved-report metadata exposed to authorization code. The stored
/// state document is deliberately excluded.
/// </summary>
/// <param name="Id">The saved-report identifier.</param>
/// <param name="Title">The current display title.</param>
/// <param name="Owner">The canonical owner identity.</param>
/// <param name="IsGlobal">Whether the report is globally published.</param>
/// <param name="IsPrimary">Whether the report is published as a primary report.</param>
/// <param name="Origin">Whether the row originated from a user or configured document.</param>
public sealed record SavedReportAuthorizationResource(
    string Id,
    string Title,
    string? Owner,
    bool IsGlobal,
    bool IsPrimary,
    SavedReportOrigin Origin);

/// <summary>
/// The resource passed to callback and ASP.NET Core resource-based authorization.
/// SavedReport is immutable current metadata. Definition is the mutable, typed result
/// of applying the client request and is the object validated and persisted after
/// authorization.
/// </summary>
public sealed record InteractiveReportAuthorizationResource
{
    /// <summary>Gets the configured report name.</summary>
    public required string ReportName { get; init; }
    /// <summary>Gets immutable current saved-report metadata when the operation targets an existing row.</summary>
    public SavedReportAuthorizationResource? SavedReport { get; init; }
    /// <summary>Gets the mutable proposed saved-report definition when the operation creates or updates a row.</summary>
    public InteractiveReportDefinition? Definition { get; init; }
}

/// <summary>Contains one application authorization decision requested by Interactive Reports.</summary>
public sealed record InteractiveReportAuthorizationRequest
{
    /// <summary>Gets the current authenticated or anonymous principal.</summary>
    public required ClaimsPrincipal User { get; init; }
    /// <summary>Gets the operation being authorized.</summary>
    public required InteractiveReportAction Action { get; init; }
    /// <summary>Gets the report and optional saved-report resource being authorized.</summary>
    public required InteractiveReportAuthorizationResource Resource { get; init; }

    /// <summary>
    /// Gets the current request scope. It can resolve IAuthorizationService and any other
    /// scoped application service without capturing a startup service provider.
    /// </summary>
    public required IServiceProvider RequestServices { get; init; }
}

/// <summary>A direct integrator authorization callback. <see langword="true"/> grants access; <see langword="false"/> denies it.</summary>
/// <param name="request">The action, principal, resource, and request service scope to authorize.</param>
/// <param name="cancellationToken">Signals that authorization should be canceled.</param>
/// <returns>A task containing the authorization decision.</returns>
public delegate ValueTask<bool> InteractiveReportAuthorizationCallback(
    InteractiveReportAuthorizationRequest request,
    CancellationToken cancellationToken);

/// <summary>
/// ASP.NET Core requirement emitted by UseAspNetCoreAuthorization. Applications can
/// handle it with AuthorizationHandler&lt;InteractiveReportAuthorizationRequirement,
/// InteractiveReportAuthorizationResource&gt;.
/// </summary>
/// <param name="action">The Interactive Reports action being authorized.</param>
public sealed class InteractiveReportAuthorizationRequirement(
    InteractiveReportAction action) : IAuthorizationRequirement
{
    /// <summary>Gets the action the application's authorization handler must evaluate.</summary>
    public InteractiveReportAction Action { get; } = action;
}

/// <summary>
/// An optional control-flow exception for callbacks that express an expected denial
/// by throwing. Other exceptions are treated as authorization infrastructure errors.
/// </summary>
public sealed class InteractiveReportAuthorizationDeniedException : Exception
{
    /// <summary>
    /// Creates an authorization-denied exception with a default message and no inner exception.
    /// </summary>
    public InteractiveReportAuthorizationDeniedException()
        : base("Interactive Reports authorization denied the operation.")
    {
    }

    /// <summary>
    /// Creates an authorization-denied exception with a caller-supplied message.
    /// </summary>
    /// <param name="message">The denial message.</param>
    public InteractiveReportAuthorizationDeniedException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Creates an authorization-denied exception with a caller-supplied message and inner exception.
    /// </summary>
    /// <param name="message">The denial message.</param>
    /// <param name="innerException">The exception that led the callback to deny access.</param>
    public InteractiveReportAuthorizationDeniedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Internal adapter contract shared by direct callbacks and ASP.NET Core authorization.</summary>
internal interface IInteractiveReportAuthorizer
{
    /// <summary>
    /// Evaluates the configured authorization rule for a report operation.
    /// </summary>
    /// <param name="request">The complete application authorization request.</param>
    /// <param name="cancellationToken">Signals that the operation should be canceled.</param>
    /// <returns>A task whose result is <see langword="true"/> when access is granted; otherwise, <see langword="false"/>.</returns>
    ValueTask<bool> Authorize(
        InteractiveReportAuthorizationRequest request,
        CancellationToken cancellationToken);
}

/// <summary>Adapts a host-supplied callback to the internal authorizer contract.</summary>
/// <param name="callback">The callback to invoke for every authorization decision.</param>
internal sealed class CallbackInteractiveReportAuthorizer(
    InteractiveReportAuthorizationCallback callback) : IInteractiveReportAuthorizer
{
    /// <summary>
    /// Evaluates the configured authorization rule for a report operation.
    /// </summary>
    /// <param name="request">The complete application authorization request.</param>
    /// <param name="cancellationToken">Signals that the operation should be canceled.</param>
    /// <returns>A task whose result is <see langword="true"/> when access is granted; otherwise, <see langword="false"/>.</returns>
    public ValueTask<bool> Authorize(
        InteractiveReportAuthorizationRequest request,
        CancellationToken cancellationToken)
        => callback(request, cancellationToken);
}

/// <summary>Delegates Interactive Reports decisions to ASP.NET Core resource-based authorization.</summary>
internal sealed class AspNetCoreInteractiveReportAuthorizer : IInteractiveReportAuthorizer
{
    /// <summary>
    /// Evaluates the configured authorization rule for a report operation.
    /// </summary>
    /// <param name="request">The complete application authorization request.</param>
    /// <param name="cancellationToken">Signals that the operation should be canceled.</param>
    /// <returns>A task whose result is <see langword="true"/> when access is granted; otherwise, <see langword="false"/>.</returns>
    public async ValueTask<bool> Authorize(
        InteractiveReportAuthorizationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var authorization = request.RequestServices.GetRequiredService<IAuthorizationService>();
        var result = await authorization.AuthorizeAsync(
            request.User,
            request.Resource,
            new InteractiveReportAuthorizationRequirement(request.Action));
        return result.Succeeded;
    }
}
