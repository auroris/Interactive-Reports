using InteractiveReport.AspNetCore;
using InteractiveReport.Client.FileDownload;
using InteractiveReport.Client.Json;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.Data.Sqlite;

namespace WindowsAuthentication
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
            ReportAccess access = new ReportAccess(builder.Configuration);

            // Interactive Reports consumes the host's identity. Negotiate lets ASP.NET Core
            // obtain a Windows principal from the caller and challenge requests without one.
            // Authorization then decides what that identified caller may do.
            builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme).AddNegotiate();
            builder.Services.AddAuthorization(access.ConfigurePolicies);

            InteractiveReportBuilder reports = builder.Services.AddInteractiveReports(builder.Configuration);
            // This hook adds application restrictions to report operations, including calls
            // made directly to the API. Hiding a button in the viewer would not enforce them.
            reports.UseAuthorization(access.Authorize);

            // Set a group to let Windows membership own the administrator decision.
            // Otherwise, use the bootstrap accounts and the admin page's editor.
            // Registering this callback replaces both of those sources: returning false
            // does not fall back to them. Leave it unregistered to retain editable grants.
            string? administratorGroup = builder.Configuration["ReportAccess:AdministratorGroup"];
            if (!string.IsNullOrWhiteSpace(administratorGroup))
            {
                reports.UseAdministrators(access.IsAdministrator);
            }

            // The viewer uses JSON endpoints. CSV delivery has its own client package,
            // which lets this example demonstrate an export-specific authorization rule.
            builder.Services.AddInteractiveReportJson();
            builder.Services.AddInteractiveReportFileDownload();

            // A file-backed database preserves saved documents and administrator grants
            // across restarts. Resolve it against the project so launching from another
            // shell directory does not silently select a different database.
            string dataDirectory = Path.Combine(builder.Environment.ContentRootPath, "App_Data");
            Directory.CreateDirectory(dataDirectory);

            SqliteConnectionStringBuilder connectionSettings = new SqliteConnectionStringBuilder();
            connectionSettings.DataSource = Path.Combine(dataDirectory, "reports.db");
            string connectionString = connectionSettings.ToString();

            // Both the orders report and SavedReports refer to SampleDb in appsettings.json.
            // Supply the computed path before startup validates that configuration. Provider
            // metadata remains in appsettings.json, where the connection's type is explicit.
            Dictionary<string, string?> settings = new Dictionary<string, string?>();
            settings.Add("ConnectionStrings:SampleDb", connectionString);
            builder.Configuration.AddInMemoryCollection(settings);
            await SampleData.EnsureCreated(connectionString);

            WebApplication app = builder.Build();
            // Authentication must populate HttpContext.User before authorization evaluates it.
            // Database access still uses the host process account; identifying a Windows
            // caller does not make this application impersonate that caller.
            app.UseAuthentication();
            app.UseAuthorization();
            // Require an identity at the API boundary, then let the report policies and
            // operation hooks make finer decisions. The packaged HTML and assets explicitly
            // allow anonymous delivery; report data still goes through the protected API.
            app.MapInteractiveReportJson("/api/reports").RequireAuthorization();
            app.MapInteractiveReportFileDownload("/api/download").RequireAuthorization();
            app.MapGet("/", OpenReport).RequireAuthorization();

            await app.RunAsync();
        }

        private static IResult OpenReport()
        {
            // The packaged viewer already supports saved documents and administration links,
            // so the host does not need a custom page to demonstrate those features.
            return Results.Redirect("/api/reports/orders/view");
        }
    }
}
