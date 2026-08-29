using System.Text.Json;
using InteractiveReport.Core.Definitions;
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
    /// authentication mechanism of its own.
    /// </summary>
    public static RouteGroupBuilder MapInteractiveReports(
        this IEndpointRouteBuilder endpoints,
        string prefix = "/api/reports")
    {
        var group = endpoints.MapGroup(prefix);
        group.AddEndpointFilter(static async (invocation, next) =>
        {
            // Data and identity responses are request-specific. Handlers may replace
            // this policy when their output is deliberately cacheable (the packaged
            // UI assets do so with no-cache + ETag).
            invocation.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(invocation);
        });
        group.MapGet("/{name}/schema", GetSchema);
        group.MapPost("/{name}/query", PostQuery);
        group.MapPost("/{name}/export", PostExport);

        // Packaged UI assets. Anonymous even when the host locks the group — see UiEndpoints.
        group.MapGet("/ui/{file}", UiEndpoints.Serve).AllowAnonymous();

        // Packaged pages: anonymous shells like the assets — identical for any name
        // (no existence disclosure; the element's schema call is the gate). Disabled
        // via InteractiveReport:ViewerPagesEnabled. Literal-first routing means the
        // existing /ui and /saved segments shadow reports with those names at /view,
        // as they already do on the data routes.
        group.MapGet("/{name}/view", ViewerPageEndpoints.Report).AllowAnonymous();
        group.MapGet("/admin", ViewerPageEndpoints.Admin).AllowAnonymous();

        // Identity + saved reports (literal segments win over {name} in ASP.NET routing).
        group.MapGet("/whoami", SavedReportEndpoints.Whoami);
        WithStorageErrors(group.MapGet("/{name}/saved", SavedReportEndpoints.ListForReport));
        WithStorageErrors(group.MapPost("/{name}/saved", SavedReportEndpoints.Save));
        WithStorageErrors(group.MapGet("/saved/{id}", SavedReportEndpoints.Load));
        WithStorageErrors(group.MapPut("/saved/{id}", SavedReportEndpoints.Update));
        WithStorageErrors(group.MapDelete("/saved/{id}", SavedReportEndpoints.Delete));
        group.MapGet("/admin/users", SavedReportEndpoints.AdminListUsers);
        group.MapGet("/admin/authorization", AuthorizationEndpoints.List);
        group.MapPost("/admin/authorization/administrators", AuthorizationEndpoints.GrantAdministrator);
        group.MapDelete("/admin/authorization/administrators", AuthorizationEndpoints.RevokeAdministrator);
        group.MapPut("/admin/authorization/reports/{name}", AuthorizationEndpoints.SetReportRestriction);
        group.MapPost("/admin/authorization/reports/{name}/users", AuthorizationEndpoints.GrantReportUser);
        group.MapDelete("/admin/authorization/reports/{name}/users", AuthorizationEndpoints.RevokeReportUser);
        WithStorageErrors(group.MapGet(
            "/admin/saved/{id}/document", SavedReportEndpoints.AdminDownloadDocument));
        WithStorageErrors(group.MapPost(
            "/admin/{name}/documents", SavedReportEndpoints.AdminUploadDocument));

        return group;
    }

    /// <summary>
    /// Saved-report handlers predate the optional store and contain deliberate
    /// domain-level exception translations. This outer boundary handles only errors
    /// that escape those translations, including a missing or unreachable store.
    /// </summary>
    private static void WithStorageErrors(RouteHandlerBuilder endpoint)
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
    }

    private static async Task<IResult> GetSchema(string name, HttpContext ctx, CancellationToken ct)
    {
        var store = ctx.RequestServices.GetRequiredService<IReportDefinitionStore>();
        var (def, findError) = await ReportRequestAccess.ResolveDefinition(store, name, ctx, ct);
        if (findError is not null) return findError;
        if (def is null) return Results.NotFound();
        var actions = SavedReportsListingDefinition.Matches(def.Name)
            ? new[] { InteractiveReportAction.ListAllSavedReports }
            : new[] { InteractiveReportAction.ViewReport };
        if (await ReportRequestAccess.AuthorizeOperations(
                def,
                ctx,
                actions,
                new InteractiveReportAuthorizationResource { ReportName = def.Name },
                administratorRequired: false,
                hideDenied: def.Authorization?.AdministratorsOnly == true,
                denialDetail: null,
                ct) is { } denied)
            return denied;

        try
        {
            var executor = ctx.RequestServices.GetRequiredService<ReportExecutor>();
            var contextParams = await ReportRequestAccess.ResolveContextParameters(def, ctx, ct);
            var columns = await executor.GetSchema(def, contextParams, ct);

            return Results.Json(new
            {
                name = def.Name,
                title = def.Title ?? ColumnModel.Prettify(def.Name),
                styleSheet = def.StyleSheet?.Trim(),
                columns = columns.Select(c => new ColumnInfo(c.Name, c.Label, c.KindName, c.IsComputed)),
                editLink = ResolveEditLink(def, columns, ctx),
                columnOverrides = ResolveColumnOverrides(def, columns),
                defaultState = SchemaDefaultState(def),
                stateVersion = ReportState.CurrentVersion,
                capabilities = new
                {
                    expressionFunctions = ExpressionLanguageCatalog.Functions,
                    aggregateFunctions = AggregateCatalog.FunctionsByColumnType,
                    chartAggregateFunctions = AggregateCatalog.ChartFunctionsByColumnType,
                },
                // Always the resolved effective set (canonical casing/order), so the
                // client never needs its own copy of the catalog to interpret it.
                features = ReportFeatures.Resolve(def),
                limits = new
                {
                    defaultPageSize = def.DefaultPageSize,
                    maxPageSize = def.MaxPageSize,
                    maxRows = def.MaxRows,
                    maxChartPoints = def.MaxChartPoints,
                },
                authorization = new
                {
                    // A presentation hint, not a grant. Every mutation is still
                    // evaluated against its concrete action and resource.
                    mayRequestAdministration = await ReportRequestAccess.MayRequestAdministration(ctx, ct),
                },
            }, IrJson.Options);
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
    private static object? ResolveEditLink(ReportDefinition def, Core.Schema.ReportSchema schema, HttpContext ctx)
    {
        if (def.EditLink is not { } editLink) return null;

        var placeholders = EditLinkTemplate.Parse(editLink.UrlTemplate, out var error);
        var unknown = placeholders?.FirstOrDefault(name => !schema.TryGetValue(name, out _));
        if (placeholders is null || unknown is not null)
        {
            ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("InteractiveReport").LogWarning(
                "Report {Report}: editLink.urlTemplate {Problem}; the edit column is disabled.",
                def.Name,
                placeholders is null ? $"is invalid — {error}" : $"references unknown column '{unknown}'");
            return null;
        }

        return new
        {
            urlTemplate = EditLinkTemplate.Rewrite(
                editLink.UrlTemplate,
                name => schema.TryGetValue(name, out var col) ? col.Name : name),
            label = string.IsNullOrWhiteSpace(editLink.Label) ? "Edit" : editLink.Label.Trim(),
            target = string.Equals(editLink.Target, "_blank", StringComparison.OrdinalIgnoreCase)
                ? "_blank"
                : "_self",
        };
    }

    /// <summary>
    /// Per-column behavior flags for the client, filtered to live schema columns and
    /// keyed by canonical name. Labels are deliberately absent — they ride the default
    /// report's labels channel like columnLabels always has — so this map only exists
    /// when a column carries behavior the client must gate on.
    /// </summary>
    private static object? ResolveColumnOverrides(ReportDefinition def, Core.Schema.ReportSchema schema)
    {
        if (def.Columns is not { Count: > 0 }) return null;

        var result = new Dictionary<string, object>();
        foreach (var (name, over) in def.Columns)
        {
            if (over is null || !schema.TryGetValue(name, out var col)) continue;
            var helpText = string.IsNullOrWhiteSpace(over.HelpText) ? null : over.HelpText.Trim();
            if (over.HideLabel != true && over.Sortable != false && over.Filterable != false && helpText is null)
                continue;
            result[col.Name] = new
            {
                hideLabel = over.HideLabel == true ? (bool?)true : null,
                sortable = over.Sortable == false ? (bool?)false : null,
                filterable = over.Filterable == false ? (bool?)false : null,
                helpText,
            };
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
    /// Same state document, same gate, no paging: rows capped at the definition's
    /// MaxRows with truncation signaled via the X-IR-Truncated response header.
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
                if (ReportRequestAccess.RequireFeature(definition, ReportFeatures.Download) is { } disabled)
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
        var store = ctx.RequestServices.GetRequiredService<IReportDefinitionStore>();
        var (definition, findError) = await ReportRequestAccess.ResolveDefinition(store, name, ctx, ct);
        if (findError is not null) return findError;
        if (definition is null) return Results.NotFound();
        IReadOnlyCollection<InteractiveReportAction> actions =
            SavedReportsListingDefinition.Matches(definition.Name)
                ? action == InteractiveReportAction.Export
                    ? [InteractiveReportAction.ListAllSavedReports, InteractiveReportAction.Export]
                    : [InteractiveReportAction.ListAllSavedReports]
                : [action];
        if (await ReportRequestAccess.AuthorizeOperations(
                definition,
                ctx,
                actions,
                new InteractiveReportAuthorizationResource { ReportName = definition.Name },
                administratorRequired: false,
                hideDenied: definition.Authorization?.AdministratorsOnly == true,
                denialDetail: null,
                ct) is { } denied)
            return denied;
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
            var contextParams = await ReportRequestAccess.ResolveContextParameters(definition, ctx, ct);
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
        var logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("InteractiveReport");
        logger.LogError(ex, "Report {Report}: {Operation} failed (traceId {TraceId})",
            reportName, operation, ctx.TraceIdentifier);

        return Results.Problem(
            title: "Report execution failed",
            statusCode: StatusCodes.Status500InternalServerError,
            extensions: new Dictionary<string, object?> { ["traceId"] = ctx.TraceIdentifier });
    }
}
