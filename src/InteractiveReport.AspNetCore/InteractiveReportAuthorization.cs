using System.Security.Claims;
using InteractiveReport.AspNetCore.Definitions;
using InteractiveReport.Core.SavedReports;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace InteractiveReport.AspNetCore;

/// <summary>The operation an Interactive Reports caller is attempting.</summary>
public enum InteractiveReportAction
{
    ViewReport,
    Query,
    Export,
    ListSavedReports,
    ReadSavedReport,
    CreateSavedReport,
    UpdateSavedReport,
    DeleteSavedReport,
    PublishGlobalReport,
    PublishPrimaryReport,
    ChangeSavedReportOwner,
    ListAllSavedReports,
    DownloadReportDocument,
    UploadReportDocument,
}

/// <summary>
/// Immutable current saved-report metadata exposed to authorization code. The stored
/// state document is deliberately excluded.
/// </summary>
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
    public required string ReportName { get; init; }
    public SavedReportAuthorizationResource? SavedReport { get; init; }
    public InteractiveReportDefinition? Definition { get; init; }
}

/// <summary>One application authorization decision requested by Interactive Reports.</summary>
public sealed record InteractiveReportAuthorizationRequest
{
    public required ClaimsPrincipal User { get; init; }
    public required InteractiveReportAction Action { get; init; }
    public required InteractiveReportAuthorizationResource Resource { get; init; }

    /// <summary>
    /// The current request scope. It can resolve IAuthorizationService and any other
    /// scoped application service without capturing a startup service provider.
    /// </summary>
    public required IServiceProvider RequestServices { get; init; }
}

/// <summary>A direct integrator authorization callback. True grants; false denies.</summary>
public delegate ValueTask<bool> InteractiveReportAuthorizationCallback(
    InteractiveReportAuthorizationRequest request,
    CancellationToken cancellationToken);

/// <summary>
/// ASP.NET Core requirement emitted by UseAspNetCoreAuthorization. Applications can
/// handle it with AuthorizationHandler&lt;InteractiveReportAuthorizationRequirement,
/// InteractiveReportAuthorizationResource&gt;.
/// </summary>
public sealed class InteractiveReportAuthorizationRequirement(
    InteractiveReportAction action) : IAuthorizationRequirement
{
    public InteractiveReportAction Action { get; } = action;
}

/// <summary>
/// An optional control-flow exception for callbacks that express an expected denial
/// by throwing. Other exceptions are treated as authorization infrastructure errors.
/// </summary>
public sealed class InteractiveReportAuthorizationDeniedException : Exception
{
    public InteractiveReportAuthorizationDeniedException()
        : base("Interactive Reports authorization denied the operation.")
    {
    }

    public InteractiveReportAuthorizationDeniedException(string message)
        : base(message)
    {
    }

    public InteractiveReportAuthorizationDeniedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal interface IInteractiveReportAuthorizer
{
    ValueTask<bool> Authorize(
        InteractiveReportAuthorizationRequest request,
        CancellationToken cancellationToken);
}

internal sealed class CallbackInteractiveReportAuthorizer(
    InteractiveReportAuthorizationCallback callback) : IInteractiveReportAuthorizer
{
    public ValueTask<bool> Authorize(
        InteractiveReportAuthorizationRequest request,
        CancellationToken cancellationToken)
        => callback(request, cancellationToken);
}

internal sealed class AspNetCoreInteractiveReportAuthorizer : IInteractiveReportAuthorizer
{
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
