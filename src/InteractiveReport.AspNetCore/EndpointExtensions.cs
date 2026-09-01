// HTTP module entrypoint: registers the Interactive Reports REST surface and connects
// transport concerns to the engine. Endpoint handlers resolve identity, authorization,
// configured definitions, context parameters, and coded errors before invoking shared
// execution services.

using System.Text.Json;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Export;
using InteractiveReport.Core.Expressions;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Validation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace InteractiveReport.AspNetCore;

/// <summary>Provides the host entrypoint that maps the Interactive Reports HTTP surface.</summary>
public static class EndpointExtensions
{
    private const string ReportsTag = "Interactive Reports";
    private const string SavedReportsTag = "Interactive Reports - Saved Reports";
    private const string AdministrationTag = "Interactive Reports - Administration";

    /// <summary>Executes an already authorized and parsed report-state operation.</summary>
    /// <param name="context">The active HTTP exchange.</param>
    /// <param name="definition">The authorized report definition.</param>
    /// <param name="executor">The scoped report executor.</param>
    /// <param name="state">The deserialized request state.</param>
    /// <param name="contextParameters">Application values resolved for the definition's context parameters.</param>
    /// <param name="ct">Cancels execution.</param>
    /// <returns>The HTTP result produced by the operation.</returns>
    private delegate Task<IResult> StateOperation(
        HttpContext context,
        ReportDefinition definition,
        ReportExecutor executor,
        ReportState state,
        IReadOnlyDictionary<string, object?> contextParameters,
        CancellationToken ct);

    /// <summary>
    /// Mounts the report endpoints and returns their group so hosts can chain standard conventions —
    /// .RequireAuthorization(...), antiforgery/CSRF filters for cookie-auth hosts, rate limiting, etc. The
    /// engine deliberately has no authentication mechanism of its own. Every data and
    /// security-administration endpoint enters IReportAccessService. The opt-in whoami bootstrap diagnostic
    /// and packaged HTML/CSS/JS delivery are the deliberate exceptions.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder on which to register the report routes.</param>
    /// <param name="prefix">The URL prefix under which to map the report routes; defaults to <c>"/api/reports"</c>.</param>
    /// <returns>The mapped route group, which the host can configure further.</returns>
    /// <remarks>Adds routes and endpoint filters to <paramref name="endpoints"/>.</remarks>
    /// <example>
    /// <code><![CDATA[
    /// app.MapInteractiveReports("/api/reports")
    ///     .RequireAuthorization("ReportingUsers")
    ///     .RequireRateLimiting("reports");
    /// ]]></code>
    /// </example>
    public static RouteGroupBuilder MapInteractiveReports(
        this IEndpointRouteBuilder endpoints,
        string prefix = "/api/reports")
        => MapInteractiveReportsCore(endpoints, prefix, logger: null);

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
    public static RouteGroupBuilder MapInteractiveReports(
        this IEndpointRouteBuilder endpoints,
        string prefix,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        return MapInteractiveReportsCore(endpoints, prefix, logger);
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
                group.MapPost("/{name}/export", PostExport),
                ReportsTag,
                "Export a report",
                "Executes the supplied report state without paging and returns CSV. A positive maxRows definition setting caps the output.")
            .Accepts<ReportState>("application/json")
            .Produces(StatusCodes.Status200OK, contentType: "text/csv")
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
            .Produces<InteractiveReportError>(StatusCodes.Status400BadRequest)
            .Produces<InteractiveReportError>(StatusCodes.Status409Conflict);
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
            .Produces<InteractiveReportError>(StatusCodes.Status400BadRequest)
            .Produces<InteractiveReportError>(StatusCodes.Status409Conflict);
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
    /// <param name="name">The report name to resolve.</param>
    /// <param name="ctx">The current HTTP request and response context.</param>
    /// <param name="ct">Cancels authorization, context resolution, or schema discovery.</param>
    /// <returns>The schema JSON, an access result, or a sanitized server-error result.</returns>
    /// <remarks>Performs authorization, may query the database for schema metadata, and writes the selected HTTP response.</remarks>
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
                Columns: columns.Select(c => new ColumnInfo(c.Name, c.Label, c.KindName, c.IsComputed)).ToArray(),
                EditLink: ResolveEditLink(def, columns, ctx),
                ColumnOverrides: ResolveColumnOverrides(def, columns),
                DefaultState: SchemaDefaultState(def),
                Capabilities: new InteractiveReportCapabilities(
                    ExpressionLanguageCatalog.Functions,
                    AggregateCatalog.FunctionsByColumnType,
                    AggregateCatalog.ChartFunctionsByColumnType),
                // Return the resolved effective set in canonical casing and order so the
                // casing/order), so the client never needs its own copy of the catalog to
                // interpret it.
                Features: ReportFeatures.Resolve(def),
                Limits: new InteractiveReportLimits(
                    def.DefaultPageSize,
                    def.MaxPageSize,
                    def.MaxRows,
                    def.MaxChartPoints),
                // A presentation hint, not a grant. Every mutation is still evaluated against
                // its concrete action and resource.
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
    /// Resolves the definition's edit link with placeholders rewritten to canonical
    /// schema casing (so client row lookups hit row keys directly) and defaults resolved. An unresolvable
    /// template disables the edit column for this schema — omitted from the payload, with the problem
    /// logged; the query path surfaces the same binding failure to users through ignored[].
    /// </summary>
    /// <param name="def">The definition containing the optional edit-link template.</param>
    /// <param name="schema">The live schema used to bind placeholder names.</param>
    /// <param name="ctx">The request context used to obtain the package logger.</param>
    /// <returns>The client-ready edit-link contract, or <see langword="null"/> when absent or unbindable.</returns>
    /// <remarks>Logs a warning when an invalid or schema-stale template disables the edit column.</remarks>
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
    /// Resolves per-column behavior flags for the client, filtered to live schema columns
    /// and keyed by canonical name. Labels are deliberately absent — they ride the default report's labels
    /// channel like columnLabels always has — so this map only exists when a column carries behavior the
    /// client must gate on.
    /// </summary>
    /// <param name="def">The definition containing optional column overrides.</param>
    /// <param name="schema">The live schema used to canonicalize and filter column names.</param>
    /// <returns>Overrides keyed by canonical column name, or <see langword="null"/> when none affect client behavior.</returns>
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
    /// Builds the complete default report state sent by the schema endpoint. The result is never
    /// null. An unconfigured effective Default synthesizes to an empty state (every schema column in
    /// database order), and the definition's labels (columnLabels overlaid with columns[*].label) become the
    /// default report's labels unless the effective state carries its own. Query responses never apply
    /// labels; the document ingestion pipeline mirrors this same layering so exports render what an
    /// equivalent client displays.
    /// </summary>
    /// <param name="def">The definition supplying the effective default state and definition-level labels.</param>
    /// <returns>A detached, complete state with a definition-input table and layered default labels.</returns>
    internal static ReportState SchemaDefaultState(ReportDefinition def)
    {
        // Resolve against an empty request to get a detached copy; the
        // store's definition (and its DefaultState) must not be mutated by response shaping.
        var state = ReportStateResolver.Resolve(def.DefaultState, new ReportState());
        if (state.Tables is not { Count: > 0 })
        {
            state.ActiveTable = "base";
            state.Tables = new(StringComparer.OrdinalIgnoreCase)
            {
                ["base"] = new ReportTable { From = "definition" },
            };
        }
        var source = DefinitionInputTable(state);
        if (source is not null && def.GetEffectiveColumnLabels() is { } definitionLabels)
        {
            source.Composables ??= [];
            var shapeIndex = source.Composables.FindIndex(IsShapeComposable);
            var inputCount = shapeIndex < 0 ? source.Composables.Count : shapeIndex;
            var labels = source.Composables
                .Take(inputCount)
                .FirstOrDefault(composable => IsComposableKind(composable, "labels"));
            if (labels is null)
            {
                labels = new TableComposable { Kind = "labels" };
                source.Composables.Insert(inputCount, labels);
            }
            labels.Labels ??= new(definitionLabels);
        }
        return state;
    }

    /// <summary>
    /// Determines whether a composable changes the table shape and therefore requires schema recompilation.
    /// </summary>
    /// <param name="composable">The composable to classify.</param>
    /// <returns><see langword="true"/> when the composable changes result shape; otherwise, <see langword="false"/>.</returns>
    private static bool IsShapeComposable(TableComposable composable)
        => IsComposableKind(composable, "group")
            || IsComposableKind(composable, "pivot")
            || IsComposableKind(composable, "chart");

    /// <summary>
    /// Determines whether a composable has the requested kind, ignoring casing and surrounding whitespace.
    /// </summary>
    /// <param name="composable">The composable whose kind should be tested.</param>
    /// <param name="kind">The canonical operation name to compare.</param>
    /// <returns><see langword="true"/> when the operation names match; otherwise, <see langword="false"/>.</returns>
    private static bool IsComposableKind(TableComposable composable, string kind)
        => string.Equals(composable.Kind?.Trim(), kind, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Follows the selected table's explicit ancestry to its definition input. This
    /// deliberately ignores dictionary enumeration order: a document may contain several independent roots
    /// and table identifiers carry no semantic role.
    /// </summary>
    /// <param name="state">The complete state whose active-table ancestry should be followed.</param>
    /// <returns>The table that reads from <c>definition</c>, or <see langword="null"/> when the ancestry is missing, broken, or cyclic.</returns>
    private static ReportTable? DefinitionInputTable(ReportState state)
    {
        if (state.Tables is not { Count: > 0 } tables
            || string.IsNullOrWhiteSpace(state.ActiveTable))
            return null;

        var lookup = new Dictionary<string, ReportTable>(tables, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = state.ActiveTable;
        while (!string.Equals(current, "definition", StringComparison.OrdinalIgnoreCase))
        {
            if (!seen.Add(current) || !lookup.TryGetValue(current, out var table))
                return null;
            if (string.Equals(table.From, "definition", StringComparison.OrdinalIgnoreCase))
                return table;
            if (string.IsNullOrWhiteSpace(table.From))
                return null;
            current = table.From;
        }
        return null;
    }

    /// <summary>
    /// Executes a posted report query through the shared request pipeline.
    /// </summary>
    /// <param name="name">The configured or built-in report name from the route.</param>
    /// <param name="ctx">The current HTTP request and response context.</param>
    /// <param name="ct">Cancels authorization, body reading, context resolution, and query execution.</param>
    /// <returns>The report-result JSON or a standardized access, validation, or server-error result.</returns>
    /// <remarks>Consumes the JSON request body and may execute database commands.</remarks>
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
    /// Exports the posted state through the same authorization and validation pipeline as a query, without paging.
    /// Rows are capped when the definition's MaxRows
    /// is positive, with truncation signaled via X-IR-Truncated. Download is one of the two server-enforced
    /// features because it creates an external artifact; hiding the menu client-side is not enough.
    /// </summary>
    /// <param name="name">The configured or built-in report name from the route.</param>
    /// <param name="ctx">The current HTTP request and response context.</param>
    /// <param name="ct">Cancels authorization, body reading, context resolution, query execution, and rendering.</param>
    /// <returns>The requested file result or a standardized feature, format, access, validation, or server-error result.</returns>
    /// <remarks>Consumes the JSON request body, may execute database commands, and sets <c>X-IR-Truncated</c> on successful exports.</remarks>
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
                var exporter = context.RequestServices.GetRequiredService<IReportFileExporter>();
                return exporter.SupportedFormats.Contains(format.Trim(), StringComparer.OrdinalIgnoreCase)
                    ? null
                    : Error(
                        InteractiveReportErrorCodes.UnsupportedExportFormat,
                        StatusCodes.Status400BadRequest,
                        $"format '{format}' is not supported; supported formats: "
                        + string.Join(", ", exporter.SupportedFormats));
            },
            static async (context, definition, _, state, contextParams, token) =>
            {
                var format = context.Request.Query["format"].FirstOrDefault() ?? "csv";
                var export = await context.RequestServices
                    .GetRequiredService<IReportFileExporter>()
                    .Export(definition, state, contextParams, format, token);
                context.Response.Headers["X-IR-Truncated"] = export.Truncated ? "true" : "false";
                return Results.File(export.Bytes, export.ContentType, export.FileName);
            },
            ct);

    /// <summary>
    /// Runs the shared report-state request pipeline. Definition lookup and authorization
    /// happen before body parsing, then both query and export receive identical context resolution,
    /// validation error shaping, cancellation, and sanitization behavior.
    /// </summary>
    /// <param name="name">The configured or built-in report name from the route.</param>
    /// <param name="ctx">The current HTTP request and response context.</param>
    /// <param name="operationName">A diagnostic operation name used when logging unexpected failures.</param>
    /// <param name="action">The report action required from the caller.</param>
    /// <param name="preflight">An optional authorized-definition check performed before reading the body.</param>
    /// <param name="operation">The query or export callback invoked after parsing and context resolution.</param>
    /// <param name="ct">Cancels authorization, body reading, context resolution, and execution.</param>
    /// <returns>The operation result or the first access, preflight, parse, validation, or server-error result.</returns>
    /// <remarks>Consumes the request body after authorization and may execute whatever side effects <paramref name="operation"/> defines.</remarks>
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
            // Returning the parser message is safe here because it references only caller-supplied JSON.
            return Error(
                InteractiveReportErrorCodes.MalformedReportState,
                StatusCodes.Status400BadRequest,
                ex.Message);
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
    /// Converts structured report-state validation errors into the public HTTP 400 error shape.
    /// </summary>
    /// <param name="ex">The validation exception whose path-aware errors should be flattened.</param>
    /// <returns>A coded JSON result containing one line per validation error.</returns>
    internal static IResult ValidationProblem(ReportValidationException ex)
    {
        var details = string.Join(
            Environment.NewLine,
            ex.Errors.Select(error => string.IsNullOrWhiteSpace(error.Path)
                ? error.Message
                : $"{error.Path}: {error.Message}"));
        return Error(
            InteractiveReportErrorCodes.ReportStateInvalid,
            StatusCodes.Status400BadRequest,
            details);
    }

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
    {
        var (title, description) = InteractiveReportErrorCatalog.Find(code);
        return Results.Json(
            new InteractiveReportError(code, description, title, details, traceId),
            IrJson.Options,
            statusCode: statusCode);
    }

    /// <summary>
    /// Creates the standardized authentication-required response.
    /// </summary>
    /// <returns>The HTTP result to send to the client.</returns>
    internal static IResult AuthenticationRequired()
        => Error(
            InteractiveReportErrorCodes.AuthenticationRequired,
            StatusCodes.Status401Unauthorized);

    /// <summary>
    /// Creates the standardized report-not-found response.
    /// </summary>
    /// <returns>The HTTP result to send to the client.</returns>
    internal static IResult ReportNotFound()
        => Error(
            InteractiveReportErrorCodes.ReportNotFound,
            StatusCodes.Status404NotFound);

    /// <summary>
    /// Creates the standardized saved-report-not-found response.
    /// </summary>
    /// <returns>The HTTP result to send to the client.</returns>
    internal static IResult SavedReportNotFound()
        => Error(
            InteractiveReportErrorCodes.SavedReportNotFound,
            StatusCodes.Status404NotFound);

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
    /// Resolves the configured report-access service from request services.
    /// </summary>
    /// <param name="context">The current HTTP request and response context.</param>
    /// <returns>The configured report access service.</returns>
    private static IReportAccessService Access(HttpContext context)
        => context.RequestServices.GetRequiredService<IReportAccessService>();

    /// <summary>
    /// Resolves the optional package logger associated with the current application.
    /// </summary>
    /// <param name="context">The current HTTP request and response context.</param>
    /// <returns>The configured host-owned logger, or <see langword="null"/> when package logging is disabled.</returns>
    internal static ILogger? Log(HttpContext context)
        => context.RequestServices.GetRequiredService<InteractiveReportLogging>().Logger;
}
