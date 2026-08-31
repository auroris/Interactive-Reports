using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Workbench;

/// <summary>
/// SAMPLE-ONLY authentication: every request is authenticated as the X-Workbench-User
/// header value (default "workbench-dev"). This is the harness's stand-in for the host
/// app's real identity provider — the engine only ever sees a ClaimsPrincipal. Switching
/// the header lets you act as different users when exercising ownership and admin paths.
/// </summary>
public sealed class DevAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>The authentication scheme registered by the workbench.</summary>
    public const string SchemeName = "WorkbenchDev";

    /// <summary>The request header used to choose the simulated identity.</summary>
    public const string UserHeader = "X-Workbench-User";

    /// <summary>The identity used when the request omits <see cref="UserHeader"/>.</summary>
    public const string DefaultUser = "workbench-dev";

    /// <summary>
    /// Creates the workbench authentication handler with the services required by ASP.NET Core.
    /// </summary>
    /// <param name="options">The monitor that supplies authentication scheme options.</param>
    /// <param name="logger">The factory used by the base authentication handler to create its logger.</param>
    /// <param name="encoder">The URL encoder required by the ASP.NET Core authentication handler.</param>
    public DevAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    /// <summary>
    /// Creates a successful authentication ticket from <see cref="UserHeader"/> or <see cref="DefaultUser"/>.
    /// </summary>
    /// <returns>A completed task containing the workbench authentication ticket.</returns>
    /// <remarks>Reads the current request headers but does not validate credentials.</remarks>
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var user = Request.Headers[UserHeader].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(user)) user = DefaultUser;

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, user), new Claim(ClaimTypes.Name, user)],
            Scheme.Name);

        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
