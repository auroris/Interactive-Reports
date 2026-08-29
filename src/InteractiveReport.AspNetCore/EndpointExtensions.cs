using System.Text.Json;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Expressions;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Validation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace InteractiveReport.AspNetCore;

public static class EndpointExtensions
{
    private const string ReportsTag = "Interactive Reports";
    private const string SavedReportsTag = "Interactive Reports - Saved Reports";
    private const string AdministrationTag = "Interactive Reports - Administration";

    private delegate Task<IResult> StateOperation(
        HttpContext context,
        ReportDefinition definition,
        ReportExecutor executor,
        ReportState state,
        IReadOnlyDictionary<string, object?> contextParameters,
        CancellationToken ct);

    /// <summary>
    /// Mounts the report endpoints. Returns the group so hosts can chain standard
    /// conventions — .RequireAuthorization(...), antiforgery/CSRF filters for
    /// cookie-auth hosts, rate limiting, etc. The engine deliberately has no
    /// authentication mechanism of its own. Every data and security-administration
    /// endpoint enters IReportAccessService. The opt-in whoami bootstrap diagnostic
    /// and packaged HTML/CSS/JS delivery are the deliberate exceptions.
    /// </summary>
    public static RouteGroupBuilder MapInteractiveReports(
        this IEndpointRouteBuilder endpoints,
        string prefix = "/api/reports")
        => MapInteractiveReportsCore(endpoints, prefix, logger: null);

    /// <summary>
    /// Mounts the report endpoints and sends all package logging to the supplied
    /// host-owned logger. The package does not create or configure logging providers.
    /// </summary>
    public static RouteGroupBuilder MapInteractiveReports(
        this IEndpointRouteBuilder endpoints,
        string prefix,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        return MapInteractiveReportsCore(endpoints, prefix, logger);
    }

    private static RouteGroupBuilder MapInteractiveReportsCore(
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
            // Data and identity responses are request-specific. Handlers may replace
            // this policy when their output is deliberately cacheable (the packaged
            // UI assets do so with no-cache + ETag).
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
            .ProducesValidationProblem();
        ProtectedApi(
                group.MapPost("/{name}/export", PostExport),
                ReportsTag,
                "Export a report",
                "Executes the supplied report state without paging and returns CSV. A positive maxRows definition setting caps the output.")
            .Accepts<ReportState>("application/json")
            .Produces(StatusCodes.Status200OK, contentType: "text/csv")
            .ProducesProblem(StatusCodes.Status400BadRequest);

        // Packaged UI assets. Anonymous even when the host locks the group — see UiEndpoints.
        group.MapGet("/ui/{file}", UiEndpoints.Serve)
            .AllowAnonymous()
            .ExcludeFromDescription();

        // Packaged pages: anonymous shells like the assets — identical for any name
        // (no existence disclosure; the element's schema call is the gate). Disabled
        // via InteractiveReport:ViewerPagesEnabled. Literal-first routing means the
        // existing /ui and /saved segments shadow reports with those names at /view,
        // as they already do on the data routes.
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
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError);
        ProtectedApi(
                WithStorageErrors(group.MapGet("/{name}/saved", SavedReportEndpoints.ListForReport)),
                SavedReportsTag,
                "List saved reports",
                "Lists saved reports visible to the current caller for one report definition.")
            .Produces<SavedReportSummary[]>();
        ProtectedApi(
                WithStorageErrors(group.MapPost("/{name}/saved", SavedReportEndpoints.Save)),
                SavedReportsTag,
                "Create a saved report",
                "Creates a private, global, or primary saved report after validating the submitted state.")
            .Accepts<SaveReportRequest>("application/json")
            .Produces<SavedReportSummary>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);
        ProtectedApi(
                WithStorageErrors(group.MapGet("/saved/{id}", SavedReportEndpoints.Load)),
                SavedReportsTag,
                "Load a saved report",
                "Returns visible saved-report metadata and its versioned report-state document.")
            .Produces<SavedReportDocument>();
        ProtectedApi(
                WithStorageErrors(group.MapPut("/saved/{id}", SavedReportEndpoints.Update)),
                SavedReportsTag,
                "Update a saved report",
                "Changes selected saved-report properties. Publication and ownership changes require administrator authority.")
            .Accepts<UpdateSavedReportRequest>("application/json")
            .Produces<SavedReportSummary>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);
        ProtectedApi(
                WithStorageErrors(group.MapDelete("/saved/{id}", SavedReportEndpoints.Delete)),
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
            .ProducesProblem(StatusCodes.Status400BadRequest);
        ProtectedApi(
                group.MapDelete("/admin/authorization/administrators", AuthorizationEndpoints.RevokeAdministrator),
                AdministrationTag,
                "Revoke administrator access",
                "Removes a database-authored administrator grant.")
            .Accepts<AuthorizationIdentityRequest>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest);
        ProtectedApi(
                group.MapPut("/admin/authorization/reports/{name}", AuthorizationEndpoints.SetReportRestriction),
                AdministrationTag,
                "Set report restriction",
                "Controls whether the report requires an explicit per-user grant.")
            .Accepts<ReportRestrictionRequest>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest);
        ProtectedApi(
                group.MapPost("/admin/authorization/reports/{name}/users", AuthorizationEndpoints.GrantReportUser),
                AdministrationTag,
                "Grant report access",
                "Adds a database-authored user grant for one report.")
            .Accepts<AuthorizationIdentityRequest>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest);
        ProtectedApi(
                group.MapDelete("/admin/authorization/reports/{name}/users", AuthorizationEndpoints.RevokeReportUser),
                AdministrationTag,
                "Revoke report access",
                "Removes a database-authored user grant for one report.")
            .Accepts<AuthorizationIdentityRequest>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest);
        ProtectedApi(
                WithStorageErrors(group.MapGet(
                    "/admin/saved/{id}/document", SavedReportEndpoints.AdminDownloadDocument)),
                AdministrationTag,
                "Download a report document",
                "Downloads a saved report as a source-controllable report-document envelope.")
            .Produces<ReportDocumentFile>(contentType: "application/json");
        ProtectedApi(
                WithStorageErrors(group.MapPost(
                    "/admin/{name}/documents", SavedReportEndpoints.AdminUploadDocument)),
                AdministrationTag,
                "Upload a report document",
                "Validates and imports a report-document envelope as a saved report.")
            .Accepts<ReportDocumentFile>("application/json")
            .Produces<SavedReportSummary>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        return group;
    }

    /// <summary>
    /// Saved-report handlers predate the optional store and contain deliberate
    /// domain-level exception translations. This outer boundary handles only errors
    /// that escape those translations, including a missing or unreachable store.
    /// </summary>
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

    private static RouteHandlerBuilder Api(
        RouteHandlerBuilder endpoint,
        string tag,
        string summary,
        string description)
        => endpoint
            .WithTags(tag)
            .WithSummary(summary)
            .WithDescription(description);

    private static RouteHandlerBuilder ProtectedApi(
        RouteHandlerBuilder endpoint,
        string tag,
        string summary,
        string description)
        => Api(endpoint, tag, summary, description)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

    private static async Task<IResult> GetSchema(string name, HttpContext ctx, CancellationToken ct)
    {
        var accessService = Access(ctx);
        var actions = SavedReportsListingDefinition.Matches(name)
            ? new[] { InteractiveReportAction.ListAllSavedReports }
            : new[] { InteractiveReportAction.ViewReport };
        var access = await accessService.Authorize(new ReportAccessRequest
        {
            ReportName = name,
            Actions = actions,
        }, ctx, ct);
        if (access.Error is not null) return access.Error;
        var def = access.Definition!;

        try
        {
            var executor = ctx.RequestServices.GetRequiredService<ReportExecutor>();
            var contextParams = await accessService.ResolveContextParameters(def, ctx, ct);
            var columns = await executor.GetSchema(def, contextParams, ct);

            return Results.Json(new InteractiveReportSchema(
                Name: def.Name,
                Title: def.Title ?? ColumnModel.Prettify(def.Name),
                StyleSheet: def.StyleSheet?.Trim(),
                Columns: columns.Select(c => new ColumnInfo(c.Name, c.Label, c.KindName, c.IsComputed)).ToArray(),
                EditLink: ResolveEditLink(def, columns, ctx),
                ColumnOverrides: ResolveColumnOverrides(def, columns),
                DefaultState: SchemaDefaultState(def),
                StateVersion: ReportState.CurrentVersion,
                Capabilities: new InteractiveReportCapabilities(
                    ExpressionLanguageCatalog.Functions,
                    AggregateCatalog.FunctionsByColumnType,
                    AggregateCatalog.ChartFunctionsByColumnType),
                // Always the resolved effective set (canonical casing/order), so the
                // client never needs its own copy of the catalog to interpret it.
                Features: ReportFeatures.Resolve(def),
                Limits: new InteractiveReportLimits(
                    def.DefaultPageSize,
                    def.MaxPageSize,
                    def.MaxRows,
                    def.MaxChartPoints),
                // A presentation hint, not a grant. Every mutation is still
                // evaluated against its concrete action and resource.
                Authorization: new InteractiveReportAuthorizationHint(
                    await accessService.MayRequestAdministration(ctx, ct))),
                IrJson.Options);
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ServerError(ctx, def.Name, "schema discovery", ex);
        }
    }

    /// <summary>
    /// Delivers the definition's edit link with placeholders rewritten to canonical
    /// schema casing (so client row lookups hit row keys directly) and defaults
    /// resolved. An unresolvable template disables the edit column for this schema —
    /// omitted from the payload, with the problem logged; the query path surfaces the
    /// same binding failure to users through ignored[].
    /// </summary>
    private static InteractiveReportEditLink? ResolveEditLink(
        ReportDefinition def,
        Core.Schema.ReportSchema schema,
        HttpContext ctx)
    {
        if (def.EditLink is not { } editLink) return null;

        var placeholders = EditLinkTemplate.Parse(editLink.UrlTemplate, out var error);
        var unknown = placeholders?.FirstOrDefault(name => !schema.TryGetValue(name, out _));
        if (placeholders is null || unknown is not null)
        {
            Log(ctx)?.LogWarning(
                "Report {Report}: editLink.urlTemplate {Problem}; the edit column is disabled.",
                def.Name,
                placeholders is null ? $"is invalid — {error}" : $"references unknown column '{unknown}'");
            return null;
        }

        return new InteractiveReportEditLink(
            UrlTemplate: EditLinkTemplate.Rewrite(
                editLink.UrlTemplate,
                name => schema.TryGetValue(name, out var col) ? col.Name : name),
            Label: string.IsNullOrWhiteSpace(editLink.Label) ? "Edit" : editLink.Label.Trim(),
            Target: string.Equals(editLink.Target, "_blank", StringComparison.OrdinalIgnoreCase)
                ? "_blank"
                : "_self");
    }

    /// <summary>
    /// Per-column behavior flags for the client, filtered to live schema columns and
    /// keyed by canonical name. Labels are deliberately absent — they ride the default
    /// report's labels channel like columnLabels always has — so this map only exists
    /// when a column carries behavior the client must gate on.
    /// </summary>
    private static IReadOnlyDictionary<string, InteractiveReportColumnOptions>? ResolveColumnOverrides(
        ReportDefinition def,
        Core.Schema.ReportSchema schema)
    {
        if (def.Columns is not { Count: > 0 }) return null;

        var result = new Dictionary<string, InteractiveReportColumnOptions>();
        foreach (var (name, over) in def.Columns)
        {
            if (over is null || !schema.TryGetValue(name, out var col)) continue;
            var helpText = string.IsNullOrWhiteSpace(over.HelpText) ? null : over.HelpText.Trim();
            if (over.HideLabel != true && over.Sortable != false && over.Filterable != false && helpText is null)
                continue;
            result[col.Name] = new InteractiveReportColumnOptions(
                HideLabel: over.HideLabel == true ? true : null,
                Sortable: over.Sortable == false ? false : null,
                Filterable: over.Filterable == false ? false : null,
                HelpText: helpText);
        }
        return result.Count > 0 ? result : null;
    }

    /// <summary>
    /// The default report the schema endpoint sends down — always complete, never null.
    /// An unconfigured effective Default synthesizes to an empty state (every schema
    /// column in database order), and the definition's labels (columnLabels overlaid
    /// with columns[*].label) become the default report's labels unless the effective
    /// state carries its own. Query responses never apply labels; the document
    /// ingestion pipeline mirrors this same layering so exports render what an
    /// equivalent client displays.
    /// </summary>
    internal static ReportState SchemaDefaultState(ReportDefinition def)
    {
        // Resolve against an empty request to get a detached copy — the store's
        // definition (and its DefaultState) must not be mutated by response shaping.
        var state = ReportStateResolver.Resolve(def.DefaultState, new ReportState());
        if (state.Pipeline is not { Count: > 0 })
            state.Pipeline = [new PipelineStage { Shape = new StageShape { Kind = "source" } }];
        var source = state.Pipeline[0];
        source.Layer ??= new StageLayer();
        if (source.Layer.Labels is null && def.GetEffectiveColumnLabels() is { } definitionLabels)
            source.Layer.Labels = new(definitionLabels);
        return state;
    }

    private static Task<IResult> PostQuery(string name, HttpContext ctx, CancellationToken ct)
        => ExecuteStateOperation(
            name,
            ctx,
            "query",
            InteractiveReportAction.Query,
            preflight: null,
            static async (_, definition, executor, state, contextParams, token) =>
            {
                var result = await executor.Query(definition, state, contextParams, token);
                return Results.Json(result, IrJson.Options);
            },
            ct);

    /// <summary>
    /// Same state document, same gate, no paging: rows are capped when the definition's
    /// MaxRows is positive, with truncation signaled via X-IR-Truncated.
    /// Download is one of the two server-enforced features because it creates an
    /// external artifact; hiding the menu client-side is not enough.
    /// </summary>
    private static Task<IResult> PostExport(string name, HttpContext ctx, CancellationToken ct)
        => ExecuteStateOperation(
            name,
            ctx,
            "export",
            InteractiveReportAction.Export,
            static (context, definition) =>
            {
                if (Access(context).RequireFeature(definition, ReportFeatures.Download) is { } disabled)
                    return disabled;
                var format = context.Request.Query["format"].FirstOrDefault() ?? "csv";
                return string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : Results.Problem(
                        title: "Unsupported export format",
                        detail: $"format '{format}' is not supported (csv only for now)",
                        statusCode: StatusCodes.Status400BadRequest);
            },
            static async (context, definition, executor, state, contextParams, token) =>
            {
                var export = await executor.Export(definition, state, contextParams, token);
                var csv = Core.Export.CsvWriter.Write(export.Columns, export.Rows);
                context.Response.Headers["X-IR-Truncated"] = export.Truncated ? "true" : "false";
                return Results.File(csv, "text/csv; charset=utf-8", $"{definition.Name}.csv");
            },
            ct);

    /// <summary>
    /// Shared report-state request pipeline. Definition lookup and authorization happen
    /// before body parsing, then both query and export receive identical context
    /// resolution, validation error shaping, cancellation, and sanitization behavior.
    /// </summary>
    private static async Task<IResult> ExecuteStateOperation(
        string name,
        HttpContext ctx,
        string operationName,
        InteractiveReportAction action,
        Func<HttpContext, ReportDefinition, IResult?>? preflight,
        StateOperation operation,
        CancellationToken ct)
    {
        var accessService = Access(ctx);
        IReadOnlyCollection<InteractiveReportAction> actions =
            SavedReportsListingDefinition.Matches(name)
                ? action == InteractiveReportAction.Export
                    ? [InteractiveReportAction.ListAllSavedReports, InteractiveReportAction.Export]
                    : [InteractiveReportAction.ListAllSavedReports]
                : [action];
        var access = await accessService.Authorize(new ReportAccessRequest
        {
            ReportName = name,
            Actions = actions,
        }, ctx, ct);
        if (access.Error is not null) return access.Error;
        var definition = access.Definition!;
        if (preflight?.Invoke(ctx, definition) is { } rejected) return rejected;

        ReportState state;
        try
        {
            state = await JsonSerializer.DeserializeAsync<ReportState>(ctx.Request.Body, IrJson.Options, ct)
                ?? new ReportState();
        }
        catch (JsonException ex)
        {
            // Precise by design: the message only references the caller's input.
            return Results.Problem(
                title: "Malformed report state document",
                detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            var executor = ctx.RequestServices.GetRequiredService<ReportExecutor>();
            var contextParams = await accessService.ResolveContextParameters(definition, ctx, ct);
            return await operation(ctx, definition, executor, state, contextParams, ct);
        }
        catch (ReportValidationException ex)
        {
            return ValidationProblem(ex);
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ServerError(ctx, definition.Name, operationName, ex);
        }
    }

    /// <summary>
    /// Everything that isn't a validation error is sanitized: full details (including
    /// provider messages that may embed SQL fragments) go to the server log under a
    /// correlation id; the client gets a generic problem document carrying that id.
    /// </summary>
    internal static IResult ValidationProblem(ReportValidationException ex)
    {
        var errors = ex.Errors
            .GroupBy(error => error.Path)
            .ToDictionary(group => group.Key, group => group.Select(error => error.Message).ToArray());
        return Results.ValidationProblem(errors, title: "Report state failed validation");
    }

    /// <summary>
    /// Definition resolution behind error shaping. Find validates configuration and
    /// synchronizes configured documents, so a mistake introduced by a live config
    /// reload must surface as the standard sanitized problem document rather than an
    /// unhandled 500. (Startup-time mistakes fail the host before traffic — see
    /// InteractiveReportStartupValidator.)
    /// </summary>
    internal static IResult ServerError(HttpContext ctx, string reportName, string operation, Exception ex)
    {
        Log(ctx)?.LogError(ex, "Report {Report}: {Operation} failed (traceId {TraceId})",
            reportName, operation, ctx.TraceIdentifier);

        return Results.Problem(
            title: "Report execution failed",
            statusCode: StatusCodes.Status500InternalServerError,
            extensions: new Dictionary<string, object?> { ["traceId"] = ctx.TraceIdentifier });
    }

    private static IReportAccessService Access(HttpContext context)
        => context.RequestServices.GetRequiredService<IReportAccessService>();

    internal static ILogger? Log(HttpContext context)
        => context.RequestServices.GetRequiredService<InteractiveReportLogging>().Logger;
}
