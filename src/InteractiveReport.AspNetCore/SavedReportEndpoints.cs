using System.Text.Json;
using InteractiveReport.Core.Definitions;
using InteractiveReport.Core.Execution;
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
        var configured = Documents(ctx).List(def).Where(document => !document.Primary).ToArray();
        var configuredTitles = configured.Select(document => document.Title)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // A checked-in document is authoritative for its title. Existing database
        // rows remain available to administrators for rename/delete, but do not make
        // the end-user selector ambiguous.
        return Results.Json(
            configured.Select(Summary).Concat(
                saved.Where(report => !configuredTitles.Contains(report.Title))
                    .Select(report => Summary(report, identity))),
            IrJson.Options);
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
        if (ConfiguredTitleExists(Documents(ctx).List(def), request.Title!))
            return ConfiguredTitleConflict(request.Title!);

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
        if (Documents(ctx).Find(id) is { } configured)
        {
            var definition = await ctx.RequestServices.GetRequiredService<IReportDefinitionStore>()
                .Find(configured.ReportName, ct);
            if (definition is null) return Results.NotFound();
            if (await ReportRequestAccess.Authorize(definition, ctx) is { } configuredDenied)
                return configuredDenied;
            return Results.Json(new
            {
                summary = Summary(configured),
                state = configured.State,
            }, IrJson.Options);
        }

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
        if (Documents(ctx).Find(id) is { } configured)
            return await ReadOnlyConfiguredResult(configured, ctx, ct);

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
            if (ConfiguredTitleExists(Documents(ctx).List(report.ReportName), request.Title))
                return ConfiguredTitleConflict(request.Title);
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
        if (Documents(ctx).Find(id) is { } configured)
            return await ReadOnlyConfiguredResult(configured, ctx, ct);

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
        var configured = Documents(ctx).ListAll().Where(document => !document.Primary).Select(Summary);
        return Results.Json(configured.Concat(all.Select(report => Summary(report, identity))), IrJson.Options);
    }

    /// <summary>
    /// Downloads the canonical source-controlled envelope, not the endpoint's
    /// summary/state response wrapper. The resulting file can be placed directly in a
    /// report definition's documentFiles collection after the operator chooses whether
    /// it should be primary.
    /// </summary>
    internal static async Task<IResult> AdminDownloadDocument(
        string id,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (Identity(ctx) is null) return Results.Unauthorized();
        if (!IsAdmin(ctx)) return Results.NotFound();

        string reportName;
        ReportDocumentFile document;
        if (Documents(ctx).Find(id) is { } configured)
        {
            reportName = configured.ReportName;
            document = new ReportDocumentFile
            {
                Title = configured.Title,
                Primary = configured.Primary,
                State = configured.State,
            };
        }
        else
        {
            var report = await SavedStore(ctx).Get(id, ct);
            if (report is null) return Results.NotFound();
            reportName = report.ReportName;

            ReportState? state;
            try
            {
                state = JsonSerializer.Deserialize<ReportState>(report.StateJson, IrJson.Options);
            }
            catch (JsonException ex)
            {
                return EndpointExtensions.ServerError(ctx, reportName, "report document download", ex);
            }

            if (state is null)
                return EndpointExtensions.ServerError(
                    ctx,
                    reportName,
                    "report document download",
                    new InvalidOperationException($"Saved report '{id}' has no state document."));

            document = new ReportDocumentFile
            {
                Title = report.Title,
                Primary = false,
                State = state,
            };
        }

        var definition = await ctx.RequestServices.GetRequiredService<IReportDefinitionStore>()
            .Find(reportName, ct);
        if (definition is null) return Results.NotFound();
        if (await ReportRequestAccess.Authorize(definition, ctx) is { } denied) return denied;

        var jsonOptions = new JsonSerializerOptions(IrJson.Options) { WriteIndented = true };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, jsonOptions);
        return Results.File(
            bytes,
            "application/json; charset=utf-8",
            DownloadFileName(reportName, document.Title!));
    }

    /// <summary>
    /// Imports a canonical report-document file as a private saved report owned by the
    /// administrator. This deliberately bypasses the end-user savedReports feature
    /// flag, but not report authorization or document validation. The imported copy is
    /// a convenient live test surface; primary remains file-publication metadata.
    /// </summary>
    internal static async Task<IResult> AdminUploadDocument(
        string name,
        HttpContext ctx,
        CancellationToken ct)
    {
        var identity = Identity(ctx);
        if (identity is null) return Results.Unauthorized();
        if (!IsAdmin(ctx)) return Results.NotFound();

        var definition = await ctx.RequestServices.GetRequiredService<IReportDefinitionStore>()
            .Find(name, ct);
        if (definition is null) return Results.NotFound();
        if (await ReportRequestAccess.Authorize(definition, ctx) is { } denied) return denied;

        ReportDocumentFile? document;
        try
        {
            document = await JsonSerializer.DeserializeAsync<ReportDocumentFile>(
                ctx.Request.Body,
                IrJson.Options,
                ct);
        }
        catch (JsonException ex)
        {
            return BadRequest("Malformed report document", ex.Message);
        }

        if (TitleError(document?.Title, "Malformed report document") is { } titleError) return titleError;
        if (document!.State is null)
            return BadRequest("Malformed report document", "state is required");
        if (ConfiguredTitleExists(Documents(ctx).List(definition), document.Title!))
            return ConfiguredTitleConflict(document.Title!);

        try
        {
            var contextParams = await ReportRequestAccess.ResolveContextParameters(definition, ctx, ct);
            await ctx.RequestServices.GetRequiredService<ReportExecutor>()
                .ValidateDocument(definition, document.State, contextParams, ct);
        }
        catch (ReportValidationException ex)
        {
            return EndpointExtensions.ValidationProblem(ex);
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return EndpointExtensions.ServerError(ctx, definition.Name, "report document upload", ex);
        }

        var report = new SavedReport
        {
            Id = SavedReport.NewId(),
            ReportName = definition.Name,
            Title = document.Title!.Trim(),
            Owner = identity,
            IsGlobal = false,
            StateJson = JsonSerializer.Serialize(document.State, IrJson.Options),
        };
        try
        {
            await SavedStore(ctx).Create(report, ct);
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return EndpointExtensions.ServerError(ctx, definition.Name, "report document upload", ex);
        }

        return Results.Json(
            Summary(report, identity),
            IrJson.Options,
            statusCode: StatusCodes.Status201Created);
    }

    // --- helpers -------------------------------------------------------------

    private static InteractiveReportOptions Options(HttpContext ctx)
        => ctx.RequestServices.GetRequiredService<IOptionsMonitor<InteractiveReportOptions>>().CurrentValue;

    private static ISavedReportStore SavedStore(HttpContext ctx)
        => ctx.RequestServices.GetRequiredService<ISavedReportStore>();

    private static ConfiguredReportDocumentStore Documents(HttpContext ctx)
        => ctx.RequestServices.GetRequiredService<ConfiguredReportDocumentStore>();

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

    private static SavedReportSummary Summary(SavedReport report, string? caller) => new(
        report.Id,
        report.ReportName,
        report.Title,
        report.IsGlobal,
        report.Owner,
        SavedReportAccessPolicy.IsOwner(report, caller),
        IsReadOnly: false,
        report.ModifiedUtc);

    private static SavedReportSummary Summary(ConfiguredReportDocument document) => new(
        document.Id,
        document.ReportName,
        document.Title,
        IsGlobal: true,
        Owner: null,
        Mine: false,
        IsReadOnly: true,
        document.ModifiedUtc);

    private static bool ConfiguredTitleExists(
        IEnumerable<ConfiguredReportDocument> documents,
        string title)
        => documents.Any(document => !document.Primary
            && string.Equals(document.Title, title.Trim(), StringComparison.OrdinalIgnoreCase));

    private static IResult ConfiguredTitleConflict(string title)
        => Results.Problem(
            title: "Configured report title",
            detail: $"'{title.Trim()}' is supplied by a read-only configured report document; choose another title.",
            statusCode: StatusCodes.Status409Conflict);

    private static async Task<IResult> ReadOnlyConfiguredResult(
        ConfiguredReportDocument document,
        HttpContext ctx,
        CancellationToken ct)
    {
        var definition = await ctx.RequestServices.GetRequiredService<IReportDefinitionStore>()
            .Find(document.ReportName, ct);
        if (definition is null) return Results.NotFound();
        if (await ReportRequestAccess.Authorize(definition, ctx) is { } denied) return denied;
        return Results.Problem(
            title: "Read-only report",
            detail: "Configured report documents cannot be updated or deleted. Use Save As to create an editable copy.",
            statusCode: StatusCodes.Status403Forbidden);
    }

    private static IResult? TitleError(string? title, string problemTitle = "Malformed save request")
        => string.IsNullOrWhiteSpace(title) || title.Trim().Length > 200
            ? BadRequest(problemTitle, "title is required (1–200 characters)")
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

    private static string DownloadFileName(string reportName, string title)
    {
        var stem = $"{reportName}.{title}";
        var safe = new string(stem.Select(character =>
            char.IsLetterOrDigit(character) || character is '.' or '-' or '_'
                ? character
                : '-').ToArray()).Trim('.', '-');
        return (safe.Length == 0 ? "report" : safe) + ".json";
    }
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

internal sealed record SavedReportSummary(
    string Id,
    string ReportName,
    string Title,
    bool IsGlobal,
    string? Owner,
    bool Mine,
    bool IsReadOnly,
    DateTime ModifiedUtc);
