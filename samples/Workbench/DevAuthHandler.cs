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
    public const string SchemeName = "WorkbenchDev";
    public const string UserHeader = "X-Workbench-User";
    public const string DefaultUser = "workbench-dev";

    public DevAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

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
