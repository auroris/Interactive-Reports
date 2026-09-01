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
/// Implements identity, saved-report, report-document, and authorization-user HTTP operations.
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
    // Identity bootstrap.

    /// <summary>
    /// Returns the exact identity and administrator sources Interactive Reports sees for the current caller.
    /// </summary>
    /// <param name="ctx">The current HTTP request and response context.</param>
    /// <param name="ct">Cancels database administrator lookup.</param>
    /// <returns>Identity diagnostics as JSON, a disabled-endpoint 404, or a sanitized lookup failure.</returns>
    /// <remarks>May read administrator persistence; it does not mutate identity or authorization state.</remarks>
    internal static async Task<IResult> Whoami(HttpContext ctx, CancellationToken ct)
    {
        var opts = Options(ctx);
        if (!opts.WhoamiEnabled)
            return EndpointExtensions.Error(
                InteractiveReportErrorCodes.EndpointNotFound,
                StatusCodes.Status404NotFound);

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
            // Expose the exact value an operator would place in InteractiveReport:Administrators.
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

    // Application-provided authorization user directory.

    /// <summary>
    /// Returns application-provided identity choices after administrator authorization.
    /// </summary>
    /// <param name="ctx">The current HTTP request and response context.</param>
    /// <param name="ct">Cancels authorization and user-provider lookup.</param>
    /// <returns>A normalized JSON user list, a hidden-denial result, or a sanitized provider failure.</returns>
    /// <remarks>Invokes the optional host user provider and rejects blank or duplicate identity values.</remarks>
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

    // End-user saved-report surface.

    /// <summary>Lists every report document visible through the caller's authorized report definitions.</summary>
    internal static async Task<IResult> ListAvailable(HttpContext ctx, CancellationToken ct)
    {
        await Synchronizer(ctx).EnsureSynced(ct);
        var identity = Identity(ctx);
        var options = Options(ctx);
        var reportNames = options.Reports.Keys.ToList();
        if (ReportConnectionRegistry.IsStoreConfigured(options.SavedReports))
            reportNames.Add(SavedReportsListingDefinition.Name);

        var summaries = new List<SavedReportSummary>();
        IResult? firstDenial = null;
        var authorizedDefinitions = 0;
        foreach (var reportName in reportNames)
        {
            var access = await Access(ctx).Authorize(new ReportAccessRequest
            {
                ReportName = reportName,
                Actions = SavedReportsListingDefinition.Matches(reportName)
                    ? [InteractiveReportAction.ListAllSavedReports]
                    : [InteractiveReportAction.ListSavedReports],
                HideDenied = true,
            }, ctx, ct);
            if (access.Error is not null)
            {
                firstDenial ??= access.Error;
                if (access.Error is IStatusCodeHttpResult { StatusCode: >= 500 })
                    return access.Error;
                continue;
            }

            authorizedDefinitions++;
            var definition = access.Definition!;
            var visible = await SavedStore(ctx).ListVisibleMetadata(definition.Name, identity, ct);
            if (!visible.Any(report => report.IsDefault))
            {
                await DefaultDocuments(ctx).CreateMissing(definition, ct);
                visible = await SavedStore(ctx).ListVisibleMetadata(definition.Name, identity, ct);
            }
            summaries.AddRange(visible.Select(report => Summary(report, identity)));
        }

        return authorizedDefinitions == 0 && firstDenial is not null
            ? firstDenial
            : Results.Json(summaries, IrJson.Options);
    }

    /// <summary>
    /// Lists saved reports visible to the caller for one authorized report definition.
    /// </summary>
    /// <param name="id">The numeric document id used to select its report family.</param>
    /// <param name="ctx">The current HTTP request and response context.</param>
    /// <param name="ct">Cancels authorization, document synchronization, and persistence reads.</param>
    /// <returns>A JSON array containing public documents and the caller's private documents.</returns>
    /// <remarks>Synchronizes configured document identities before listing.</remarks>
    internal static async Task<IResult> ListForReport(long id, HttpContext ctx, CancellationToken ct)
    {
        await Synchronizer(ctx).EnsureSynced(ct);
        var anchor = await SavedStore(ctx).GetMetadata(id, ct);
        if (anchor is null) return EndpointExtensions.SavedReportNotFound();
        var identity = Identity(ctx);
        var builtIn = SavedReportAccessPolicy.Read(anchor, identity, administrator: false);
        var access = await Access(ctx).Authorize(new ReportAccessRequest
        {
            ReportName = anchor.ReportName,
            Actions = [InteractiveReportAction.ReadSavedReport, InteractiveReportAction.ListSavedReports],
            Resource = Resource(anchor.ReportName, anchor),
            AdministratorRequired = builtIn != SavedReportAccess.Allowed,
            HideDenied = builtIn != SavedReportAccess.Allowed,
        }, ctx, ct);
        if (access.Error is not null) return access.Error;
        var def = access.Definition!;

        var visible = await SavedStore(ctx).ListVisibleMetadata(def.Name, identity, ct);
        return Results.Json(visible.Select(report => Summary(report, identity)), IrJson.Options);
    }

    /// <summary>
    /// Validates and creates a private, global, or primary saved report owned by the current caller.
    /// </summary>
    /// <param name="id">The numeric document id used to select its report family.</param>
    /// <param name="ctx">The current HTTP request and response context.</param>
    /// <param name="ct">Cancels authorization, request-body reading, state validation, synchronization, and persistence.</param>
    /// <returns>The created summary with HTTP 201, or an authentication, access, validation, or title-conflict result.</returns>
    /// <remarks>Consumes the JSON request body, may refresh schema caches in the submitted state, synchronizes configured documents, and inserts one saved-report row.</remarks>
    internal static async Task<IResult> Save(long id, HttpContext ctx, CancellationToken ct)
    {
        var identity = Identity(ctx);
        if (identity is null) return EndpointExtensions.AuthenticationRequired();
        await Synchronizer(ctx).EnsureSynced(ct);
        var anchor = await SavedStore(ctx).GetMetadata(id, ct);
        if (anchor is null) return EndpointExtensions.SavedReportNotFound();
        var builtIn = SavedReportAccessPolicy.Read(anchor, identity, administrator: false);
        InteractiveReportDefinition candidate = null!;
        var access = await Access(ctx).Authorize(new ReportAccessRequest
        {
            ReportName = anchor.ReportName,
            Actions = [InteractiveReportAction.ReadSavedReport, InteractiveReportAction.CreateSavedReport],
            AdministratorRequired = builtIn != SavedReportAccess.Allowed,
            HideDenied = builtIn != SavedReportAccess.Allowed,
            DenialDetail = "Publishing a global or primary report requires authorization.",
            PrepareResource = async (definition, token) =>
            {
                // Enforce the feature at creation only. Existing saved reports stay governed by
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
                        null, BadRequest(
                            InteractiveReportErrorCodes.MalformedSaveRequest,
                            ex.Message));
                }
                if (TitleError(
                        request?.Title,
                        InteractiveReportErrorCodes.SavedReportTitleInvalid) is { } titleError)
                    return new ReportAccessResourcePreparation(null, titleError);
                if (request!.State is null)
                    return new ReportAccessResourcePreparation(
                        null, BadRequest(
                            InteractiveReportErrorCodes.SavedReportStateRequired));

                candidate = new InteractiveReportDefinition
                {
                    Id = 0,
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

        if (DefinitionError(
                candidate,
                InteractiveReportErrorCodes.SavedReportTitleInvalid,
                InteractiveReportErrorCodes.SavedReportStateRequired) is { } candidateError)
            return candidateError;
        if (await ValidateSubmittedState(def, candidate, ctx, "saved report creation", ct) is { } stateError)
            return stateError;

        var candidateIsPublic = candidate.Public || candidate.Primary;
        if (await FindTitleCollision(
                ctx, def.Name, candidate.Title, identity, candidateIsPublic, exceptId: null, ct) is { } collision)
            return TitleConflict(collision, candidate.Title);

        var report = new SavedReport
        {
            Id = 0,
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
            return await TitleConflictFromStore(
                ctx, conflict, identity, candidateIsPublic, exceptId: null, ct);
        }

        return Results.Json(Summary(report, identity), IrJson.Options, statusCode: StatusCodes.Status201Created);
    }

    /// <summary>
    /// Loads one visible saved report and returns its metadata plus raw report-state document.
    /// </summary>
    /// <param name="id">The numeric report-document identifier from the route.</param>
    /// <param name="ctx">The current HTTP request and response context.</param>
    /// <param name="ct">Cancels persistence reads, authorization, and missing-file cleanup.</param>
    /// <returns>The saved-report document JSON, or a hidden not-found/access result.</returns>
    /// <remarks>Trusts database identity metadata. A missing configured file deletes its stale row, restores a synthetic default when needed, and returns 404.</remarks>
    internal static async Task<IResult> Load(long id, HttpContext ctx, CancellationToken ct)
    {
        var report = await SavedStore(ctx).Get(id, ct);
        if (report is null) return EndpointExtensions.SavedReportNotFound();
        var metadata = report.Metadata();

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

        try
        {
            JsonElement state;
            if (report.Origin == SavedReportOrigin.Configured)
            {
                if (report.SourceFile is null
                    || ConfiguredDocuments(ctx).Find(report.ReportName, report.SourceFile) is not { } file)
                {
                    await RemoveMissingConfiguredDocument(report, access.Definition!, ctx, ct);
                    return EndpointExtensions.SavedReportNotFound();
                }
                state = JsonSerializer.SerializeToElement(file.State, IrJson.Options);
            }
            else if (report.IsDefault)
            {
                var contextParameters = await Access(ctx).ResolveContextParameters(
                    access.Definition!, ctx, ct);
                var loaded = await DefaultDocuments(ctx).LoadState(
                    report,
                    access.Definition!,
                    ctx.RequestServices.GetRequiredService<ReportExecutor>(),
                    contextParameters,
                    ct);
                report = loaded.Report;
                state = JsonSerializer.SerializeToElement(loaded.State, IrJson.Options);
            }
            else
            {
                using var document = JsonDocument.Parse(
                    report.StateJson ?? throw new JsonException("The report document has no state."));
                state = document.RootElement.Clone();
            }

            return Results.Json(
                new SavedReportDocument(
                    Summary(report, identity),
                    state),
                IrJson.Options);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return EndpointExtensions.ServerError(ctx, report.ReportName, "report document retrieval", ex);
        }
    }

    /// <summary>
    /// Applies a partial update to a user report, or an explicit primary-flag update to a configured document.
    /// </summary>
    /// <param name="id">The numeric report-document identifier from the route.</param>
    /// <param name="ctx">The current HTTP request and response context.</param>
    /// <param name="ct">Cancels synchronization, body reading, authorization, validation, and persistence.</param>
    /// <returns>The updated summary, or a not-found, read-only, validation, denial, or title-conflict result.</returns>
    /// <remarks>Consumes the JSON request body and conditionally updates persistence. State changes are rebound and receive refreshed schema caches before storage.</remarks>
    internal static async Task<IResult> Update(long id, HttpContext ctx, CancellationToken ct)
    {
        await Synchronizer(ctx).EnsureSynced(ct);
        var savedStore = SavedStore(ctx);
        var current = await savedStore.Get(id, ct);
        if (current is null) return EndpointExtensions.SavedReportNotFound();
        var metadata = current.Metadata();

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
                        null, BadRequest(
                            InteractiveReportErrorCodes.MalformedUpdateRequest,
                            ex.Message));
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

        var report = current with { };

        if (metadata.Origin == SavedReportOrigin.Configured)
        {
            if (candidate.StateChanged
                || (!request.IsPrimary.HasValue && candidate.Primary == report.IsPrimary)
                || !string.Equals(candidate.Title, report.Title, StringComparison.Ordinal)
                || candidate.Public != report.IsGlobal
                || !string.Equals(candidate.Owner, report.Owner, StringComparison.Ordinal))
                return ReadOnlyConfiguredResult();

            report.IsPrimary = candidate.Primary;
            return await savedStore.Update(report, current, ct)
                ? Results.Json(Summary(report, identity), IrJson.Options)
                : EndpointExtensions.SavedReportNotFound();
        }

        if (DefinitionError(
                candidate,
                InteractiveReportErrorCodes.SavedReportTitleInvalid,
                InteractiveReportErrorCodes.SavedReportStateRequired) is { } candidateError)
            return candidateError;
        if (await ValidateSubmittedState(definition, candidate, ctx, "saved report update", ct) is { } stateError)
            return stateError;
        var titleChanged = !string.Equals(
            NormalizeTitle(candidate.Title),
            NormalizeTitle(report.Title),
            StringComparison.Ordinal);
        var scopeChanged = candidate.Public != report.IsGlobal || candidate.Primary != report.IsPrimary;
        var candidateIsPublic = candidate.Public || candidate.Primary;
        if ((titleChanged || scopeChanged)
            && await FindTitleCollision(
                ctx,
                report.ReportName,
                candidate.Title,
                candidate.Owner,
                candidateIsPublic,
                report.Id,
                ct) is { } collision)
            return TitleConflict(collision, candidate.Title);

        report.Title = candidate.Title.Trim();
        if (candidate.StateChanged)
            report.StateJson = JsonSerializer.Serialize(candidate.State, IrJson.Options);
        report.IsGlobal = candidate.Public;
        report.IsPrimary = candidate.Primary;
        report.Owner = candidate.Owner?.Trim();

        try
        {
            return await savedStore.Update(report, current, ct)
                ? Results.Json(Summary(report, identity), IrJson.Options)
                : EndpointExtensions.SavedReportNotFound();
        }
        catch (SavedReportTitleConflictException conflict)
        {
            return await TitleConflictFromStore(
                ctx, conflict, report.Owner, report.IsPublic, report.Id, ct);
        }
    }

    /// <summary>
    /// Deletes an authorized user-authored saved report.
    /// </summary>
    /// <param name="id">The numeric report-document identifier from the route.</param>
    /// <param name="ctx">The current HTTP request and response context.</param>
    /// <param name="ct">Cancels synchronization, persistence reads, authorization, and deletion.</param>
    /// <returns>No content on deletion, or a hidden not-found, denial, or configured-read-only result.</returns>
    /// <remarks>Synchronizes configured documents and deletes one persistence row when it still matches the loaded version.</remarks>
    internal static async Task<IResult> Delete(long id, HttpContext ctx, CancellationToken ct)
    {
        await Synchronizer(ctx).EnsureSynced(ct);
        var savedStore = SavedStore(ctx);
        var report = await savedStore.Get(id, ct);
        if (report is null) return EndpointExtensions.SavedReportNotFound();

        var identity = Identity(ctx);
        var builtIn = SavedReportAccessPolicy.Modify(report, identity, administrator: false);
        var access = await Access(ctx).Authorize(new ReportAccessRequest
        {
            ReportName = report.ReportName,
            Actions = [InteractiveReportAction.DeleteSavedReport],
            Resource = Resource(report.ReportName, report.Metadata()),
            AdministratorRequired = report.Origin != SavedReportOrigin.Configured
                && builtIn != SavedReportAccess.Allowed,
            HideDenied = builtIn == SavedReportAccess.Hidden,
            DenialDetail = "Deleting another owner's report requires authorization.",
        }, ctx, ct);
        if (access.Error is not null) return access.Error;

        if (report.Origin == SavedReportOrigin.Configured)
            return ReadOnlyConfiguredResult();

        return await savedStore.Delete(report, ct)
            ? Results.NoContent()
            : EndpointExtensions.SavedReportNotFound();
    }

    // Administrator report-document interchange.

    /// <summary>
    /// Downloads the canonical source-controlled envelope, not the endpoint's
    /// summary/state response wrapper. The resulting file can be placed directly in a report definition's
    /// documentFiles collection after the operator chooses whether it should be primary.
    /// </summary>
    /// <param name="id">The numeric report-document identifier from the route.</param>
    /// <param name="ctx">The current HTTP request and response context.</param>
    /// <param name="ct">Cancels synchronization, persistence reads, and administrator authorization.</param>
    /// <returns>An indented JSON file result, or an authentication, hidden-denial, not-found, or server-error result.</returns>
    /// <remarks>Reads and deserializes the stored state; it does not mutate the saved report.</remarks>
    internal static async Task<IResult> AdminDownloadDocument(
        long id,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (Identity(ctx) is null) return EndpointExtensions.AuthenticationRequired();

        var report = await SavedStore(ctx).Get(id, ct);
        if (report is null) return EndpointExtensions.SavedReportNotFound();
        var metadata = report.Metadata();
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

        ReportState? state;
        try
        {
            if (report.Origin == SavedReportOrigin.Configured)
            {
                state = report.SourceFile is null
                    ? null
                    : ConfiguredDocuments(ctx).Find(report.ReportName, report.SourceFile)?.State;
                if (state is null)
                {
                    await RemoveMissingConfiguredDocument(report, access.Definition!, ctx, ct);
                    return EndpointExtensions.SavedReportNotFound();
                }
            }
            else if (report.IsDefault)
            {
                var contextParameters = await Access(ctx).ResolveContextParameters(
                    access.Definition!, ctx, ct);
                (_, state) = await DefaultDocuments(ctx).LoadState(
                    report,
                    access.Definition!,
                    ctx.RequestServices.GetRequiredService<ReportExecutor>(),
                    contextParameters,
                    ct);
            }
            else
            {
                state = JsonSerializer.Deserialize<ReportState>(
                    report.StateJson ?? throw new JsonException("The report document has no state."),
                    IrJson.Options);
            }
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
            Default = report.IsDefault,
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
    /// Imports a canonical report-document file as a private saved report owned by
    /// the administrator. This deliberately bypasses the end-user savedReports feature flag, but not report
    /// authorization or document validation. File publication metadata is ignored; the imported copy is a
    /// private, editable test surface that may be published through a later update.
    /// </summary>
    /// <param name="id">The numeric report-document id used to resolve the report family.</param>
    /// <param name="ctx">The current HTTP request and response context.</param>
    /// <param name="ct">Cancels body reading, administrator authorization, validation, synchronization, and persistence.</param>
    /// <returns>The imported summary with HTTP 201, or an authentication, hidden-denial, validation, title-conflict, or server-error result.</returns>
    /// <remarks>Consumes the JSON body, refreshes schema caches in the imported state, and inserts a user-origin saved-report row.</remarks>
    internal static async Task<IResult> AdminUploadDocument(
        long id,
        HttpContext ctx,
        CancellationToken ct)
    {
        var identity = Identity(ctx);
        if (identity is null) return EndpointExtensions.AuthenticationRequired();
        await Synchronizer(ctx).EnsureSynced(ct);
        var anchor = await SavedStore(ctx).GetMetadata(id, ct);
        if (anchor is null) return EndpointExtensions.SavedReportNotFound();
        InteractiveReportDefinition candidate = null!;
        var access = await Access(ctx).Authorize(new ReportAccessRequest
        {
            ReportName = anchor.ReportName,
            Actions = [InteractiveReportAction.ReadSavedReport, InteractiveReportAction.UploadReportDocument],
            Resource = Resource(anchor.ReportName, anchor),
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
                        null, BadRequest(
                            InteractiveReportErrorCodes.MalformedReportDocument,
                            ex.Message));
                }
                if (TitleError(
                        document?.Title,
                        InteractiveReportErrorCodes.ReportDocumentTitleInvalid) is { } titleError)
                    return new ReportAccessResourcePreparation(null, titleError);
                if (document!.State is null)
                    return new ReportAccessResourcePreparation(
                        null, BadRequest(
                            InteractiveReportErrorCodes.ReportDocumentStateRequired));

                candidate = new InteractiveReportDefinition
                {
                    Id = 0,
                    ReportName = definition.Name,
                    Title = document.Title!.Trim(),
                    Public = false,
                    Primary = false,
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

        if (DefinitionError(
                candidate,
                InteractiveReportErrorCodes.ReportDocumentTitleInvalid,
                InteractiveReportErrorCodes.ReportDocumentStateRequired) is { } candidateError)
            return candidateError;
        if (await ValidateSubmittedState(definition, candidate, ctx, "report document upload", ct) is { } stateError)
            return stateError;

        var candidateIsPublic = candidate.Public || candidate.Primary;
        if (await FindTitleCollision(
                ctx,
                definition.Name,
                candidate.Title,
                identity,
                candidateIsPublic,
                exceptId: null,
                ct) is { } collision)
            return TitleConflict(collision, candidate.Title);

        var report = new SavedReport
        {
            Id = 0,
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
            return await TitleConflictFromStore(
                ctx, conflict, identity, candidateIsPublic, exceptId: null, ct);
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

    // Request-scoped services and protocol helpers.

    /// <summary>
    /// Returns the current Interactive Reports options from the request services.
    /// </summary>
    /// <param name="ctx">The current HTTP request and response context.</param>
    /// <returns>The interactive report options.</returns>
    private static InteractiveReportOptions Options(HttpContext ctx)
        => ctx.RequestServices.GetRequiredService<IOptionsMonitor<InteractiveReportOptions>>().CurrentValue;

    /// <summary>
    /// Resolves the configured saved-report store from request services.
    /// </summary>
    /// <param name="ctx">The current HTTP request and response context.</param>
    /// <returns>The configured saved-report store.</returns>
    private static ISavedReportStore SavedStore(HttpContext ctx)
        => ctx.RequestServices.GetRequiredService<ISavedReportStore>();

    /// <summary>
    /// Resolves the configured report-access service from request services.
    /// </summary>
    /// <param name="ctx">The current HTTP request and response context.</param>
    /// <returns>The configured report access service.</returns>
    private static IReportAccessService Access(HttpContext ctx)
        => ctx.RequestServices.GetRequiredService<IReportAccessService>();

    /// <summary>
    /// Resolves the configured document synchronizer from request services.
    /// </summary>
    /// <param name="ctx">The current HTTP request and response context.</param>
    /// <returns>The configured report document synchronizer.</returns>
    private static ConfiguredReportDocumentSynchronizer Synchronizer(HttpContext ctx)
        => ctx.RequestServices.GetRequiredService<ConfiguredReportDocumentSynchronizer>();

    private static DefaultReportDocumentService DefaultDocuments(HttpContext ctx)
        => ctx.RequestServices.GetRequiredService<DefaultReportDocumentService>();

    private static ConfiguredReportDocumentStore ConfiguredDocuments(HttpContext ctx)
        => ctx.RequestServices.GetRequiredService<ConfiguredReportDocumentStore>();

    /// <summary>Removes a stale configured identity and ensures its family again has a durable default.</summary>
    private static async Task RemoveMissingConfiguredDocument(
        SavedReport report,
        ReportDefinition definition,
        HttpContext ctx,
        CancellationToken ct)
    {
        await Synchronizer(ctx).RemoveMissing(report, ct);
        await DefaultDocuments(ctx).CreateMissing(definition, ct);
    }

    /// <summary>
    /// Resolves the caller identity used for saved-report ownership checks.
    /// </summary>
    /// <param name="ctx">The current HTTP request and response context.</param>
    /// <returns>The normalized identity selected by <c>IdentityClaim</c>, or <see langword="null"/> for an unauthenticated or unresolved caller.</returns>
    private static string? Identity(HttpContext ctx)
    {
        var opts = Options(ctx);
        return ReportIdentity.Resolve(ctx.User, opts.IdentityClaim);
    }

    /// <summary>
    /// Projects saved-report metadata into the public summary response.
    /// </summary>
    /// <param name="report">The metadata to expose.</param>
    /// <param name="caller">The normalized caller identity used to compute the <c>Mine</c> flag.</param>
    /// <returns>The public metadata projection, including ownership and configured-read-only flags.</returns>
    private static SavedReportSummary Summary(SavedReportMetadata report, string? caller) => new(
        report.Id,
        report.ReportName,
        report.Title,
        report.IsGlobal,
        report.IsDefault,
        report.IsPrimary,
        SavedReportAccessPolicy.IsOwner(report, caller),
        report.Origin == SavedReportOrigin.Configured,
        report.ModifiedUtc);

    /// <summary>
    /// Projects saved-report metadata into the public summary response.
    /// </summary>
    /// <param name="report">The persisted row whose metadata should be exposed.</param>
    /// <param name="caller">The normalized caller identity used to compute the <c>Mine</c> flag.</param>
    /// <returns>The public metadata projection, including ownership and configured-read-only flags.</returns>
    private static SavedReportSummary Summary(SavedReport report, string? caller)
        => Summary(report.Metadata(), caller);

    /// <summary>
    /// Yields the administrator-only actions implied by changes to publication or ownership.
    /// </summary>
    /// <param name="candidate">The proposed saved-report definition.</param>
    /// <param name="current">The persisted metadata being updated, or <see langword="null"/> during creation.</param>
    /// <param name="originalOwner">The caller who will own a newly created report.</param>
    /// <returns>Zero or more publish-global, publish-primary, and change-owner actions in that order.</returns>
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

    /// <summary>
    /// Validates a changed state against the live report and replaces it with refreshed schema caches.
    /// </summary>
    /// <param name="reportDefinition">The authorized executable definition used for binding and schema discovery.</param>
    /// <param name="candidate">The mutable saved-report candidate containing the submitted state.</param>
    /// <param name="ctx">The current HTTP request and response context.</param>
    /// <param name="operation">The diagnostic operation name used if validation fails unexpectedly.</param>
    /// <param name="ct">Cancels context resolution and schema refresh.</param>
    /// <returns><see langword="null"/> when unchanged or valid; otherwise, a validation or sanitized server-error result.</returns>
    /// <remarks>When state changed, mutates <paramref name="candidate"/> by replacing its state with the canonical cache-refreshed document and may query database schema.</remarks>
    private static async Task<IResult?> ValidateSubmittedState(
        ReportDefinition reportDefinition,
        InteractiveReportDefinition candidate,
        HttpContext ctx,
        string operation,
        CancellationToken ct)
    {
        if (!candidate.StateChanged) return null;
        if (candidate.State is null)
            return BadRequest(
                InteractiveReportErrorCodes.ReportDefinitionStateRequired);

        try
        {
            var contextParams = await Access(ctx).ResolveContextParameters(
                reportDefinition,
                ctx,
                ct);
            candidate.State = await ctx.RequestServices.GetRequiredService<ReportExecutor>()
                .RefreshSchemaCaches(reportDefinition, candidate.State, contextParams, ct);
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

    /// <summary>
    /// Builds the authorization resource passed to the host policy service.
    /// </summary>
    /// <param name="reportName">The canonical configured report name.</param>
    /// <param name="report">Optional persisted metadata for read, update, or delete authorization.</param>
    /// <param name="definition">Optional client-authored candidate for create or update authorization.</param>
    /// <returns>A detached resource containing the supplied report and candidate projections.</returns>
    internal static InteractiveReportAuthorizationResource Resource(
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
    /// Finds a same-title row across both configured and user origins. Title uniqueness spans one report
    /// definition's rows and synced
    /// configured rows included, so callers must EnsureSynced first.
    /// </summary>
    /// <param name="ctx">The current HTTP request and response context.</param>
    /// <param name="reportName">The configured report name whose definition or saved reports are being addressed.</param>
    /// <param name="title">The normalized saved-report title to compare or persist.</param>
    /// <param name="exceptId">A saved-report identifier to exclude from the title-collision search; <see langword="null"/> excludes none.</param>
    /// <param name="ct">Cancels persistence lookup.</param>
    /// <returns>The conflicting row, or <see langword="null"/> when the title is available.</returns>
    private static async Task<SavedReport?> FindTitleCollision(
        HttpContext ctx,
        string reportName,
        string title,
        string? owner,
        bool isPublic,
        long? exceptId,
        CancellationToken ct)
        => await SavedStore(ctx).FindTitleCollision(
            reportName, title, owner, isPublic, exceptId, ct);

    /// <summary>
    /// Translates a unique-index race after re-reading the winning row. The store's index caught a save the advisory pre-check missed, usually a
    /// concurrent writer). Re-reading the collision row recovers the precise 409 wording (configured versus
    /// user); when the winner vanished again in between, the generic user-collision wording stands.
    /// </summary>
    /// <param name="ctx">The current HTTP request and response context.</param>
    /// <param name="conflict">The conflicting saved-report title or store exception to translate.</param>
    /// <param name="exceptId">A saved-report identifier to exclude from the title-collision search; <see langword="null"/> excludes none.</param>
    /// <param name="ct">Cancels collision re-read.</param>
    /// <returns>A configured- or user-specific HTTP 409 result.</returns>
    private static async Task<IResult> TitleConflictFromStore(
        HttpContext ctx,
        SavedReportTitleConflictException conflict,
        string? owner,
        bool isPublic,
        long? exceptId,
        CancellationToken ct)
    {
        var collision = await FindTitleCollision(
            ctx, conflict.ReportName, conflict.Title, owner, isPublic, exceptId, ct);
        return collision is not null
            ? TitleConflict(collision, conflict.Title)
            : EndpointExtensions.Error(
                InteractiveReportErrorCodes.SavedReportTitleConflict,
                StatusCodes.Status409Conflict,
                $"A saved report named '{conflict.Title.Trim()}' already exists. Replace it if it is available to you, or choose another title.");
    }

    /// <summary>
    /// Creates the conflict response for a duplicate saved-report title.
    /// </summary>
    /// <param name="collision">The persisted row already using the title.</param>
    /// <param name="title">The requested title included in caller-safe detail.</param>
    /// <returns>A coded HTTP 409 result that distinguishes configured read-only documents from user reports.</returns>
    private static IResult TitleConflict(SavedReport collision, string title)
        => collision.Origin == SavedReportOrigin.Configured
            ? EndpointExtensions.Error(
                InteractiveReportErrorCodes.ConfiguredReportTitleConflict,
                StatusCodes.Status409Conflict,
                $"'{title.Trim()}' is supplied by a read-only configured report document; choose another title.")
            : EndpointExtensions.Error(
                InteractiveReportErrorCodes.SavedReportTitleConflict,
                StatusCodes.Status409Conflict,
                $"A saved report named '{title.Trim()}' already exists. Replace it if it is available to you, or choose another title.");

    /// <summary>
    /// Creates the forbidden response returned when a configured document is modified through persistence APIs.
    /// </summary>
    /// <returns>The HTTP result to send to the client.</returns>
    private static IResult ReadOnlyConfiguredResult()
        => EndpointExtensions.Error(
            InteractiveReportErrorCodes.ConfiguredReportReadOnly,
            StatusCodes.Status403Forbidden);

    /// <summary>
    /// Validates a saved-report title and returns an error response when invalid.
    /// </summary>
    /// <param name="title">The optional title to validate after trimming.</param>
    /// <param name="code">The request-specific error code to use when invalid.</param>
    /// <returns><see langword="null"/> for a title of 1 to 200 characters; otherwise, a coded HTTP 400 result.</returns>
    private static IResult? TitleError(string? title, string code)
        => string.IsNullOrWhiteSpace(title) || title.Trim().Length > 200
            ? BadRequest(code)
            : null;

    private static string NormalizeTitle(string title)
        => title.Trim().ToUpperInvariant();

    /// <summary>
    /// Validates a client-authored saved-report candidate independently of live schema binding.
    /// </summary>
    /// <param name="definition">The candidate assembled from create or update input.</param>
    /// <param name="titleCode">The error code for a missing or invalid title.</param>
    /// <param name="stateCode">The error code for an explicitly changed but missing state.</param>
    /// <returns><see langword="null"/> when structurally valid; otherwise, the first coded HTTP 400 result.</returns>
    private static IResult? DefinitionError(
        InteractiveReportDefinition definition,
        string titleCode,
        string stateCode)
    {
        if (TitleError(definition.Title, titleCode) is { } titleError)
            return titleError;
        if (definition.Owner is not null && string.IsNullOrWhiteSpace(definition.Owner))
            return BadRequest(InteractiveReportErrorCodes.SavedReportOwnerInvalid);
        if (definition.StateChanged && definition.State is null)
            return BadRequest(stateCode);
        return null;
    }

    /// <summary>
    /// Creates a standardized validation-error response.
    /// </summary>
    /// <param name="code">The stable protocol or diagnostic code to return.</param>
    /// <param name="details">Optional caller-safe request details.</param>
    /// <returns>A JSON HTTP 400 result using the shared error catalog.</returns>
    private static IResult BadRequest(
        string code,
        string? details = null)
        => EndpointExtensions.Error(
            code,
            StatusCodes.Status400BadRequest,
            details);

    /// <summary>
    /// Builds a filesystem-neutral JSON download name from a report name and title.
    /// </summary>
    /// <param name="reportName">The canonical configured report name.</param>
    /// <param name="title">The saved-report display title.</param>
    /// <returns>A sanitized filename suitable for Content-Disposition.</returns>
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
