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
        group.MapGet("/{name}/schema", GetSchema);
        group.MapPost("/{name}/query", PostQuery);
        group.MapPost("/{name}/export", PostExport);

        // Packaged UI assets. Anonymous even when the host locks the group — see UiEndpoints.
        group.MapGet("/ui/{file}", UiEndpoints.Serve).AllowAnonymous();

        // Identity + saved reports (literal segments win over {name} in ASP.NET routing).
        group.MapGet("/whoami", SavedReportEndpoints.Whoami);
        group.MapGet("/{name}/saved", SavedReportEndpoints.ListForReport);
        group.MapPost("/{name}/saved", SavedReportEndpoints.Save);
        group.MapGet("/saved/{id}", SavedReportEndpoints.Load);
        group.MapPut("/saved/{id}", SavedReportEndpoints.Update);
        group.MapDelete("/saved/{id}", SavedReportEndpoints.Delete);
        group.MapGet("/admin/saved", SavedReportEndpoints.AdminListAll);

        return group;
    }

    private static async Task<IResult> GetSchema(string name, HttpContext ctx, CancellationToken ct)
    {
        var store = ctx.RequestServices.GetRequiredService<IReportDefinitionStore>();
        var def = await store.Find(name, ct);
        if (def is null) return Results.NotFound();
        if (await ReportRequestAccess.Authorize(def, ctx) is { } denied) return denied;

        try
        {
            var executor = ctx.RequestServices.GetRequiredService<ReportExecutor>();
            var contextParams = await ReportRequestAccess.ResolveContextParameters(def, ctx, ct);
            var columns = await executor.GetSchema(def, contextParams, ct);

            return Results.Json(new
            {
                name = def.Name,
                title = def.Title ?? ColumnModel.Prettify(def.Name),
                columns = columns.Select(c => new ColumnInfo(c.Name, c.Label, c.KindName, c.IsComputed)),
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
    /// The default report the schema endpoint sends down — always complete, never null.
    /// An unconfigured effective primary synthesizes to an empty state (every schema
    /// column in database order), and the definition's columnLabels become the default
    /// report's labels unless the effective state carries its own. Query responses
    /// never apply labels; the document ingestion pipeline mirrors this same layering
    /// so exports render what an equivalent client displays.
    /// </summary>
    internal static ReportState SchemaDefaultState(ReportDefinition def)
    {
        // Resolve against an empty request to get a detached copy — the store's
        // definition (and its DefaultState) must not be mutated by response shaping.
        var state = ReportStateResolver.Resolve(def.DefaultState, new ReportState());
        if (state.Labels is null && def.ColumnLabels is not null)
            state.Labels = new(def.ColumnLabels);
        return state;
    }

    private static Task<IResult> PostQuery(string name, HttpContext ctx, CancellationToken ct)
        => ExecuteStateOperation(
            name,
            ctx,
            "query",
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
    /// Download is one of the two server-enforced features — it widens egress past
    /// the page-size caps, so hiding the menu client-side is not enough.
    /// </summary>
    private static Task<IResult> PostExport(string name, HttpContext ctx, CancellationToken ct)
        => ExecuteStateOperation(
            name,
            ctx,
            "export",
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
        Func<HttpContext, ReportDefinition, IResult?>? preflight,
        StateOperation operation,
        CancellationToken ct)
    {
        var store = ctx.RequestServices.GetRequiredService<IReportDefinitionStore>();
        var definition = await store.Find(name, ct);
        if (definition is null) return Results.NotFound();
        if (await ReportRequestAccess.Authorize(definition, ctx) is { } denied) return denied;
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
            var errors = ex.Errors
                .GroupBy(error => error.Path)
                .ToDictionary(group => group.Key, group => group.Select(error => error.Message).ToArray());
            return Results.ValidationProblem(errors, title: "Report state failed validation");
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
    private static IResult ServerError(HttpContext ctx, string reportName, string operation, Exception ex)
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
