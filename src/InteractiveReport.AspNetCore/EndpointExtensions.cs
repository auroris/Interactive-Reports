using System.Text.Json;
using InteractiveReport.Core.Definitions;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace InteractiveReport.AspNetCore;

public static class EndpointExtensions
{
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
        group.MapGet("", ListReports);
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

    private static async Task<IResult> ListReports(HttpContext ctx, CancellationToken ct)
    {
        var store = ctx.RequestServices.GetRequiredService<IReportDefinitionStore>();
        var visible = new List<ReportSummary>();
        foreach (var def in await store.List(ct))
        {
            if (await Gate(def, ctx) is null)
                visible.Add(new ReportSummary { Name = def.Name, Title = def.Title ?? ColumnModel.Prettify(def.Name) });
        }
        return Results.Json(visible, IrJson.Options);
    }

    private static async Task<IResult> GetSchema(string name, HttpContext ctx, CancellationToken ct)
    {
        var store = ctx.RequestServices.GetRequiredService<IReportDefinitionStore>();
        var def = await store.Find(name, ct);
        if (def is null) return Results.NotFound();
        if (await Gate(def, ctx) is { } denied) return denied;

        try
        {
            var executor = ctx.RequestServices.GetRequiredService<ReportExecutor>();
            var contextParams = await ResolveContextParams(def, ctx, ct);
            var columns = await executor.GetSchema(def, contextParams, ct);

            return Results.Json(new
            {
                name = def.Name,
                title = def.Title ?? ColumnModel.Prettify(def.Name),
                columns = columns.Select(c => new ColumnInfo(c.Name, c.Label, c.KindName, c.IsComputed)),
                defaultState = def.DefaultState,
                limits = new { defaultPageSize = def.DefaultPageSize, maxPageSize = def.MaxPageSize, maxRows = def.MaxRows },
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

    private static async Task<IResult> PostQuery(string name, HttpContext ctx, CancellationToken ct)
    {
        var store = ctx.RequestServices.GetRequiredService<IReportDefinitionStore>();
        var def = await store.Find(name, ct);
        if (def is null) return Results.NotFound();
        if (await Gate(def, ctx) is { } denied) return denied;

        ReportState state;
        try
        {
            state = await JsonSerializer.DeserializeAsync<ReportState>(ctx.Request.Body, IrJson.Options, ct)
                ?? new ReportState();
        }
        catch (JsonException ex)
        {
            // Precise by design: the message only ever references the client's own input.
            return Results.Problem(
                title: "Malformed report state document",
                detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            var executor = ctx.RequestServices.GetRequiredService<ReportExecutor>();
            var contextParams = await ResolveContextParams(def, ctx, ct);
            var result = await executor.Query(def, state, contextParams, ct);
            return Results.Json(result, IrJson.Options);
        }
        catch (ReportValidationException ex)
        {
            var errors = ex.Errors
                .GroupBy(e => e.Path)
                .ToDictionary(g => g.Key, g => g.Select(e => e.Message).ToArray());
            return Results.ValidationProblem(errors, title: "Report state failed validation");
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ServerError(ctx, def.Name, "query", ex);
        }
    }

    /// <summary>
    /// Same state document, same gate, no paging: rows capped at the definition's
    /// MaxRows with truncation signaled via the X-IR-Truncated response header.
    /// </summary>
    private static async Task<IResult> PostExport(string name, HttpContext ctx, CancellationToken ct)
    {
        var store = ctx.RequestServices.GetRequiredService<IReportDefinitionStore>();
        var def = await store.Find(name, ct);
        if (def is null) return Results.NotFound();
        if (await Gate(def, ctx) is { } denied) return denied;

        var format = ctx.Request.Query["format"].FirstOrDefault() ?? "csv";
        if (!string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
            return Results.Problem(
                title: "Unsupported export format",
                detail: $"format '{format}' is not supported (csv only for now)",
                statusCode: StatusCodes.Status400BadRequest);

        ReportState state;
        try
        {
            state = await JsonSerializer.DeserializeAsync<ReportState>(ctx.Request.Body, IrJson.Options, ct)
                ?? new ReportState();
        }
        catch (JsonException ex)
        {
            return Results.Problem(
                title: "Malformed report state document",
                detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            var executor = ctx.RequestServices.GetRequiredService<ReportExecutor>();
            var contextParams = await ResolveContextParams(def, ctx, ct);
            var export = await executor.Export(def, state, contextParams, ct);

            var csv = Core.Export.CsvWriter.Write(export.Columns, export.Rows);
            ctx.Response.Headers["X-IR-Truncated"] = export.Truncated ? "true" : "false";
            return Results.File(csv, "text/csv; charset=utf-8", $"{def.Name}.csv");
        }
        catch (ReportValidationException ex)
        {
            var errors = ex.Errors
                .GroupBy(e => e.Path)
                .ToDictionary(g => g.Key, g => g.Select(e => e.Message).ToArray());
            return Results.ValidationProblem(errors, title: "Report state failed validation");
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ServerError(ctx, def.Name, "export", ex);
        }
    }

    /// <summary>
    /// Layered, default-deny authorization. Null result = pass. Unauthenticated callers
    /// get 401; authenticated callers failing a report's policy get 404 — a failed policy
    /// must not confirm the report exists.
    /// </summary>
    internal static async Task<IResult?> Gate(ReportDefinition def, HttpContext ctx)
    {
        var auth = def.Authorization;
        if (auth?.AllowAnonymous == true) return null;

        if (ctx.User.Identity?.IsAuthenticated != true)
            return Results.Unauthorized();

        if (auth?.Policy is { Length: > 0 } policy)
        {
            var authz = ctx.RequestServices.GetService<IAuthorizationService>()
                ?? throw new InvalidOperationException(
                    $"Report '{def.Name}' declares policy '{policy}' but the host has not registered authorization services (AddAuthorization).");
            var decision = await authz.AuthorizeAsync(ctx.User, policy);
            if (!decision.Succeeded) return Results.NotFound();
        }

        return null;
    }

    private static async Task<IReadOnlyDictionary<string, object?>> ResolveContextParams(
        ReportDefinition def, HttpContext ctx, CancellationToken ct)
    {
        if (def.ContextParams is null || def.ContextParams.Count == 0)
            return new Dictionary<string, object?>();

        var resolver = ctx.RequestServices.GetRequiredService<IContextParameterResolver>();
        var result = new Dictionary<string, object?>();
        foreach (var (paramName, spec) in def.ContextParams)
            result[paramName] = await resolver.Resolve(paramName, spec, ctx.User, ct);
        return result;
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
