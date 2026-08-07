using System.Text.Json;
using InteractiveReport.Core.Definitions;
using InteractiveReport.Core.Identity;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.SavedReports;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace InteractiveReport.AspNetCore;

/// <summary>
/// Identity + saved-report endpoints.
///
/// Authorization matrix:
///   owner (private report)  → read, update title/state, delete
///   anyone (global report)  → read
///   administrator           → everything: list all, publish/unpublish global,
///                             reassign owner, update or delete any report
/// Denials hide existence (404) except where the caller already provably knows the
/// resource exists — an owner touching admin-only powers gets an explicit 403.
/// </summary>
internal static class SavedReportEndpoints
{
    // --- whoami --------------------------------------------------------------

    internal static IResult Whoami(HttpContext ctx)
    {
        var opts = Options(ctx);
        if (!opts.WhoamiEnabled) return Results.NotFound();

        var identity = ReportIdentity.Resolve(ctx.User, opts.IdentityClaim);
        return Results.Json(new
        {
            authenticated = ctx.User.Identity?.IsAuthenticated == true,
            // The exact value to put in InteractiveReport:Administrators.
            identity,
            isAdministrator = ReportIdentity.IsAdministrator(ctx.User, opts.IdentityClaim, opts.Administrators),
            name = ctx.User.Identity?.Name,
            authenticationType = ctx.User.Identity?.AuthenticationType,
            claims = ctx.User.Claims.Select(c => new { type = c.Type, value = c.Value }),
        }, IrJson.Options);
    }

    // --- user surface --------------------------------------------------------

    internal static async Task<IResult> ListForReport(string name, HttpContext ctx, CancellationToken ct)
    {
        var store = ctx.RequestServices.GetRequiredService<IReportDefinitionStore>();
        var def = await store.Find(name, ct);
        if (def is null) return Results.NotFound();
        if (await ReportRequestAccess.Authorize(def, ctx) is { } denied) return denied;

        var identity = Identity(ctx);
        var saved = await SavedStore(ctx).ListVisible(def.Name, identity, ct);
        return Results.Json(saved.Select(r => Summary(r, identity)), IrJson.Options);
    }

    internal static async Task<IResult> Save(string name, HttpContext ctx, CancellationToken ct)
    {
        var store = ctx.RequestServices.GetRequiredService<IReportDefinitionStore>();
        var def = await store.Find(name, ct);
        if (def is null) return Results.NotFound();
        if (await ReportRequestAccess.Authorize(def, ctx) is { } denied) return denied;
        // Enforced at creation only: existing saved reports stay governed by the
        // ownership matrix, so a config change never strands unmanageable rows.
        if (ReportRequestAccess.RequireFeature(def, ReportFeatures.SavedReports) is { } featureDenied)
            return featureDenied;

        var identity = Identity(ctx);
        if (identity is null) return Results.Unauthorized();

        SaveReportRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<SaveReportRequest>(ctx.Request.Body, IrJson.Options, ct);
        }
        catch (JsonException ex)
        {
            return BadRequest("Malformed save request", ex.Message);
        }

        if (TitleError(request?.Title) is { } titleError) return titleError;
        if (request!.State is null) return BadRequest("Malformed save request", "state is required");
        if (request.IsGlobal && !IsAdmin(ctx))
            return AdminRequired("publishing a global report requires an administrator");

        var report = new SavedReport
        {
            Id = SavedReport.NewId(),
            ReportName = def.Name,
            Title = request.Title!.Trim(),
            Owner = identity,
            IsGlobal = request.IsGlobal,
            StateJson = JsonSerializer.Serialize(request.State, IrJson.Options),
        };
        await SavedStore(ctx).Create(report, ct);

        return Results.Json(Summary(report, identity), IrJson.Options, statusCode: StatusCodes.Status201Created);
    }

    internal static async Task<IResult> Load(string id, HttpContext ctx, CancellationToken ct)
    {
        var report = await SavedStore(ctx).Get(id, ct);
        if (report is null) return Results.NotFound();

        var identity = Identity(ctx);
        if (Denied(SavedReportAccessPolicy.Read(report, identity, IsAdmin(ctx)), "") is { } visibilityDenied)
            return visibilityDenied;

        // Loading a state document still requires access to the underlying report.
        var def = await ctx.RequestServices.GetRequiredService<IReportDefinitionStore>().Find(report.ReportName, ct);
        if (def is null) return Results.NotFound();
        if (await ReportRequestAccess.Authorize(def, ctx) is { } denied) return denied;

        using var state = JsonDocument.Parse(report.StateJson);
        return Results.Json(new
        {
            summary = Summary(report, identity),
            state = state.RootElement.Clone(),
        }, IrJson.Options);
    }

    internal static async Task<IResult> Update(string id, HttpContext ctx, CancellationToken ct)
    {
        var savedStore = SavedStore(ctx);
        var report = await savedStore.Get(id, ct);
        if (report is null) return Results.NotFound();

        var identity = Identity(ctx);
        var admin = IsAdmin(ctx);
        if (Denied(
                SavedReportAccessPolicy.Modify(report, identity, admin),
                "modifying a global report requires an administrator") is { } denied)
            return denied;

        UpdateSavedReportRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<UpdateSavedReportRequest>(ctx.Request.Body, IrJson.Options, ct);
        }
        catch (JsonException ex)
        {
            return BadRequest("Malformed update request", ex.Message);
        }
        if (request is null) return BadRequest("Malformed update request", "empty body");

        if (!admin && (request.IsGlobal.HasValue || request.Owner is not null))
            return AdminRequired("changing the global flag or owner requires an administrator");

        if (request.Title is not null)
        {
            if (TitleError(request.Title) is { } titleError) return titleError;
            report.Title = request.Title.Trim();
        }
        if (request.State is not null)
            report.StateJson = JsonSerializer.Serialize(request.State, IrJson.Options);
        if (request.IsGlobal.HasValue)
            report.IsGlobal = request.IsGlobal.Value;
        if (request.Owner is not null)
        {
            if (string.IsNullOrWhiteSpace(request.Owner))
                return BadRequest("Malformed update request", "owner must be a non-empty identity value");
            report.Owner = request.Owner.Trim();
        }

        return await savedStore.Update(report, ct)
            ? Results.Json(Summary(report, identity), IrJson.Options)
            : Results.NotFound();
    }

    internal static async Task<IResult> Delete(string id, HttpContext ctx, CancellationToken ct)
    {
        var savedStore = SavedStore(ctx);
        var report = await savedStore.Get(id, ct);
        if (report is null) return Results.NotFound();

        var identity = Identity(ctx);
        var admin = IsAdmin(ctx);
        if (Denied(
                SavedReportAccessPolicy.Modify(report, identity, admin),
                "deleting a global report requires an administrator") is { } denied)
            return denied;

        return await savedStore.Delete(id, ct) ? Results.NoContent() : Results.NotFound();
    }

    // --- administrator surface -----------------------------------------------

    internal static async Task<IResult> AdminListAll(HttpContext ctx, CancellationToken ct)
    {
        if (Identity(ctx) is null) return Results.Unauthorized();
        if (!IsAdmin(ctx)) return Results.NotFound();

        var identity = Identity(ctx);
        var all = await SavedStore(ctx).ListAll(ct);
        return Results.Json(all.Select(r => Summary(r, identity)), IrJson.Options);
    }

    // --- helpers -------------------------------------------------------------

    private static InteractiveReportOptions Options(HttpContext ctx)
        => ctx.RequestServices.GetRequiredService<IOptionsMonitor<InteractiveReportOptions>>().CurrentValue;

    private static ISavedReportStore SavedStore(HttpContext ctx)
        => ctx.RequestServices.GetRequiredService<ISavedReportStore>();

    private static string? Identity(HttpContext ctx)
    {
        var opts = Options(ctx);
        return ReportIdentity.Resolve(ctx.User, opts.IdentityClaim);
    }

    private static bool IsAdmin(HttpContext ctx)
    {
        var opts = Options(ctx);
        return ReportIdentity.IsAdministrator(ctx.User, opts.IdentityClaim, opts.Administrators);
    }

    private static object Summary(SavedReport r, string? caller) => new
    {
        id = r.Id,
        reportName = r.ReportName,
        title = r.Title,
        isGlobal = r.IsGlobal,
        owner = r.Owner,
        mine = SavedReportAccessPolicy.IsOwner(r, caller),
        modifiedUtc = r.ModifiedUtc,
    };

    private static IResult? TitleError(string? title)
        => string.IsNullOrWhiteSpace(title) || title.Trim().Length > 200
            ? BadRequest("Malformed save request", "title is required (1–200 characters)")
            : null;

    private static IResult BadRequest(string title, string detail)
        => Results.Problem(title: title, detail: detail, statusCode: StatusCodes.Status400BadRequest);

    private static IResult AdminRequired(string detail)
        => Results.Problem(title: "Administrator required", detail: detail, statusCode: StatusCodes.Status403Forbidden);

    private static IResult? Denied(SavedReportAccess access, string administratorDetail) => access switch
    {
        SavedReportAccess.Allowed => null,
        SavedReportAccess.Hidden => Results.NotFound(),
        SavedReportAccess.AdministratorRequired => AdminRequired(administratorDetail),
        _ => throw new ArgumentOutOfRangeException(nameof(access)),
    };
}

internal sealed class SaveReportRequest
{
    public string? Title { get; set; }
    public ReportState? State { get; set; }
    public bool IsGlobal { get; set; }
}

internal sealed class UpdateSavedReportRequest
{
    public string? Title { get; set; }
    public ReportState? State { get; set; }
    public bool? IsGlobal { get; set; }
    public string? Owner { get; set; }
}
