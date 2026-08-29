using System.Text.Json;
using InteractiveReport.AspNetCore.Definitions;
using InteractiveReport.Core.Authorization;
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
///   owner                    → read, update title/state, delete
///   anyone (global/primary) → read
///   administrator           → everything: list all, publish/unpublish global,
///                             reassign owner, update or delete any report
/// Denials hide existence (404) except where the caller already provably knows the
/// resource exists — an owner touching admin-only powers gets an explicit 403.
/// </summary>
internal static class SavedReportEndpoints
{
    // --- whoami --------------------------------------------------------------

    internal static async Task<IResult> Whoami(HttpContext ctx, CancellationToken ct)
    {
        var opts = Options(ctx);
        if (!opts.WhoamiEnabled) return Results.NotFound();

        var identity = ReportIdentity.Resolve(ctx.User, opts.IdentityClaim);
        var database = new DatabaseAdministratorAccess(false, false);
        if (ReportConnectionRegistry.IsStoreConfigured(opts.SavedReports))
        {
            try
            {
                database = await ctx.RequestServices.GetRequiredService<IReportAuthorizationStore>()
                    .GetAdministratorAccess(identity, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return EndpointExtensions.ServerError(
                    ctx, SavedReportsListingDefinition.Name, "identity authorization lookup", ex);
            }
        }

        var configuredAdministrator = ReportIdentity.IsAdministrator(
            ctx.User, opts.IdentityClaim, opts.Administrators);
        var administratorListConfigured = opts.Administrators.Count > 0 || database.Configured;
        var applicationAuthorizationConfigured = ctx.RequestServices
            .GetServices<IInteractiveReportAuthorizer>()
            .Any();
        return Results.Json(new InteractiveReportIdentity(
            Authenticated: ctx.User.Identity?.IsAuthenticated == true,
            // The exact value to put in InteractiveReport:Administrators.
            Identity: identity,
            IsAdministrator: configuredAdministrator || database.UserGranted,
            ConfiguredAdministrator: configuredAdministrator,
            DatabaseAdministrator: database.UserGranted,
            AdministratorListConfigured: administratorListConfigured,
            ApplicationAuthorizationConfigured: applicationAuthorizationConfigured,
            Name: ctx.User.Identity?.Name,
            AuthenticationType: ctx.User.Identity?.AuthenticationType,
            Claims: ctx.User.Claims.Select(c => new InteractiveReportClaim(c.Type, c.Value)).ToArray()),
            IrJson.Options);
    }

    // --- administration user directory -------------------------------------

    internal static async Task<IResult> AdminListUsers(
        HttpContext ctx,
        CancellationToken ct)
    {
        var denied = await Access(ctx).AuthorizeEndpoint(new EndpointAccessRequest
        {
            Actions = [InteractiveReportAction.ListAuthorizationUsers],
            Resource = new InteractiveReportAuthorizationResource
            {
                ReportName = SavedReportsListingDefinition.Name,
            },
            AdministratorRequired = true,
            HideDenied = true,
        }, ctx, ct);
        if (denied is not null) return denied;

        var provider = ctx.RequestServices.GetService<IInteractiveReportUserProvider>();
        if (provider is null)
            return Results.Json(Array.Empty<InteractiveReportUser>(), IrJson.Options);

        try
        {
            var supplied = await provider.GetUsers(ctx.User, ct);
            if (supplied is null || supplied.Count == 0)
                return Results.Json(Array.Empty<InteractiveReportUser>(), IrJson.Options);

            var users = new List<InteractiveReportUser>(supplied.Count);
            var values = new HashSet<string>(StringComparer.Ordinal);
            foreach (var user in supplied)
            {
                if (user is null
                    || string.IsNullOrWhiteSpace(user.Display)
                    || string.IsNullOrWhiteSpace(user.Value))
                    throw new InvalidOperationException(
                        "The Interactive Reports user provider returned an entry with an empty display or value.");

                var normalized = new InteractiveReportUser(user.Display.Trim(), user.Value.Trim());
                if (!values.Add(normalized.Value))
                    throw new InvalidOperationException(
                        $"The Interactive Reports user provider returned duplicate value '{normalized.Value}'.");
                users.Add(normalized);
            }

            return Results.Json(users, IrJson.Options);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return EndpointExtensions.ServerError(
                ctx,
                SavedReportsListingDefinition.Name,
                "administration user lookup",
                ex);
        }
    }

    // --- user surface --------------------------------------------------------

    internal static async Task<IResult> ListForReport(string name, HttpContext ctx, CancellationToken ct)
    {
        var access = await Access(ctx).Authorize(new ReportAccessRequest
        {
            ReportName = name,
            Actions = [InteractiveReportAction.ListSavedReports],
        }, ctx, ct);
        if (access.Error is not null) return access.Error;
        var def = access.Definition!;

        await Synchronizer(ctx).EnsureSynced(ct);
        var identity = Identity(ctx);
        var visible = await SavedStore(ctx).ListVisibleMetadata(def.Name, identity, ct);
        var configured = visible.Where(report => report.Origin == SavedReportOrigin.Configured).ToArray();
        var configuredTitles = configured.Select(report => report.Title)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // A checked-in document is authoritative for its title. Existing database
        // rows remain available to administrators for rename/delete, but do not make
        // the end-user selector ambiguous.
        return Results.Json(
            configured.Select(report => Summary(report, identity)).Concat(
                visible.Where(report => report.Origin == SavedReportOrigin.User
                        && !configuredTitles.Contains(report.Title))
                    .Select(report => Summary(report, identity))),
            IrJson.Options);
    }

    internal static async Task<IResult> Save(string name, HttpContext ctx, CancellationToken ct)
    {
        var identity = Identity(ctx);
        if (identity is null) return Results.Unauthorized();
        InteractiveReportDefinition candidate = null!;
        var access = await Access(ctx).Authorize(new ReportAccessRequest
        {
            ReportName = name,
            Actions = [InteractiveReportAction.CreateSavedReport],
            DenialDetail = "Publishing a global or primary report requires authorization.",
            PrepareResource = async (definition, token) =>
            {
                // Enforced at creation only: existing saved reports stay governed by
                // the ownership matrix, so a config change never strands rows.
                if (Access(ctx).RequireFeature(definition, ReportFeatures.SavedReports) is { } disabled)
                    return new ReportAccessResourcePreparation(null, disabled);

                SaveReportRequest? request;
                try
                {
                    request = await JsonSerializer.DeserializeAsync<SaveReportRequest>(
                        ctx.Request.Body, IrJson.Options, token);
                }
                catch (JsonException ex)
                {
                    return new ReportAccessResourcePreparation(
                        null, BadRequest("Malformed save request", ex.Message));
                }
                if (TitleError(request?.Title) is { } titleError)
                    return new ReportAccessResourcePreparation(null, titleError);
                if (request!.State is null)
                    return new ReportAccessResourcePreparation(
                        null, BadRequest("Malformed save request", "state is required"));

                candidate = new InteractiveReportDefinition
                {
                    Id = SavedReport.NewId(),
                    ReportName = definition.Name,
                    Title = request.Title!.Trim(),
                    Public = request.IsGlobal,
                    Primary = request.IsPrimary,
                    Owner = identity,
                    State = request.State,
                };
                return new ReportAccessResourcePreparation(
                    Resource(definition.Name, definition: candidate));
            },
            AdditionalAdministratorActions = resource =>
                RequiredAdministratorActions(resource.Definition!, current: null, identity),
        }, ctx, ct);
        if (access.Error is not null) return access.Error;
        var def = access.Definition!;

        if (DefinitionError(candidate, "Malformed save request") is { } candidateError)
            return candidateError;
        if (await ValidateSubmittedState(def, candidate, ctx, "saved report creation", ct) is { } stateError)
            return stateError;

        await Synchronizer(ctx).EnsureSynced(ct);
        if (await FindTitleCollision(ctx, def.Name, candidate.Title, exceptId: null, ct) is { } collision)
            return TitleConflict(collision, candidate.Title);

        var report = new SavedReport
        {
            Id = candidate.Id,
            ReportName = def.Name,
            Title = candidate.Title.Trim(),
            Owner = candidate.Owner,
            IsGlobal = candidate.Public,
            IsPrimary = candidate.Primary,
            StateJson = JsonSerializer.Serialize(candidate.State, IrJson.Options),
        };
        try
        {
            await SavedStore(ctx).Create(report, ct);
        }
        catch (SavedReportTitleConflictException conflict)
        {
            return await TitleConflictFromStore(ctx, conflict, exceptId: null, ct);
        }

        return Results.Json(Summary(report, identity), IrJson.Options, statusCode: StatusCodes.Status201Created);
    }

    internal static async Task<IResult> Load(string id, HttpContext ctx, CancellationToken ct)
    {
        await Synchronizer(ctx).EnsureSynced(ct);
        var metadata = await SavedStore(ctx).GetMetadata(id, ct);
        if (metadata is null) return Results.NotFound();

        // Loading a state document still requires access to the underlying report.
        var identity = Identity(ctx);
        var builtIn = SavedReportAccessPolicy.Read(metadata, identity, administrator: false);
        var access = await Access(ctx).Authorize(new ReportAccessRequest
        {
            ReportName = metadata.ReportName,
            Actions = [InteractiveReportAction.ReadSavedReport],
            Resource = Resource(metadata.ReportName, metadata),
            AdministratorRequired = builtIn != SavedReportAccess.Allowed,
            HideDenied = true,
        }, ctx, ct);
        if (access.Error is not null) return access.Error;

        var report = await SavedStore(ctx).Get(id, ct);
        if (report is null) return Results.NotFound();

        using var state = JsonDocument.Parse(report.StateJson);
        return Results.Json(
            new SavedReportDocument(Summary(report, identity), state.RootElement.Clone()),
            IrJson.Options);
    }

    internal static async Task<IResult> Update(string id, HttpContext ctx, CancellationToken ct)
    {
        await Synchronizer(ctx).EnsureSynced(ct);
        var savedStore = SavedStore(ctx);
        var metadata = await savedStore.GetMetadata(id, ct);
        if (metadata is null) return Results.NotFound();

        var identity = Identity(ctx);
        UpdateSavedReportRequest request = null!;
        InteractiveReportDefinition candidate = null!;
        var builtIn = SavedReportAccessPolicy.Modify(metadata, identity, administrator: false);
        var access = await Access(ctx).Authorize(new ReportAccessRequest
        {
            ReportName = metadata.ReportName,
            Actions = [InteractiveReportAction.UpdateSavedReport],
            AdministratorRequired = metadata.Origin == SavedReportOrigin.Configured
                || builtIn != SavedReportAccess.Allowed,
            HideDenied = metadata.Origin != SavedReportOrigin.Configured
                && builtIn == SavedReportAccess.Hidden,
            DenialDetail = metadata.Origin == SavedReportOrigin.Configured
                ? "Changing a configured report requires authorization."
                : "Modifying publication or ownership requires authorization.",
            PrepareResource = async (definition, token) =>
            {
                try
                {
                    request = await JsonSerializer.DeserializeAsync<UpdateSavedReportRequest>(
                        ctx.Request.Body, IrJson.Options, token)
                        ?? throw new JsonException("empty body");
                }
                catch (JsonException ex)
                {
                    return new ReportAccessResourcePreparation(
                        null, BadRequest("Malformed update request", ex.Message));
                }

                candidate = new InteractiveReportDefinition
                {
                    Id = metadata.Id,
                    ReportName = definition.Name,
                    Title = request.Title ?? metadata.Title,
                    Public = request.IsGlobal ?? metadata.IsGlobal,
                    Primary = request.IsPrimary ?? metadata.IsPrimary,
                    Owner = request.Owner ?? metadata.Owner,
                };
                if (request.State is not null)
                    candidate.State = request.State;
                return new ReportAccessResourcePreparation(
                    Resource(definition.Name, metadata, candidate));
            },
            AdditionalAdministratorActions = resource =>
                RequiredAdministratorActions(resource.Definition!, metadata, identity),
        }, ctx, ct);
        if (access.Error is not null) return access.Error;
        var definition = access.Definition!;

        var report = await savedStore.Get(id, ct);
        if (report is null) return Results.NotFound();

        if (metadata.Origin == SavedReportOrigin.Configured)
        {
            if (candidate.StateChanged
                || (!request.IsPrimary.HasValue && candidate.Primary == report.IsPrimary)
                || !string.Equals(candidate.Title, report.Title, StringComparison.Ordinal)
                || candidate.Public != report.IsGlobal
                || !string.Equals(candidate.Owner, report.Owner, StringComparison.Ordinal))
                return ReadOnlyConfiguredResult();

            report.IsPrimary = candidate.Primary;
            return await savedStore.Update(report, ct)
                ? Results.Json(Summary(report, identity), IrJson.Options)
                : Results.NotFound();
        }

        if (DefinitionError(candidate, "Malformed update request") is { } candidateError)
            return candidateError;
        if (await ValidateSubmittedState(definition, candidate, ctx, "saved report update", ct) is { } stateError)
            return stateError;
        if (await FindTitleCollision(ctx, report.ReportName, candidate.Title, report.Id, ct) is { } collision)
            return TitleConflict(collision, candidate.Title);

        report.Title = candidate.Title.Trim();
        if (candidate.StateChanged)
            report.StateJson = JsonSerializer.Serialize(candidate.State, IrJson.Options);
        report.IsGlobal = candidate.Public;
        report.IsPrimary = candidate.Primary;
        report.Owner = candidate.Owner?.Trim();

        try
        {
            return await savedStore.Update(report, ct)
                ? Results.Json(Summary(report, identity), IrJson.Options)
                : Results.NotFound();
        }
        catch (SavedReportTitleConflictException conflict)
        {
            return await TitleConflictFromStore(ctx, conflict, report.Id, ct);
        }
    }

    internal static async Task<IResult> Delete(string id, HttpContext ctx, CancellationToken ct)
    {
        await Synchronizer(ctx).EnsureSynced(ct);
        var savedStore = SavedStore(ctx);
        var report = await savedStore.GetMetadata(id, ct);
        if (report is null) return Results.NotFound();

        var identity = Identity(ctx);
        var builtIn = SavedReportAccessPolicy.Modify(report, identity, administrator: false);
        var access = await Access(ctx).Authorize(new ReportAccessRequest
        {
            ReportName = report.ReportName,
            Actions = [InteractiveReportAction.DeleteSavedReport],
            Resource = Resource(report.ReportName, report),
            AdministratorRequired = report.Origin != SavedReportOrigin.Configured
                && builtIn != SavedReportAccess.Allowed,
            HideDenied = builtIn == SavedReportAccess.Hidden,
            DenialDetail = "Deleting another owner's report requires authorization.",
        }, ctx, ct);
        if (access.Error is not null) return access.Error;

        if (report.Origin == SavedReportOrigin.Configured)
            return ReadOnlyConfiguredResult();

        return await savedStore.Delete(id, ct) ? Results.NoContent() : Results.NotFound();
    }

    // --- administrator surface -----------------------------------------------

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

        await Synchronizer(ctx).EnsureSynced(ct);
        var metadata = await SavedStore(ctx).GetMetadata(id, ct);
        if (metadata is null) return Results.NotFound();
        var reportName = metadata.ReportName;
        var access = await Access(ctx).Authorize(new ReportAccessRequest
        {
            ReportName = reportName,
            Actions = [InteractiveReportAction.DownloadReportDocument],
            Resource = Resource(reportName, metadata),
            AdministratorRequired = true,
            HideDenied = true,
        }, ctx, ct);
        if (access.Error is not null) return access.Error;

        var report = await SavedStore(ctx).Get(id, ct);
        if (report is null) return Results.NotFound();

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

        var document = new ReportDocumentFile
        {
            Title = report.Title,
            Primary = report.IsPrimary,
            State = state,
        };

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
    /// a convenient live test surface; its primary flag becomes stored publication
    /// metadata controlled by the administrator.
    /// </summary>
    internal static async Task<IResult> AdminUploadDocument(
        string name,
        HttpContext ctx,
        CancellationToken ct)
    {
        var identity = Identity(ctx);
        if (identity is null) return Results.Unauthorized();
        InteractiveReportDefinition candidate = null!;
        var access = await Access(ctx).Authorize(new ReportAccessRequest
        {
            ReportName = name,
            Actions = [InteractiveReportAction.UploadReportDocument],
            AdministratorRequired = true,
            HideDenied = true,
            PrepareResource = async (definition, token) =>
            {
                ReportDocumentFile? document;
                try
                {
                    document = await JsonSerializer.DeserializeAsync<ReportDocumentFile>(
                        ctx.Request.Body, IrJson.Options, token);
                }
                catch (JsonException ex)
                {
                    return new ReportAccessResourcePreparation(
                        null, BadRequest("Malformed report document", ex.Message));
                }
                if (TitleError(document?.Title, "Malformed report document") is { } titleError)
                    return new ReportAccessResourcePreparation(null, titleError);
                if (document!.State is null)
                    return new ReportAccessResourcePreparation(
                        null, BadRequest("Malformed report document", "state is required"));

                candidate = new InteractiveReportDefinition
                {
                    Id = SavedReport.NewId(),
                    ReportName = definition.Name,
                    Title = document.Title!.Trim(),
                    Public = false,
                    Primary = document.Primary,
                    Owner = identity,
                    State = document.State,
                };
                return new ReportAccessResourcePreparation(
                    Resource(definition.Name, definition: candidate));
            },
            AdditionalAdministratorActions = resource =>
                RequiredAdministratorActions(resource.Definition!, current: null, identity),
        }, ctx, ct);
        if (access.Error is not null) return access.Error;
        var definition = access.Definition!;

        if (DefinitionError(candidate, "Malformed report document") is { } candidateError)
            return candidateError;
        if (await ValidateSubmittedState(definition, candidate, ctx, "report document upload", ct) is { } stateError)
            return stateError;

        await Synchronizer(ctx).EnsureSynced(ct);
        if (await FindTitleCollision(ctx, definition.Name, candidate.Title, exceptId: null, ct) is { } collision)
            return TitleConflict(collision, candidate.Title);

        var report = new SavedReport
        {
            Id = candidate.Id,
            ReportName = definition.Name,
            Title = candidate.Title.Trim(),
            Owner = candidate.Owner,
            IsGlobal = candidate.Public,
            IsPrimary = candidate.Primary,
            StateJson = JsonSerializer.Serialize(candidate.State, IrJson.Options),
        };
        try
        {
            await SavedStore(ctx).Create(report, ct);
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (SavedReportTitleConflictException conflict)
        {
            return await TitleConflictFromStore(ctx, conflict, exceptId: null, ct);
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

    private static IReportAccessService Access(HttpContext ctx)
        => ctx.RequestServices.GetRequiredService<IReportAccessService>();

    private static ConfiguredReportDocumentSynchronizer Synchronizer(HttpContext ctx)
        => ctx.RequestServices.GetRequiredService<ConfiguredReportDocumentSynchronizer>();

    private static string? Identity(HttpContext ctx)
    {
        var opts = Options(ctx);
        return ReportIdentity.Resolve(ctx.User, opts.IdentityClaim);
    }

    private static SavedReportSummary Summary(SavedReportMetadata report, string? caller) => new(
        report.Id,
        report.ReportName,
        report.Title,
        report.IsGlobal,
        report.IsPrimary,
        SavedReportAccessPolicy.IsOwner(report, caller),
        IsReadOnly: report.Origin == SavedReportOrigin.Configured,
        report.ModifiedUtc);

    private static SavedReportSummary Summary(SavedReport report, string? caller)
        => Summary(report.Metadata(), caller);

    private static IEnumerable<InteractiveReportAction> RequiredAdministratorActions(
        InteractiveReportDefinition candidate,
        SavedReportMetadata? current,
        string? originalOwner)
    {
        if (candidate.Public != (current?.IsGlobal ?? false))
            yield return InteractiveReportAction.PublishGlobalReport;
        if (candidate.Primary != (current?.IsPrimary ?? false))
            yield return InteractiveReportAction.PublishPrimaryReport;
        var existingOwner = current is null ? originalOwner : current.Owner;
        if (!string.Equals(candidate.Owner, existingOwner, StringComparison.Ordinal))
            yield return InteractiveReportAction.ChangeSavedReportOwner;
    }

    private static async Task<IResult?> ValidateSubmittedState(
        ReportDefinition reportDefinition,
        InteractiveReportDefinition candidate,
        HttpContext ctx,
        string operation,
        CancellationToken ct)
    {
        if (!candidate.StateChanged) return null;
        if (candidate.State is null)
            return BadRequest("Malformed report definition", "state is required");

        try
        {
            var contextParams = await Access(ctx).ResolveContextParameters(
                reportDefinition,
                ctx,
                ct);
            await ctx.RequestServices.GetRequiredService<ReportExecutor>()
                .ValidateDocument(reportDefinition, candidate.State, contextParams, ct);
            return null;
        }
        catch (ReportValidationException ex)
        {
            return EndpointExtensions.ValidationProblem(ex);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return EndpointExtensions.ServerError(ctx, reportDefinition.Name, operation, ex);
        }
    }

    private static InteractiveReportAuthorizationResource Resource(
        string reportName,
        SavedReportMetadata? report = null,
        InteractiveReportDefinition? definition = null)
        => new()
        {
            ReportName = reportName,
            SavedReport = report is null
                ? null
                : new SavedReportAuthorizationResource(
                    report.Id,
                    report.Title,
                    report.Owner,
                    report.IsGlobal,
                    report.IsPrimary,
                    report.Origin),
            Definition = definition,
        };

    /// <summary>
    /// Title uniqueness spans one report definition's rows of both origins — synced
    /// configured rows included, so callers must EnsureSynced first.
    /// </summary>
    private static async Task<SavedReport?> FindTitleCollision(
        HttpContext ctx,
        string reportName,
        string title,
        string? exceptId,
        CancellationToken ct)
        => await SavedStore(ctx).FindByTitle(reportName, title, exceptId, ct);

    /// <summary>
    /// The store's unique index caught a save the advisory pre-check missed (a
    /// concurrent writer). Re-reading the collision row recovers the precise 409
    /// wording (configured versus user); when the winner vanished again in between,
    /// the generic user-collision wording stands.
    /// </summary>
    private static async Task<IResult> TitleConflictFromStore(
        HttpContext ctx,
        SavedReportTitleConflictException conflict,
        string? exceptId,
        CancellationToken ct)
    {
        var collision = await FindTitleCollision(ctx, conflict.ReportName, conflict.Title, exceptId, ct);
        return collision is not null
            ? TitleConflict(collision, conflict.Title)
            : Results.Problem(
                title: "Saved report title",
                detail: $"A saved report named '{conflict.Title.Trim()}' already exists. Replace it if it is available to you, or choose another title.",
                statusCode: StatusCodes.Status409Conflict);
    }

    private static IResult TitleConflict(SavedReport collision, string title)
        => collision.Origin == SavedReportOrigin.Configured
            ? Results.Problem(
                title: "Configured report title",
                detail: $"'{title.Trim()}' is supplied by a read-only configured report document; choose another title.",
                statusCode: StatusCodes.Status409Conflict)
            : Results.Problem(
                title: "Saved report title",
                detail: $"A saved report named '{title.Trim()}' already exists. Replace it if it is available to you, or choose another title.",
                statusCode: StatusCodes.Status409Conflict);

    private static IResult ReadOnlyConfiguredResult()
        => Results.Problem(
            title: "Read-only report",
            detail: "Configured report documents cannot be updated or deleted. Use Save As to create an editable copy.",
            statusCode: StatusCodes.Status403Forbidden);

    private static IResult? TitleError(string? title, string problemTitle = "Malformed save request")
        => string.IsNullOrWhiteSpace(title) || title.Trim().Length > 200
            ? BadRequest(problemTitle, "title is required (1–200 characters)")
            : null;

    private static IResult? DefinitionError(
        InteractiveReportDefinition definition,
        string problemTitle)
    {
        if (TitleError(definition.Title, problemTitle) is { } titleError)
            return titleError;
        if (definition.Owner is not null && string.IsNullOrWhiteSpace(definition.Owner))
            return BadRequest(problemTitle, "owner must be a non-empty identity value");
        if (definition.StateChanged && definition.State is null)
            return BadRequest(problemTitle, "state is required");
        return null;
    }

    private static IResult BadRequest(string title, string detail)
        => Results.Problem(title: title, detail: detail, statusCode: StatusCodes.Status400BadRequest);

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
