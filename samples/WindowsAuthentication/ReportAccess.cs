using System.Security.Claims;
using InteractiveReport.AspNetCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;

namespace WindowsAuthentication
{
    // Application authorization rules use the authenticated Windows principal.
    public class ReportAccess
    {
        private readonly IConfiguration configuration;

        public ReportAccess(IConfiguration configuration)
        {
            this.configuration = configuration;
        }

        public void ConfigurePolicies(AuthorizationOptions options)
        {
            // New host endpoints should require a signed-in caller unless they deliberately
            // choose another policy or allow anonymous access.
            AuthorizationPolicyBuilder defaultPolicy = new AuthorizationPolicyBuilder();
            defaultPolicy.RequireAuthenticatedUser();
            options.FallbackPolicy = defaultPolicy.Build();

            // The orders definition names Reports.Read in appsettings.json. Keeping this
            // rule in the host lets access depend on the organization's Windows groups
            // without putting that organization-specific logic into the reporting library.
            AuthorizationPolicyBuilder readerPolicy = new AuthorizationPolicyBuilder();
            readerPolicy.RequireAuthenticatedUser();
            readerPolicy.RequireAssertion(CanRead);
            options.AddPolicy("Reports.Read", readerPolicy.Build());
        }

        public bool CanRead(AuthorizationHandlerContext context)
        {
            ClaimsPrincipal user = context.User;
            if (user.Identity == null || !user.Identity.IsAuthenticated)
            {
                return false;
            }

            // An empty reader group admits every authenticated Windows user, allowing a
            // first run without creating a group. Configure one to narrow report access.
            string? readerGroup = configuration["ReportAccess:ReaderGroup"];
            if (string.IsNullOrWhiteSpace(readerGroup))
            {
                return true;
            }

            return IsGroupMember(user, readerGroup);
        }

        public ValueTask<bool> IsAdministrator(
            InteractiveReportAdministratorRequest request,
            CancellationToken cancellationToken)
        {
            // Only registered when the host delegates administrator membership to Windows.
            // Administrator authority does not bypass the reader policy or operation hook.
            cancellationToken.ThrowIfCancellationRequested();
            string? administratorGroup = configuration["ReportAccess:AdministratorGroup"];
            bool allowed = IsGroupMember(request.User, administratorGroup);
            return new ValueTask<bool>(allowed);
        }

        public ValueTask<bool> Authorize(
            InteractiveReportAuthorizationRequest request,
            CancellationToken cancellationToken)
        {
            // Respect a cancelled request before doing authorization work. If these rules
            // later consult an external ACL service, pass this token to that service too.
            cancellationToken.ThrowIfCancellationRequested();
            if (request.User.Identity == null || !request.User.Identity.IsAuthenticated)
            {
                return new ValueTask<bool>(false);
            }

            // Inspect request.Resource.ReportName, SavedReport, or Candidate here for
            // application ACLs. Built-in ownership and administrator checks still apply.
            // Resolve request-scoped services through request.RequestServices rather than
            // capturing them when this long-lived callback is registered at startup.
            switch (request.Action)
            {
                case InteractiveReportAction.Export:
                    // Permission to read data need not include permission to download it.
                    // Requiring an explicit exporter group demonstrates that distinction,
                    // and ensures administrator authority does not override the host's rule.
                    string? exporterGroup = configuration["ReportAccess:ExporterGroup"];
                    bool allowed = IsGroupMember(request.User, exporterGroup);
                    return new ValueTask<bool>(allowed);

                case InteractiveReportAction.ViewReport:
                case InteractiveReportAction.Query:
                case InteractiveReportAction.ListSavedReports:
                case InteractiveReportAction.ReadSavedReport:
                case InteractiveReportAction.CreateSavedReport:
                case InteractiveReportAction.UpdateSavedReport:
                case InteractiveReportAction.DeleteSavedReport:
                case InteractiveReportAction.PublishGlobalReport:
                case InteractiveReportAction.SelectDefaultReport:
                case InteractiveReportAction.ChangeSavedReportOwner:
                case InteractiveReportAction.ListAllSavedReports:
                case InteractiveReportAction.ListAuthorizationUsers:
                case InteractiveReportAction.ManageAdministrators:
                case InteractiveReportAction.DownloadReportDocument:
                case InteractiveReportAction.UploadReportDocument:
                    // True means this application adds no further restriction. It does not
                    // grant access to someone else's private document or make a reader an
                    // administrator; the library still enforces those boundaries.
                    return new ValueTask<bool>(true);

                default:
                    // A future library version may introduce new actions. Require an explicit
                    // decision about them here instead of granting them automatically.
                    return new ValueTask<bool>(false);
            }
        }

        private static bool IsGroupMember(ClaimsPrincipal user, string? group)
        {
            if (user.Identity == null || !user.Identity.IsAuthenticated)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(group))
            {
                return false;
            }

            // Ask the authenticated principal about membership instead of trusting a group
            // name sent by the browser. The host's Windows authentication supplies the roles.
            return user.IsInRole(group);
        }
    }
}
