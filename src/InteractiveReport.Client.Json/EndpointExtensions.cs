// HTTP module entrypoint: registers the Interactive Reports REST surface and connects
// transport concerns to the engine. Endpoint handlers resolve identity, authorization,
// configured definitions, context parameters, and coded errors before invoking shared
// execution services.

using System.Text.Json;
using InteractiveReport.AspNetCore;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Expressions;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Validation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace InteractiveReport.Client.Json;

/// <summary>Provides the host entrypoint that maps the Interactive Reports HTTP surface.</summary>
public static class EndpointExtensions
{
    private const string ReportsTag = "Interactive Reports";
    private const string SavedReportsTag = "Interactive Reports - Saved Reports";
    private const string AdministrationTag = "Interactive Reports - Administration";

    /// <summary>
    /// Mounts the report endpoints and returns their group so hosts can chain standard conventions —
    /// .RequireAuthorization(...), antiforgery/CSRF filters for cookie-auth hosts, rate limiting, etc. The
    /// engine deliberately has no authentication mechanism of its own. Every data and
    /// security-administration endpoint enters the transport-neutral server boundary, which owns
    /// authorization for every client. The opt-in whoami bootstrap diagnostic and packaged
    /// HTML/CSS/JS delivery are the deliberate exceptions.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder on which to register the report routes.</param>
    /// <param name="prefix">The URL prefix under which to map the report routes; defaults to <c>"/api/reports"</c>.</param>
    /// <returns>The mapped route group, which the host can configure further.</returns>
    /// <remarks>Adds routes and endpoint filters to <paramref name="endpoints"/>.</remarks>
    /// <example>
    /// <code><![CDATA[
    /// app.MapInteractiveReportJson("/api/reports")
    ///     .RequireAuthorization("ReportingUsers")
    ///     .RequireRateLimiting("reports");
    /// ]]></code>
    /// </example>
    public static RouteGroupBuilder MapInteractiveReportJson(
        this IEndpointRouteBuilder endpoints,
        string prefix = "/api/reports")
        => MapInteractiveReportJsonCore(endpoints, prefix, logger: null);

    /// <summary>
    /// Mounts the endpoints and sends package logging to the supplied
    /// host-owned logger. The package does not create or configure logging providers.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder on which to register the report routes.</param>
    /// <param name="prefix">The URL prefix under which to map the report routes.</param>
    /// <param name="logger">The required host-provided logger that receives package diagnostic events.</param>
    /// <returns>The mapped route group, which the host can configure further.</returns>
    /// <exception cref="ArgumentNullException">Thrown when a required argument is <see langword="null"/>.</exception>
    /// <remarks>Adds routes and endpoint filters and installs <paramref name="logger"/> as the package logger.</remarks>
    public static RouteGroupBuilder MapInteractiveReportJson(
        this IEndpointRouteBuilder endpoints,
        string prefix,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        return MapInteractiveReportJsonCore(endpoints, prefix, logger);
    }

    /// <summary>
    /// Maps the complete REST, administration, viewer, and packaged-asset route surface.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder on which to register the report routes.</param>
    /// <param name="prefix">The URL prefix under which to map the report routes.</param>
    /// <param name="logger">The host-provided logger that receives diagnostic events; <see langword="null"/> disables logging.</param>
    /// <returns>The mapped route group, which the host can configure further.</returns>
    /// <remarks>Adds route handlers, metadata, and filters to <paramref name="endpoints"/> and optionally replaces the package logger.</remarks>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="endpoints"/> is <see langword="null"/>.</exception>
    private static RouteGroupBuilder MapInteractiveReportJsonCore(
        IEndpointRouteBuilder endpoints,
        string prefix,
        ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (logger is not null)
            endpoints.ServiceProvider.GetRequiredService<InteractiveReportLogging>().Use(logger);

        var group = endpoints.MapGroup(prefix);
        group.AddEndpointFilter(InteractiveReportLogging.LogRequest);
        group.AddEndpointFilter(static async (invocation, next) =>
        {
            // Data and identity responses are request-specific. Handlers may
            // replace this policy when their output is deliberately cacheable (the packaged UI
            // assets do so with no-cache + ETag).
            invocation.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(invocation);
        });
        ProtectedApi(
                group.MapGet("/{name}/schema", GetSchema),
                ReportsTag,
                "Get a report schema",
                "Returns the report's columns, default state, enabled features, and client capabilities.")
            .Produces<InteractiveReportSchema>();
        ProtectedApi(
                group.MapPost("/{name}/query", PostQuery),
                ReportsTag,
                "Query a report",
                "Executes a versioned report-state document against the configured report definition.")
            .Accepts<ReportState>("application/json")
            .Produces<ReportResult>()
            .Produces<InteractiveReportError>(StatusCodes.Status400BadRequest);
        ProtectedApi(
                group.MapPost("/{name}/lov", PostLov),
                ReportsTag,
                "List values for one report column",
                $"Compiles the supplied current report document and returns at most {ReportExecutor.MaxLovItems} "
                + "distinct values for one column of its active table. Optional search text performs a "
                + "case-insensitive partial match before the limit; no wildcard is required.")
            .Accepts<ReportLovRequest>("application/json")
            .Produces<ReportLovResult>()
            .Produces<InteractiveReportError>(StatusCodes.Status400BadRequest);
        // Packaged UI assets. Anonymous even when the host locks the group — see UiEndpoints.
        group.MapGet("/ui/{file}", UiEndpoints.Serve)
            .AllowAnonymous()
            .ExcludeFromDescription();

        // Packaged pages: anonymous shells like the assets — identical for any name (no
        // existence disclosure; the element's schema call is the gate). Disabled via
        // InteractiveReport:ViewerPagesEnabled. Literal-first routing means the existing /ui
        // and /saved segments shadow reports with those names at /view, as they already do on
        // the data routes.
        group.MapGet("/{name}/view", ViewerPageEndpoints.Report)
            .AllowAnonymous()
            .ExcludeFromDescription();
        group.MapGet("/admin", ViewerPageEndpoints.Admin)
            .AllowAnonymous()
            .ExcludeFromDescription();

        // Identity + saved reports (literal segments win over {name} in ASP.NET routing).
        Api(
                group.MapGet("/whoami", SavedReportEndpoints.Whoami),
                ReportsTag,
                "Inspect the current identity",
                "Bootstrap diagnostic that shows the exact caller identity used by Interactive Reports. The host must explicitly enable it.")
            .Produces<InteractiveReportIdentity>()
            .Produces<InteractiveReportError>(StatusCodes.Status404NotFound)
            .Produces<InteractiveReportError>(StatusCodes.Status500InternalServerError);
        ProtectedApi(
                group.MapGet("", SavedReportEndpoints.ListConfigurations),
                ReportsTag,
                "List available report configurations",
                "Lists appsettings report configurations the current caller may view.")
            .Produces<ReportConfigurationSummary[]>();
        ProtectedApi(
                WithStorageErrors(group.MapGet("/{name}", SavedReportEndpoints.ListForReport)),
                SavedReportsTag,
                "List saved reports",
                "Lists visible documents for one appsettings report configuration; administrators receive the complete family.")
            .Produces<SavedReportSummary[]>();
        ProtectedApi(
                WithStorageErrors(group.MapPost("/{id:long}/saved", SavedReportEndpoints.Save)),
                SavedReportsTag,
                "Create a saved report",
                "Creates a private or global saved report after validating the submitted state.")
            .Accepts<SaveReportRequest>("application/json")
            .Produces<SavedReportSummary>(StatusCodes.Status201Created)
            .Produces<InteractiveReportError>(StatusCodes.Status400BadRequest)
            .Produces<InteractiveReportError>(StatusCodes.Status409Conflict);
        ProtectedApi(
                WithStorageErrors(group.MapGet("/{name}/{id:long}", SavedReportEndpoints.Load)),
                SavedReportsTag,
                "Load a saved report",
                "Returns a visible saved-report document after verifying its configured family name.")
            .Produces<SavedReportDocument>();
        ProtectedApi(
                WithStorageErrors(group.MapPut("/{id:long}", SavedReportEndpoints.Update)),
                SavedReportsTag,
                "Update a saved report",
                "Changes selected saved-report properties. Publication and ownership changes require administrator authority.")
            .Accepts<UpdateSavedReportRequest>("application/json")
            .Produces<SavedReportSummary>()
            .Produces<InteractiveReportError>(StatusCodes.Status400BadRequest)
            .Produces<InteractiveReportError>(StatusCodes.Status409Conflict);
        ProtectedApi(
                WithStorageErrors(group.MapDelete("/{id:long}", SavedReportEndpoints.Delete)),
                SavedReportsTag,
                "Delete a saved report",
                "Deletes a user-authored saved report visible to the current caller.")
            .Produces(StatusCodes.Status204NoContent);
        ProtectedApi(
                group.MapGet("/admin/users", SavedReportEndpoints.AdminListUsers),
                AdministrationTag,
                "List authorization users",
                "Returns application-provided identity choices for authorization administration.")
            .Produces<InteractiveReportUser[]>();
        ProtectedApi(
                group.MapGet("/admin/authorization", AuthorizationEndpoints.List),
                AdministrationTag,
                "Get authorization configuration",
                "Returns configured and database-authored administrator, restriction, and user grants.")
            .Produces<InteractiveReportAuthorizationState>();
        ProtectedApi(
                group.MapPost("/admin/authorization/administrators", AuthorizationEndpoints.GrantAdministrator),
                AdministrationTag,
                "Grant administrator access",
                "Adds a database-authored administrator grant.")
            .Accepts<AuthorizationIdentityRequest>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<InteractiveReportError>(StatusCodes.Status400BadRequest);
        ProtectedApi(
                group.MapDelete("/admin/authorization/administrators", AuthorizationEndpoints.RevokeAdministrator),
                AdministrationTag,
                "Revoke administrator access",
                "Removes a database-authored administrator grant.")
            .Accepts<AuthorizationIdentityRequest>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<InteractiveReportError>(StatusCodes.Status400BadRequest);
        ProtectedApi(
                group.MapPut("/admin/authorization/reports/{name}", AuthorizationEndpoints.SetReportRestriction),
                AdministrationTag,
                "Set report restriction",
                "Controls whether the report requires an explicit per-user grant.")
            .Accepts<ReportRestrictionRequest>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<InteractiveReportError>(StatusCodes.Status400BadRequest);
        ProtectedApi(
                group.MapPost("/admin/authorization/reports/{name}/users", AuthorizationEndpoints.GrantReportUser),
                AdministrationTag,
                "Grant report access",
                "Adds a database-authored user grant for one report.")
            .Accepts<AuthorizationIdentityRequest>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<InteractiveReportError>(StatusCodes.Status400BadRequest);
        ProtectedApi(
                group.MapDelete("/admin/authorization/reports/{name}/users", AuthorizationEndpoints.RevokeReportUser),
                AdministrationTag,
                "Revoke report access",
                "Removes a database-authored user grant for one report.")
            .Accepts<AuthorizationIdentityRequest>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<InteractiveReportError>(StatusCodes.Status400BadRequest);
        ProtectedApi(
                WithStorageErrors(group.MapGet(
                    "/admin/saved/{id:long}/document", SavedReportEndpoints.AdminDownloadDocument)),
                AdministrationTag,
                "Download a report document",
                "Downloads a saved report as a source-controllable report-document envelope.")
            .Produces<ReportDocumentFile>(contentType: "application/json");
        ProtectedApi(
                WithStorageErrors(group.MapPost(
                    "/admin/{id:long}/documents", SavedReportEndpoints.AdminUploadDocument)),
                AdministrationTag,
                "Upload a report document",
                "Validates and imports a report-document envelope as a saved report.")
            .Accepts<ReportDocumentFile>("application/json")
            .Produces<SavedReportSummary>(StatusCodes.Status201Created)
            .Produces<InteractiveReportError>(StatusCodes.Status400BadRequest)
            .Produces<InteractiveReportError>(StatusCodes.Status409Conflict);

        return group;
    }

    /// <summary>
    /// Wraps a saved-report endpoint with the outer storage-error boundary. Handlers retain their
    /// domain-level exception translations. This outer boundary handles only errors that escape those
    /// translations, including a missing or unreachable store.
    /// </summary>
    /// <param name="endpoint">The saved-report route handler to wrap.</param>
    /// <returns>The same builder for further endpoint metadata configuration.</returns>
    /// <remarks>Adds an endpoint filter that preserves request cancellation and sanitizes otherwise unhandled storage failures.</remarks>
    private static RouteHandlerBuilder WithStorageErrors(RouteHandlerBuilder endpoint)
    {
        endpoint.AddEndpointFilter(async (invocation, next) =>
        {
            try
            {
                return await next(invocation);
            }
            catch (OperationCanceledException)
                when (invocation.HttpContext.RequestAborted.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var reportName = invocation.HttpContext.Request.RouteValues["name"]?.ToString()
                    ?? invocation.HttpContext.Request.RouteValues["id"]?.ToString()
                    ?? SavedReportsListingDefinition.Name;
                return ServerError(
                    invocation.HttpContext,
                    reportName,
                    "saved-report storage",
                    ex);
            }
        });
        return endpoint;
    }

    /// <summary>
    /// Adds OpenAPI tags, summary, and description metadata to a report endpoint.
    /// </summary>
    /// <param name="endpoint">The route handler to describe.</param>
    /// <param name="tag">The endpoint metadata tag used to group generated API documentation.</param>
    /// <param name="summary">The short operation summary exposed through OpenAPI.</param>
    /// <param name="description">The human-readable description exposed through endpoint metadata.</param>
    /// <returns>The same builder with OpenAPI metadata attached.</returns>
    /// <remarks>Mutates endpoint metadata.</remarks>
    private static RouteHandlerBuilder Api(
        RouteHandlerBuilder endpoint,
        string tag,
        string summary,
        string description)
        => endpoint
            .WithTags(tag)
            .WithSummary(summary)
            .WithDescription(description);

    /// <summary>
    /// Adds standard authentication and server-error response metadata to a protected report endpoint.
    /// </summary>
    /// <param name="endpoint">The protected route handler to describe.</param>
    /// <param name="tag">The endpoint metadata tag used to group generated API documentation.</param>
    /// <param name="summary">The short operation summary exposed through OpenAPI.</param>
    /// <param name="description">The human-readable description exposed through endpoint metadata.</param>
    /// <returns>The same builder with descriptive and common protected-error metadata attached.</returns>
    /// <remarks>Mutates endpoint metadata; it does not itself enforce authorization.</remarks>
    private static RouteHandlerBuilder ProtectedApi(
        RouteHandlerBuilder endpoint,
        string tag,
        string summary,
        string description)
        => Api(endpoint, tag, summary, description)
            .Produces<InteractiveReportError>(StatusCodes.Status401Unauthorized)
            .Produces<InteractiveReportError>(StatusCodes.Status403Forbidden)
            .Produces<InteractiveReportError>(StatusCodes.Status404NotFound)
            .Produces<InteractiveReportError>(StatusCodes.Status500InternalServerError);

    /// <summary>
    /// Discovers and returns the authorized schema for a report definition.
    /// </summary>
    /// <param name="name">The configured or built-in report-definition key from the route.</param>
    /// <param name="ctx">The current HTTP request and response context.</param>
    /// <param name="ct">Cancels authorization, context resolution, or schema discovery.</param>
    /// <returns>The schema JSON, an access result, or a sanitized server-error result.</returns>
    private static async Task<IResult> GetSchema(string name, HttpContext ctx, CancellationToken ct)
    {
        var schema = await Server(ctx).GetSchema(name, Context(ctx), ct);
        return schema.Failure is not null
            ? Failure(schema.Failure, ctx)
            : Results.Json(schema.Value, IrJson.Options);
    }

    /// <summary>
    /// Executes a posted report query through the shared request pipeline.
    /// </summary>
    /// <param name="name">The configured or built-in report-definition key from the route.</param>
    /// <param name="ctx">The current HTTP request and response context.</param>
    /// <param name="ct">Cancels body reading, authorization, context resolution, and query execution.</param>
    /// <returns>The report-result JSON or a standardized access, validation, or server-error result.</returns>
    /// <remarks>Consumes the JSON request body and may execute database commands.</remarks>
    private static async Task<IResult> PostQuery(string name, HttpContext ctx, CancellationToken ct)
    {
        ReportState state;
        try
        {
            state = await JsonSerializer.DeserializeAsync<ReportState>(ctx.Request.Body, IrJson.Options, ct)
                ?? new ReportState();
        }
        catch (JsonException ex)
        {
            // Returning the parser message is safe here because it references only caller-supplied JSON.
            return Error(
                InteractiveReportErrorCodes.MalformedReportState,
                StatusCodes.Status400BadRequest,
                ex.Message);
        }

        var queried = await Server(ctx).Query(name, state, Context(ctx), ct);
        return queried.Failure is not null
            ? Failure(queried.Failure, ctx)
            : Results.Json(queried.Value, IrJson.Options);
    }

    /// <summary>
    /// Executes a list-of-values request through the same definition and query authorization path as
    /// the current report table.
    /// </summary>
    /// <param name="name">The configured or built-in report-definition key from the route.</param>
    /// <param name="ctx">The current HTTP request and response context.</param>
    /// <param name="ct">Cancels body reading, authorization, context resolution, and lookup execution.</param>
    /// <returns>The bounded LOV JSON or a standardized access, validation, or server-error result.</returns>
    private static async Task<IResult> PostLov(string name, HttpContext ctx, CancellationToken ct)
    {
        ReportLovRequest request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<ReportLovRequest>(
                ctx.Request.Body,
                IrJson.Options,
                ct) ?? new ReportLovRequest();
        }
        catch (JsonException ex)
        {
            return Error(
                InteractiveReportErrorCodes.MalformedReportState,
                StatusCodes.Status400BadRequest,
                ex.Message);
        }

        var resolved = await Server(ctx).Lov(name, request, Context(ctx), ct);
        return resolved.Failure is not null
            ? Failure(resolved.Failure, ctx)
            : Results.Json(resolved.Value, IrJson.Options);
    }

    /// <summary>
    /// Resolves the transport-neutral server boundary from request services.
    /// </summary>
    /// <param name="ctx">The current HTTP request and response context.</param>
    /// <returns>The application-wide Interactive Reports server.</returns>
    internal static IInteractiveReportServer Server(HttpContext ctx)
        => ctx.RequestServices.GetRequiredService<IInteractiveReportServer>();

    /// <summary>
    /// Projects the current HTTP exchange onto the transport-neutral request context.
    /// </summary>
    /// <param name="ctx">The current HTTP request and response context.</param>
    /// <returns>The request context consumed by the server boundary.</returns>
    internal static InteractiveReportRequestContext Context(HttpContext ctx)
        => InteractiveReportHttpRequest.Context(ctx);

    /// <summary>
    /// Translates a transport-neutral server failure into its HTTP response.
    /// </summary>
    /// <param name="failure">The classified server failure.</param>
    /// <param name="ctx">The active HTTP exchange.</param>
    /// <returns>The HTTP result carrying the failure's stable code.</returns>
    internal static IResult Failure(InteractiveReportFailure failure, HttpContext ctx)
        => InteractiveReportHttpResult.Failure(failure, ctx);

    /// <summary>
    /// Builds an Interactive Reports HTTP error using the shared catalog and wire type.
    /// </summary>
    /// <param name="code">The stable protocol or diagnostic code to return.</param>
    /// <param name="statusCode">The HTTP status code to attach to the JSON result.</param>
    /// <param name="details">Optional request-specific details safe to expose to the caller.</param>
    /// <param name="traceId">Optional correlation id for server-side diagnostics.</param>
    /// <returns>A JSON result containing the stable code and catalog fallback text.</returns>
    internal static IResult Error(
        string code,
        int statusCode,
        string? details = null,
        string? traceId = null)
        => InteractiveReportHttpResult.Error(code, statusCode, details, traceId);

    /// <summary>
    /// Logs an unexpected exception with the request trace id and returns a sanitized server error.
    /// Definition resolution sits behind this error shaping because it validates configuration and
    /// synchronizes configured documents, so a mistake introduced by a live config reload must surface as
    /// the standard sanitized coded error rather than an unhandled 500. (Startup-time mistakes fail the host
    /// before traffic; see <c>InteractiveReportStartupValidator</c>.)
    /// </summary>
    /// <param name="ctx">The current HTTP request and response context.</param>
    /// <param name="reportName">The configured report name whose definition or saved reports are being addressed.</param>
    /// <param name="operation">The human-readable operation included in the server log.</param>
    /// <param name="ex">The full exception retained in server diagnostics.</param>
    /// <returns>A generic HTTP 500 JSON result containing only the request trace id.</returns>
    /// <remarks>Emits an error log when package logging is enabled.</remarks>
    internal static IResult ServerError(HttpContext ctx, string reportName, string operation, Exception ex)
    {
        Log(ctx)?.LogError(ex, "Report {Report}: {Operation} failed (traceId {TraceId})",
            reportName, operation, ctx.TraceIdentifier);

        return Error(
            InteractiveReportErrorCodes.ReportExecutionFailed,
            StatusCodes.Status500InternalServerError,
            traceId: ctx.TraceIdentifier);
    }

    /// <summary>
    /// Resolves the optional package logger associated with the current application.
    /// </summary>
    /// <param name="context">The current HTTP request and response context.</param>
    /// <returns>The configured host-owned logger, or <see langword="null"/> when package logging is disabled.</returns>
    internal static ILogger? Log(HttpContext context)
        => context.RequestServices.GetRequiredService<InteractiveReportLogging>().Logger;
}
