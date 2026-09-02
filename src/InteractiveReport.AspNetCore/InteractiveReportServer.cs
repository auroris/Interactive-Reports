using System.Text.Json;
using InteractiveReport.AspNetCore.Definitions;
using InteractiveReport.Core.Authorization;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Expressions;
using InteractiveReport.Core.Identity;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.SavedReports;
using InteractiveReport.Core.Validation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InteractiveReport.AspNetCore;

/// <summary>Classifies a server operation failure without assigning HTTP or GraphQL semantics.</summary>
public enum InteractiveReportFailureKind
{
    Invalid,
    Unauthenticated,
    Forbidden,
    NotFound,

    /// <summary>The request is well formed but collides with state that already exists.</summary>
    Conflict,
    Internal,
}

/// <summary>A stable failure returned to an Interactive Reports client adapter.</summary>
public sealed record InteractiveReportFailure(
    InteractiveReportFailureKind Kind,
    string Code,
    string? Details = null,
    string? TraceIdentifier = null,
    IReadOnlyDictionary<string, string[]>? Validation = null);

/// <summary>Contains either a successful server value or a transport-neutral failure.</summary>
public sealed record InteractiveReportServerResult<T>(
    T? Value,
    InteractiveReportFailure? Failure,
    bool Truncated = false)
{
    public static InteractiveReportServerResult<T> Success(T value, bool truncated = false)
        => new(value, null, truncated);

    public static InteractiveReportServerResult<T> Failed(InteractiveReportFailure failure)
        => new(default, failure);
}

/// <summary>
/// One report document in its source-controlled envelope form, together with the configuration it
/// belongs to. Clients package it for download; the report name is carried separately because the
/// envelope itself is what an operator drops into a definition's documentFiles.
/// </summary>
/// <param name="ReportName">The canonical configured report the document belongs to.</param>
/// <param name="Document">The envelope exactly as it should be written to a file.</param>
public sealed record InteractiveReportDocumentExport(string ReportName, ReportDocumentFile Document);

/// <summary>A loaded, authorized report document ready for a client to echo, mutate, and query.</summary>
/// <param name="ReportName">The canonical configured report the document belongs to.</param>
/// <param name="Metadata">The row as it exists after any reconciliation or auto-repair.</param>
/// <param name="State">
/// The document bound to the current state model, with schema caches refreshed for the origins that
/// require it. Every document is served through this one model, so what a client reads is what this
/// version of the engine can execute.
/// </param>
public sealed record InteractiveReportLoadedDocument(
    string ReportName,
    SavedReportMetadata Metadata,
    ReportState State);

/// <summary>
/// Application boundary used by JSON, GraphQL, and file clients. It resolves definitions,
/// authorization, saved documents, trusted context, and execution without exposing transport types.
/// </summary>
public interface IInteractiveReportServer
{
    Task<InteractiveReportServerResult<IReadOnlyList<ReportConfigurationSummary>>> ListConfigurations(
        InteractiveReportRequestContext context,
        CancellationToken ct = default);

    Task<InteractiveReportServerResult<IReadOnlyList<SavedReportSummary>>> ListSavedReports(
        string reportName,
        InteractiveReportRequestContext context,
        CancellationToken ct = default);

    Task<InteractiveReportServerResult<InteractiveReportLoadedDocument>> LoadDocument(
        long id,
        InteractiveReportRequestContext context,
        CancellationToken ct = default);

    /// <summary>
    /// Loads a document that must belong to <paramref name="reportName"/>. The report-level gate runs
    /// before the row is read, and a document from another family is hidden as not-found, so a
    /// caller cannot reach a document through a report route it does not belong to. Clients holding
    /// a report name should prefer this overload; the id-only form is for callers that address a
    /// document by identifier alone and must infer its report from the row.
    /// </summary>
    Task<InteractiveReportServerResult<InteractiveReportLoadedDocument>> LoadDocument(
        string reportName,
        long id,
        InteractiveReportRequestContext context,
        CancellationToken ct = default);

    Task<InteractiveReportServerResult<ReportResult>> Query(
        string reportName,
        ReportState state,
        InteractiveReportRequestContext context,
        CancellationToken ct = default);

    Task<InteractiveReportServerResult<ReportResult>> Query(
        InteractiveReportLoadedDocument document,
        InteractiveReportRequestContext context,
        CancellationToken ct = default);

    /// <summary>
    /// Queries a loaded document using a state the client has adjusted — paging, search, or sort
    /// applied on top of what was loaded. Authorization still uses the document's own metadata, so a
    /// client cannot widen its access by rewriting the state it submits.
    /// </summary>
    Task<InteractiveReportServerResult<ReportResult>> Query(
        InteractiveReportLoadedDocument document,
        ReportState state,
        InteractiveReportRequestContext context,
        CancellationToken ct = default);

    Task<InteractiveReportServerResult<ReportResult>> QueryForDownload(
        string reportName,
        ReportState state,
        InteractiveReportRequestContext context,
        CancellationToken ct = default);

    /// <summary>
    /// Discovers the authorized schema for a report: its columns, edit link, per-column overrides,
    /// synthetic default state, engine capabilities, effective features, and limits.
    /// </summary>
    Task<InteractiveReportServerResult<InteractiveReportSchema>> GetSchema(
        string reportName,
        InteractiveReportRequestContext context,
        CancellationToken ct = default);

    /// <summary>
    /// Resolves a bounded list of values for one column, through the same definition resolution and
    /// query authorization as the report table the values are being picked for.
    /// </summary>
    Task<InteractiveReportServerResult<ReportLovResult>> Lov(
        string reportName,
        ReportLovRequest request,
        InteractiveReportRequestContext context,
        CancellationToken ct = default);

    /// <summary>
    /// Creates a document in the family named by an existing document's id. The request body is read
    /// through <paramref name="readRequest"/> only after the report-level gate has passed, so an
    /// unauthorized caller never reaches the parse.
    /// </summary>
    Task<InteractiveReportServerResult<SavedReportSummary>> SaveDocument(
        long anchorId,
        Func<CancellationToken, Task<SaveReportRequest?>> readRequest,
        InteractiveReportRequestContext context,
        CancellationToken ct = default);

    /// <summary>
    /// Applies a partial update to a user-authored document addressed by its database id. Publication,
    /// default selection, and ownership changes each escalate to their own administrator decision.
    /// </summary>
    Task<InteractiveReportServerResult<SavedReportSummary>> UpdateDocument(
        long id,
        Func<CancellationToken, Task<UpdateSavedReportRequest?>> readRequest,
        InteractiveReportRequestContext context,
        CancellationToken ct = default);

    /// <summary>
    /// Exports one document as the source-controlled envelope an operator can place in a
    /// definition's documentFiles. Administrator-only, and hidden when denied.
    /// </summary>
    Task<InteractiveReportServerResult<InteractiveReportDocumentExport>> ExportDocument(
        long id,
        InteractiveReportRequestContext context,
        CancellationToken ct = default);

    /// <summary>
    /// Imports a source-controlled envelope into the family named by an existing document's id, as a
    /// private document owned by the importing administrator.
    /// </summary>
    Task<InteractiveReportServerResult<SavedReportSummary>> ImportDocument(
        long anchorId,
        Func<CancellationToken, Task<ReportDocumentFile?>> readRequest,
        InteractiveReportRequestContext context,
        CancellationToken ct = default);

    /// <summary>
    /// Describes the identity and administrator sources the engine sees for the caller. Opt-in: when
    /// the diagnostic is disabled this reports not-found rather than an empty answer.
    /// </summary>
    Task<InteractiveReportServerResult<InteractiveReportIdentity>> DescribeIdentity(
        InteractiveReportRequestContext context,
        CancellationToken ct = default);

    /// <summary>
    /// Lists the host-supplied identity choices offered for authorization administration.
    /// Administrator-only; a host with no user provider reports an empty list.
    /// </summary>
    Task<InteractiveReportServerResult<IReadOnlyList<InteractiveReportUser>>> ListAuthorizationUsers(
        InteractiveReportRequestContext context,
        CancellationToken ct = default);

    /// <summary>
    /// Lists configured and database-authored authorization state. Administrator-only.
    /// </summary>
    Task<InteractiveReportServerResult<InteractiveReportAuthorizationState>> ListAuthorizationState(
        InteractiveReportRequestContext context,
        CancellationToken ct = default);

    /// <summary>
    /// Grants database-authored administrator authority. The identity is read through
    /// <paramref name="readIdentity"/> only after administration has been authorized, so an
    /// unauthorized request is hidden whether or not the supplied value is well formed.
    /// </summary>
    Task<InteractiveReportServerResult<bool>> GrantAdministrator(
        Func<CancellationToken, Task<string?>> readIdentity,
        InteractiveReportRequestContext context,
        CancellationToken ct = default);

    /// <summary>Revokes database-authored administrator authority.</summary>
    Task<InteractiveReportServerResult<bool>> RevokeAdministrator(
        Func<CancellationToken, Task<string?>> readIdentity,
        InteractiveReportRequestContext context,
        CancellationToken ct = default);

    /// <summary>
    /// Enables or disables the database-authored restriction on one configured report. A report that
    /// allows anonymous access, or that only administrators may reach, cannot be restricted.
    /// </summary>
    Task<InteractiveReportServerResult<bool>> SetReportRestriction(
        string reportName,
        Func<CancellationToken, Task<bool?>> readRestricted,
        InteractiveReportRequestContext context,
        CancellationToken ct = default);

    /// <summary>Grants one identity database-authored access to one restricted report.</summary>
    Task<InteractiveReportServerResult<bool>> GrantReportUser(
        string reportName,
        Func<CancellationToken, Task<string?>> readIdentity,
        InteractiveReportRequestContext context,
        CancellationToken ct = default);

    /// <summary>Revokes one identity's database-authored access to one restricted report.</summary>
    Task<InteractiveReportServerResult<bool>> RevokeReportUser(
        string reportName,
        Func<CancellationToken, Task<string?>> readIdentity,
        InteractiveReportRequestContext context,
        CancellationToken ct = default);

    /// <summary>
    /// Deletes a user-authored document the caller may modify. Documents are addressed by their
    /// database id — the only stable handle a document has — and a configured document is refused
    /// because its declaring file, not the database, is authoritative.
    /// </summary>
    Task<InteractiveReportServerResult<bool>> DeleteDocument(
        long id,
        InteractiveReportRequestContext context,
        CancellationToken ct = default);
}

internal sealed class InteractiveReportServer(
    IReportAuthorizationService authorization,
    IReportAuthorizationStore authorizationStore,
    ISavedReportStore savedReports,
    ConfiguredReportDocumentSynchronizer synchronizer,
    ConfiguredReportDocumentStore configuredDocuments,
    DefaultReportDocumentService defaultDocuments,
    ReportExecutor executor,
    IOptionsMonitor<InteractiveReportOptions> options,
    InteractiveReportLogging logging) : IInteractiveReportServer
{
    public async Task<InteractiveReportServerResult<IReadOnlyList<ReportConfigurationSummary>>> ListConfigurations(
        InteractiveReportRequestContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        // A caller sees only the configurations they may view. An ordinary denial hides one entry;
        // an infrastructure failure stops the whole catalogue so a broken authorizer cannot
        // masquerade as an empty report list.
        var summaries = new List<ReportConfigurationSummary>();
        InteractiveReportFailure? firstDenial = null;
        foreach (var reportName in options.CurrentValue.Reports.Keys)
        {
            var resolved = await authorization.ResolveDefinition(reportName, context, ct);
            InteractiveReportFailure? denied;
            if (resolved.Failure is not null) denied = Failure(resolved.Failure);
            else if (resolved.Definition is null)
                denied = new(
                    InteractiveReportFailureKind.NotFound,
                    InteractiveReportErrorCodes.ReportNotFound);
            else
            {
                var failure = await AuthorizeActions(
                    resolved.Definition,
                    [InteractiveReportAction.ViewReport],
                    resource: null,
                    administratorRequired: false,
                    hideDenied: true,
                    denialDetail: null,
                    context,
                    ct);
                denied = failure is null ? null : Failure(failure);
            }

            if (denied is not null)
            {
                firstDenial ??= denied;
                if (denied.Kind == InteractiveReportFailureKind.Internal)
                    return InteractiveReportServerResult<IReadOnlyList<ReportConfigurationSummary>>
                        .Failed(denied);
                continue;
            }

            var definition = resolved.Definition!;
            summaries.Add(new ReportConfigurationSummary(
                definition.Name,
                definition.Title ?? ColumnModel.Prettify(definition.Name)));
        }

        // An anonymous caller on an all-authenticated catalogue is told to sign in; an authenticated
        // caller who can see nothing gets the honest answer, an empty catalogue — every hidden entry
        // was already dropped one at a time, so the whole list is not a "report not found".
        if (summaries.Count == 0
            && firstDenial is { Kind: InteractiveReportFailureKind.Unauthenticated })
            return InteractiveReportServerResult<IReadOnlyList<ReportConfigurationSummary>>
                .Failed(firstDenial);
        return InteractiveReportServerResult<IReadOnlyList<ReportConfigurationSummary>>.Success(
            summaries.OrderBy(summary => summary.Title, StringComparer.OrdinalIgnoreCase).ToList());
    }

    public async Task<InteractiveReportServerResult<IReadOnlyList<SavedReportSummary>>> ListSavedReports(
        string reportName,
        InteractiveReportRequestContext context,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportName);
        ArgumentNullException.ThrowIfNull(context);

        var listing = SavedReportsListingDefinition.Matches(reportName);
        var resolved = await authorization.ResolveDefinition(reportName, context, ct);
        if (resolved.Failure is not null)
            return InteractiveReportServerResult<IReadOnlyList<SavedReportSummary>>.Failed(
                Failure(resolved.Failure));
        if (resolved.Definition is null) return NotFoundReport<IReadOnlyList<SavedReportSummary>>();
        var definition = resolved.Definition;

        var denied = await AuthorizeActions(
            definition,
            listing
                ? [InteractiveReportAction.ListAllSavedReports]
                : [InteractiveReportAction.ListSavedReports],
            resource: null,
            administratorRequired: listing,
            hideDenied: true,
            denialDetail: null,
            context,
            ct);
        if (denied is not null)
            return InteractiveReportServerResult<IReadOnlyList<SavedReportSummary>>.Failed(Failure(denied));

        // The store returns every public and private document for the configured family in one
        // query. Reconciliation consumes that complete truth before caller visibility is applied.
        List<SavedReport> family;
        try
        {
            family = (await synchronizer.ReconcileFamily(definition.Name, ct)).ToList();
            if (!family.Any(report => report.IsDefault))
            {
                var created = await defaultDocuments.CreateMissing(definition, family, ct);
                family.RemoveAll(report => report.Id == created.Id);
                family.Add(created);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ReportDocumentBootstrapException)
        {
            return NotFoundDocument<IReadOnlyList<SavedReportSummary>>();
        }
        catch (Exception ex)
        {
            return InteractiveReportServerResult<IReadOnlyList<SavedReportSummary>>.Failed(
                Internal(definition.Name, "saved-report storage", context, ex));
        }

        var identity = ReportIdentity.Resolve(context.User, options.CurrentValue.IdentityClaim);
        var administratorDenial = await authorization.AuthorizeEndpoint(
            [InteractiveReportAction.ListAllSavedReports],
            new InteractiveReportAuthorizationResource { ReportName = definition.Name },
            administratorRequired: true,
            hideDenied: true,
            denialDetail: null,
            context,
            ct);
        if (administratorDenial is { Kind: ReportAuthorizationFailureKind.Internal })
            return InteractiveReportServerResult<IReadOnlyList<SavedReportSummary>>.Failed(
                Failure(administratorDenial));
        var administrator = administratorDenial is null;

        // Listing visibility is the same read decision the single-document paths make, applied to
        // the complete unfiltered family that configured reconciliation just produced. It is
        // deliberately the policy call rather than a local predicate: a second copy of an
        // authorization rule is a copy that can drift out of step with the first.
        var visible = family
            .Where(report => SavedReportAccessPolicy.Read(report, identity, administrator)
                == SavedReportAccess.Allowed)
            .OrderByDescending(report => report.IsDefault)
            .ThenByDescending(report => report.IsGlobal)
            .ThenBy(report => report.Title, StringComparer.OrdinalIgnoreCase)
            .Select(report => SavedReportSummary.From(report.Metadata(), identity))
            .ToList();
        return InteractiveReportServerResult<IReadOnlyList<SavedReportSummary>>.Success(visible);
    }

    public Task<InteractiveReportServerResult<InteractiveReportLoadedDocument>> LoadDocument(
        long id,
        InteractiveReportRequestContext context,
        CancellationToken ct = default)
        => LoadDocumentCore(reportName: null, id, context, ct);

    public Task<InteractiveReportServerResult<InteractiveReportLoadedDocument>> LoadDocument(
        string reportName,
        long id,
        InteractiveReportRequestContext context,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportName);
        return LoadDocumentCore(reportName, id, context, ct);
    }

    private async Task<InteractiveReportServerResult<InteractiveReportLoadedDocument>> LoadDocumentCore(
        string? reportName,
        long id,
        InteractiveReportRequestContext context,
        CancellationToken ct)
    {
        // A named caller clears the report-level gate before the row is read. An id-only caller
        // cannot: the row is the only thing that names its report, so it is read first and the
        // gate runs against whatever family it turns out to belong to.
        ReportDefinition? definition = null;
        if (reportName is not null)
        {
            var named = await authorization.ResolveDefinition(reportName, context, ct);
            if (named.Failure is not null)
                return InteractiveReportServerResult<InteractiveReportLoadedDocument>.Failed(
                    Failure(named.Failure));
            if (named.Definition is null) return NotFoundReport<InteractiveReportLoadedDocument>();
            definition = named.Definition;
        }

        SavedReport? saved;
        try
        {
            saved = await savedReports.Get(id, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return InteractiveReportServerResult<InteractiveReportLoadedDocument>.Failed(
                Internal(id.ToString(), "saved-report retrieval", context, ex));
        }
        if (saved is null) return NotFoundDocument<InteractiveReportLoadedDocument>();

        if (definition is null)
        {
            var (family, hidden) = await ResolveRowFamily(saved.ReportName, context, ct);
            if (hidden is not null)
                return InteractiveReportServerResult<InteractiveReportLoadedDocument>.Failed(hidden);
            definition = family!;
        }
        else if (!string.Equals(saved.ReportName, definition.Name, StringComparison.Ordinal))
        {
            // The document exists but belongs to another family. Hiding it as not-found keeps a
            // report route from being a probe for documents outside it.
            return NotFoundDocument<InteractiveReportLoadedDocument>();
        }

        var metadata = saved.Metadata();
        var identity = ReportIdentity.Resolve(context.User, options.CurrentValue.IdentityClaim);
        var builtIn = SavedReportAccessPolicy.Read(metadata, identity, administrator: false);
        var denied = await AuthorizeActions(
            definition,
            [InteractiveReportAction.ReadSavedReport],
            new InteractiveReportAuthorizationResource
            {
                ReportName = definition.Name,
                SavedReport = metadata,
            },
            administratorRequired: builtIn != SavedReportAccess.Allowed,
            hideDenied: true,
            denialDetail: null,
            context,
            ct);
        if (denied is not null)
            return InteractiveReportServerResult<InteractiveReportLoadedDocument>.Failed(Failure(denied));

        try
        {
            var contextParameters = await authorization.ResolveContextParameters(definition, context, ct);
            ReportState state;
            if (saved.Origin == SavedReportOrigin.Configured)
            {
                ConfiguredReportDocument? file;
                try
                {
                    file = saved.SourceFile is null
                        ? null
                        : configuredDocuments.Find(saved.ReportName, saved.SourceFile);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    await synchronizer.RemoveInvalid(saved, ex, ct);
                    return NotFoundDocument<InteractiveReportLoadedDocument>();
                }

                if (file is null)
                {
                    await synchronizer.RemoveMissing(saved, ct);
                    try
                    {
                        await defaultDocuments.CreateMissing(definition, ct);
                    }
                    catch (ReportDocumentBootstrapException)
                    {
                        // The missing configured identity remains a hidden not-found result.
                    }
                    return NotFoundDocument<InteractiveReportLoadedDocument>();
                }

                try
                {
                    state = await executor.RefreshSchemaCaches(
                        definition, file.State, contextParameters, ct);
                }
                catch (ReportValidationException ex)
                {
                    await synchronizer.RemoveInvalid(saved, ex, ct);
                    return NotFoundDocument<InteractiveReportLoadedDocument>();
                }
            }
            else if (saved.IsDefault)
            {
                SavedReport current;
                (current, state) = await defaultDocuments.LoadState(
                    saved, definition, executor, contextParameters, ct);
                // Auto-repair rewrites the publication flags, and a lost repair race re-reads
                // whatever another writer committed. Authorization above deliberately judged the
                // row as it was read, but the returned document — which clients hand back as the
                // authorization resource for the follow-up query — must describe the row that
                // now exists.
                metadata = current.Metadata();
            }
            else
            {
                state = JsonSerializer.Deserialize<ReportState>(
                        saved.StateJson ?? throw new JsonException("The report document has no state."),
                        IrJson.Options)
                    ?? throw new JsonException("The report document has no state.");
            }

            return InteractiveReportServerResult<InteractiveReportLoadedDocument>.Success(
                new InteractiveReportLoadedDocument(definition.Name, metadata, state));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ReportDocumentBootstrapException)
        {
            return NotFoundDocument<InteractiveReportLoadedDocument>();
        }
        catch (Exception ex)
        {
            return InteractiveReportServerResult<InteractiveReportLoadedDocument>.Failed(
                Internal(saved.ReportName, "report document retrieval", context, ex));
        }
    }

    public Task<InteractiveReportServerResult<ReportResult>> Query(
        string reportName,
        ReportState state,
        InteractiveReportRequestContext context,
        CancellationToken ct = default)
        => Query(reportName, state, savedReport: null, InteractiveReportAction.Query, requireDownload: false, context, ct);

    public Task<InteractiveReportServerResult<ReportResult>> Query(
        InteractiveReportLoadedDocument document,
        InteractiveReportRequestContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Query(document, document.State, context, ct);
    }

    public Task<InteractiveReportServerResult<ReportResult>> Query(
        InteractiveReportLoadedDocument document,
        ReportState state,
        InteractiveReportRequestContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Query(
            document.ReportName,
            state,
            document.Metadata,
            InteractiveReportAction.Query,
            requireDownload: false,
            context,
            ct);
    }

    public Task<InteractiveReportServerResult<ReportResult>> QueryForDownload(
        string reportName,
        ReportState state,
        InteractiveReportRequestContext context,
        CancellationToken ct = default)
        => Query(reportName, state, savedReport: null, InteractiveReportAction.Export, requireDownload: true, context, ct);

    public async Task<InteractiveReportServerResult<InteractiveReportSchema>> GetSchema(
        string reportName,
        InteractiveReportRequestContext context,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportName);
        ArgumentNullException.ThrowIfNull(context);

        var authorized = await AuthorizeQuery(
            reportName, InteractiveReportAction.ViewReport, savedReport: null, context, ct);
        if (authorized.Failure is not null)
            return InteractiveReportServerResult<InteractiveReportSchema>.Failed(authorized.Failure);
        var definition = authorized.Definition!;

        try
        {
            var contextParameters = await authorization.ResolveContextParameters(definition, context, ct);
            var columns = await executor.GetSchema(definition, contextParameters, ct);

            return InteractiveReportServerResult<InteractiveReportSchema>.Success(new InteractiveReportSchema(
                Name: definition.Name,
                Title: definition.Title ?? ColumnModel.Prettify(definition.Name),
                Columns: columns.Select(c => new ColumnInfo(c.Name, c.Label, c.KindName, c.IsComputed)).ToArray(),
                EditLink: ResolveEditLink(definition, columns),
                CreateLink: ResolveCreateLink(definition),
                ColumnOverrides: ResolveColumnOverrides(definition, columns),
                DefaultState: ReportDocumentDefaults.Create(definition),
                Capabilities: new InteractiveReportCapabilities(
                    ExpressionLanguageCatalog.Functions,
                    AggregateCatalog.FunctionsByColumnType,
                    AggregateCatalog.ChartFunctionsByColumnType),
                // The resolved effective set, in canonical casing and order, so no client needs its
                // own copy of the catalog to interpret it.
                Features: ReportFeatures.Resolve(definition),
                Limits: new InteractiveReportLimits(
                    definition.DefaultPageSize,
                    definition.MaxPageSize,
                    definition.MaxRows,
                    definition.MaxChartPoints),
                // A presentation hint, not a grant. Every mutation is still evaluated against its
                // concrete action and resource.
                Authorization: new InteractiveReportAuthorizationHint(
                    await authorization.MayRequestAdministration(context, ct))));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return InteractiveReportServerResult<InteractiveReportSchema>.Failed(
                Internal(definition.Name, "schema discovery", context, ex));
        }
    }

    /// <summary>
    /// Rewrites the configured edit-link template onto canonical column names. A template that cannot
    /// be parsed, or that names a column the live schema does not have, disables the edit column
    /// rather than failing the schema request.
    /// </summary>
    private InteractiveReportEditLink? ResolveEditLink(
        ReportDefinition definition,
        Core.Schema.ReportSchema schema)
    {
        if (definition.EditLink is not { } editLink) return null;

        var placeholders = EditLinkTemplate.Parse(editLink.UrlTemplate, out var error);
        var unknown = placeholders?.FirstOrDefault(name => !schema.TryGetValue(name, out _));
        if (placeholders is null || unknown is not null)
        {
            logging.Logger?.LogWarning(
                "Report {Report}: editLink.urlTemplate {Problem}; the edit column is disabled.",
                definition.Name,
                placeholders is null ? $"is invalid — {error}" : $"references unknown column '{unknown}'");
            return null;
        }

        return new InteractiveReportEditLink(
            UrlTemplate: EditLinkTemplate.Rewrite(
                editLink.UrlTemplate,
                name => schema.TryGetValue(name, out var col) ? col.Name : name),
            Label: string.IsNullOrWhiteSpace(editLink.Label) ? "Edit" : editLink.Label.Trim(),
            Target: ResolveLinkTarget(editLink.Target),
            Mode: ResolveLinkMode(editLink.Mode));
    }

    /// <summary>
    /// Normalizes the configured create button. It has no schema dependency: the URL is a constant
    /// (validated at load), so nothing here can disable it.
    /// </summary>
    private static InteractiveReportCreateLink? ResolveCreateLink(ReportDefinition definition)
    {
        if (definition.CreateLink is not { } createLink) return null;

        return new InteractiveReportCreateLink(
            Url: string.IsNullOrWhiteSpace(createLink.Url) ? null : createLink.Url.Trim(),
            Label: string.IsNullOrWhiteSpace(createLink.Label) ? "Create" : createLink.Label.Trim(),
            Target: ResolveLinkTarget(createLink.Target),
            Mode: ResolveLinkMode(createLink.Mode));
    }

    private static string ResolveLinkTarget(string? target)
        => string.Equals(target, "_blank", StringComparison.OrdinalIgnoreCase) ? "_blank" : "_self";

    private static string ResolveLinkMode(string? mode)
        => string.Equals(mode, "event", StringComparison.OrdinalIgnoreCase) ? "event" : "navigate";

    /// <summary>
    /// Resolves per-column behavior flags, filtered to live schema columns and keyed by canonical
    /// name. Labels are deliberately absent because they ride the synthetic fallback/document-label
    /// channel, so this map exists only when a column carries behavior a client must gate on.
    /// </summary>
    private static IReadOnlyDictionary<string, InteractiveReportColumnOptions>? ResolveColumnOverrides(
        ReportDefinition definition,
        Core.Schema.ReportSchema schema)
    {
        if (definition.Columns is not { Count: > 0 }) return null;

        var result = new Dictionary<string, InteractiveReportColumnOptions>();
        foreach (var (name, over) in definition.Columns)
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

    public async Task<InteractiveReportServerResult<ReportLovResult>> Lov(
        string reportName,
        ReportLovRequest request,
        InteractiveReportRequestContext context,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportName);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var authorized = await AuthorizeQuery(
            reportName, InteractiveReportAction.Query, savedReport: null, context, ct);
        if (authorized.Failure is not null)
            return InteractiveReportServerResult<ReportLovResult>.Failed(authorized.Failure);
        var definition = authorized.Definition!;

        try
        {
            var contextParameters = await authorization.ResolveContextParameters(definition, context, ct);
            return InteractiveReportServerResult<ReportLovResult>.Success(
                await executor.Lov(definition, request, contextParameters, ct));
        }
        catch (ReportValidationException ex)
        {
            return InteractiveReportServerResult<ReportLovResult>.Failed(Validation(ex));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return InteractiveReportServerResult<ReportLovResult>.Failed(
                Internal(definition.Name, "list of values", context, ex));
        }
    }

    public async Task<InteractiveReportServerResult<bool>> DeleteDocument(
        long id,
        InteractiveReportRequestContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        SavedReport? report;
        try
        {
            report = await savedReports.Get(id, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return InteractiveReportServerResult<bool>.Failed(
                Internal(id.ToString(), "saved-report retrieval", context, ex));
        }
        if (report is null) return NotFoundDocument<bool>();

        var (family, hidden) = await ResolveRowFamily(report.ReportName, context, ct);
        if (hidden is not null) return InteractiveReportServerResult<bool>.Failed(hidden);
        var resolvedDefinition = family!;

        var metadata = report.Metadata();
        var identity = ReportIdentity.Resolve(context.User, options.CurrentValue.IdentityClaim);
        var builtIn = SavedReportAccessPolicy.Modify(metadata, identity, administrator: false);
        var denied = await AuthorizeActions(
            resolvedDefinition,
            [InteractiveReportAction.DeleteSavedReport],
            new InteractiveReportAuthorizationResource
            {
                ReportName = resolvedDefinition.Name,
                SavedReport = metadata,
            },
            administratorRequired: report.Origin != SavedReportOrigin.Configured
                && builtIn != SavedReportAccess.Allowed,
            hideDenied: builtIn == SavedReportAccess.Hidden,
            denialDetail: "Deleting another owner's report requires authorization.",
            context,
            ct);
        if (denied is not null)
            return InteractiveReportServerResult<bool>.Failed(Failure(denied));

        if (report.Origin == SavedReportOrigin.Configured)
            return InteractiveReportServerResult<bool>.Failed(new(
                InteractiveReportFailureKind.Forbidden,
                InteractiveReportErrorCodes.ConfiguredReportReadOnly));

        try
        {
            // The compare-and-delete carries the snapshot that was authorized, so a row that
            // changed underneath the decision is reported as gone rather than deleted blindly.
            var deleted = await savedReports.Delete(report, ct);
            if (deleted)
            {
                logging.Logger?.LogInformation(
                    "Deleted saved report {Id} ('{Title}') for report '{Report}' (traceId {TraceId})",
                    id,
                    report.Title,
                    report.ReportName,
                    context.TraceIdentifier);
                return InteractiveReportServerResult<bool>.Success(true);
            }
            return NotFoundDocument<bool>();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return InteractiveReportServerResult<bool>.Failed(
                Internal(report.ReportName, "saved-report deletion", context, ex));
        }
    }

    public async Task<InteractiveReportServerResult<InteractiveReportIdentity>> DescribeIdentity(
        InteractiveReportRequestContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var current = options.CurrentValue;
        // The diagnostic is opt-in: when it is off the route must be indistinguishable from one
        // that was never mapped.
        if (!current.WhoamiEnabled)
            return InteractiveReportServerResult<InteractiveReportIdentity>.Failed(new(
                InteractiveReportFailureKind.NotFound,
                InteractiveReportErrorCodes.EndpointNotFound));

        var identity = ReportIdentity.Resolve(context.User, current.IdentityClaim);
        var database = new DatabaseAdministratorAccess(false, false);
        if (ReportConnectionRegistry.IsStoreConfigured(current.SavedReports))
        {
            try
            {
                database = await authorizationStore.GetAdministratorAccess(identity, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return InteractiveReportServerResult<InteractiveReportIdentity>.Failed(Internal(
                    SavedReportsListingDefinition.Name, "identity authorization lookup", context, ex));
            }
        }

        var configuredAdministrator = ReportIdentity.IsAdministrator(
            context.User, current.IdentityClaim, current.Administrators);
        return InteractiveReportServerResult<InteractiveReportIdentity>.Success(new InteractiveReportIdentity(
            Authenticated: context.User.Identity?.IsAuthenticated == true,
            // Expose the exact value an operator would place in InteractiveReport:Administrators.
            Identity: identity,
            IsAdministrator: configuredAdministrator || database.UserGranted,
            ConfiguredAdministrator: configuredAdministrator,
            DatabaseAdministrator: database.UserGranted,
            AdministratorListConfigured: current.Administrators.Count > 0 || database.Configured,
            ApplicationAuthorizationConfigured: context.RequestServices
                .GetServices<IInteractiveReportAuthorizer>()
                .Any(),
            Name: context.User.Identity?.Name,
            AuthenticationType: context.User.Identity?.AuthenticationType,
            Claims: context.User.Claims
                .Select(claim => new InteractiveReportClaim(claim.Type, claim.Value))
                .ToArray()));
    }

    public async Task<InteractiveReportServerResult<IReadOnlyList<InteractiveReportUser>>> ListAuthorizationUsers(
        InteractiveReportRequestContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (await AuthorizeAdministration(
                InteractiveReportAction.ListAuthorizationUsers,
                SavedReportsListingDefinition.Name,
                context,
                ct) is { } denied)
            return InteractiveReportServerResult<IReadOnlyList<InteractiveReportUser>>.Failed(denied);

        var provider = context.RequestServices.GetService<IInteractiveReportUserProvider>();
        if (provider is null)
            return InteractiveReportServerResult<IReadOnlyList<InteractiveReportUser>>.Success([]);

        try
        {
            var supplied = await provider.GetUsers(context.User, ct);
            if (supplied is null || supplied.Count == 0)
                return InteractiveReportServerResult<IReadOnlyList<InteractiveReportUser>>.Success([]);

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

            return InteractiveReportServerResult<IReadOnlyList<InteractiveReportUser>>.Success(users);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return InteractiveReportServerResult<IReadOnlyList<InteractiveReportUser>>.Failed(Internal(
                SavedReportsListingDefinition.Name, "administration user lookup", context, ex));
        }
    }

    public async Task<InteractiveReportServerResult<InteractiveReportAuthorizationState>> ListAuthorizationState(
        InteractiveReportRequestContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (await AuthorizeAdministration(InteractiveReportAction.ManageAuthorization, SavedReportsListingDefinition.Name, context, ct) is { } denied)
            return InteractiveReportServerResult<InteractiveReportAuthorizationState>.Failed(denied);

        IReadOnlyList<ReportAuthorizationEntry> entries;
        try
        {
            entries = await authorizationStore.ListAll(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return InteractiveReportServerResult<InteractiveReportAuthorizationState>.Failed(
                Internal(SavedReportsListingDefinition.Name, "authorization listing", context, ex));
        }

        var current = options.CurrentValue;
        var databaseAdministrators = entries
            .Where(entry => entry.Kind == ReportAuthorizationEntryKind.Administrator)
            .Select(entry => entry.Identity!)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var databaseRestrictions = entries
            .Where(entry => entry.Kind == ReportAuthorizationEntryKind.ReportRestriction)
            .Select(entry => entry.ReportName!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var databaseUsers = entries
            .Where(entry => entry.Kind == ReportAuthorizationEntryKind.ReportUser)
            .GroupBy(entry => entry.ReportName!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(entry => entry.Identity!)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);

        var reports = current.Reports.Select(pair =>
        {
            var authorization = pair.Value.Authorization;
            var databaseRestricted = databaseRestrictions.Contains(pair.Key);
            return new InteractiveReportAuthorizationReport(
                Name: pair.Key,
                Title: pair.Value.Title ?? ColumnModel.Prettify(pair.Key),
                Restricted: authorization?.Restricted == true || databaseRestricted,
                ConfiguredRestricted: authorization?.Restricted == true,
                DatabaseRestricted: databaseRestricted,
                CanRestrict: authorization?.AllowAnonymous != true
                    && authorization?.AdministratorsOnly != true,
                ConfiguredUsers: authorization?.Users?.Select(identity => identity.Trim())
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray() ?? [],
                DatabaseUsers: databaseUsers.GetValueOrDefault(pair.Key) ?? []);
        }).OrderBy(report => report.Title, StringComparer.OrdinalIgnoreCase).ToArray();

        return InteractiveReportServerResult<InteractiveReportAuthorizationState>.Success(
            new InteractiveReportAuthorizationState(
                ConfiguredAdministrators: current.Administrators
                    .Select(identity => identity.Trim())
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                DatabaseAdministrators: databaseAdministrators,
                Reports: reports));
    }

    public Task<InteractiveReportServerResult<bool>> GrantAdministrator(
        Func<CancellationToken, Task<string?>> readIdentity,
        InteractiveReportRequestContext context,
        CancellationToken ct = default)
        => MutateIdentity(
            SavedReportsListingDefinition.Name,
            readIdentity,
            (identity, token) => authorizationStore.GrantAdministrator(identity, token),
            "administrator authorization update",
            context,
            ct);

    public Task<InteractiveReportServerResult<bool>> RevokeAdministrator(
        Func<CancellationToken, Task<string?>> readIdentity,
        InteractiveReportRequestContext context,
        CancellationToken ct = default)
        => MutateIdentity(
            SavedReportsListingDefinition.Name,
            readIdentity,
            async (identity, token) => await authorizationStore.RevokeAdministrator(identity, token),
            "administrator authorization update",
            context,
            ct);

    public async Task<InteractiveReportServerResult<bool>> SetReportRestriction(
        string reportName,
        Func<CancellationToken, Task<bool?>> readRestricted,
        InteractiveReportRequestContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(readRestricted);
        ArgumentNullException.ThrowIfNull(context);

        var (canonicalName, report) = FindConfiguredReport(reportName);
        if (canonicalName is null || report is null) return NotFoundReport<bool>();
        if (await AuthorizeAdministration(InteractiveReportAction.ManageAuthorization, canonicalName, context, ct) is { } denied)
            return InteractiveReportServerResult<bool>.Failed(denied);

        bool? restricted;
        try
        {
            restricted = await readRestricted(ct);
        }
        catch (JsonException ex)
        {
            return InteractiveReportServerResult<bool>.Failed(
                Invalid(InteractiveReportErrorCodes.MalformedAuthorizationRequest, ex.Message));
        }
        if (restricted is null)
            return InteractiveReportServerResult<bool>.Failed(
                Invalid(InteractiveReportErrorCodes.AuthorizationRestrictionRequired));
        // A report nobody has to authenticate for, or one only administrators may reach, has no
        // per-user surface for a restriction to govern.
        if (restricted == true
            && (report.Authorization?.AllowAnonymous == true
                || report.Authorization?.AdministratorsOnly == true))
            return InteractiveReportServerResult<bool>.Failed(
                Invalid(InteractiveReportErrorCodes.ReportRestrictionConflict));

        try
        {
            await authorizationStore.SetReportRestricted(canonicalName, restricted.Value, ct);
            logging.Logger?.LogInformation(
                "Set report restriction for report '{Report}' to {Restricted} (traceId {TraceId})",
                canonicalName,
                restricted.Value,
                context.TraceIdentifier);
            return InteractiveReportServerResult<bool>.Success(true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return InteractiveReportServerResult<bool>.Failed(
                Internal(canonicalName, "report restriction update", context, ex));
        }
    }

    public Task<InteractiveReportServerResult<bool>> GrantReportUser(
        string reportName,
        Func<CancellationToken, Task<string?>> readIdentity,
        InteractiveReportRequestContext context,
        CancellationToken ct = default)
        => MutateReportIdentity(
            reportName,
            readIdentity,
            (canonical, identity, token) => authorizationStore.GrantReportUser(canonical, identity, token),
            "report user grant",
            context,
            ct);

    public Task<InteractiveReportServerResult<bool>> RevokeReportUser(
        string reportName,
        Func<CancellationToken, Task<string?>> readIdentity,
        InteractiveReportRequestContext context,
        CancellationToken ct = default)
        => MutateReportIdentity(
            reportName,
            readIdentity,
            async (canonical, identity, token) =>
                await authorizationStore.RevokeReportUser(canonical, identity, token),
            "report user revoke",
            context,
            ct);

    /// <summary>
    /// Runs an identity mutation that is scoped to the whole installation rather than one report.
    /// The caller clears administration before the identity is read, so an unauthorized request is
    /// hidden whether or not its body is well formed.
    /// </summary>
    private async Task<InteractiveReportServerResult<bool>> MutateIdentity(
        string resourceReportName,
        Func<CancellationToken, Task<string?>> readIdentity,
        Func<string, CancellationToken, Task> mutation,
        string operation,
        InteractiveReportRequestContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(readIdentity);
        ArgumentNullException.ThrowIfNull(context);

        if (await AuthorizeAdministration(InteractiveReportAction.ManageAuthorization, resourceReportName, context, ct) is { } denied)
            return InteractiveReportServerResult<bool>.Failed(denied);

        var (identity, failure) = await ReadIdentity(readIdentity, ct);
        if (failure is not null) return InteractiveReportServerResult<bool>.Failed(failure);

        try
        {
            await mutation(identity!, ct);
            logging.Logger?.LogInformation(
                "Administrative authorization mutation '{Operation}' succeeded for identity '{Identity}' (traceId {TraceId})",
                operation,
                identity,
                context.TraceIdentifier);
            return InteractiveReportServerResult<bool>.Success(true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return InteractiveReportServerResult<bool>.Failed(
                Internal(resourceReportName, operation, context, ex));
        }
    }

    /// <summary>Runs an identity mutation scoped to one configured report that supports user grants.</summary>
    private async Task<InteractiveReportServerResult<bool>> MutateReportIdentity(
        string reportName,
        Func<CancellationToken, Task<string?>> readIdentity,
        Func<string, string, CancellationToken, Task> mutation,
        string operation,
        InteractiveReportRequestContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(readIdentity);
        ArgumentNullException.ThrowIfNull(context);

        var (canonicalName, report) = FindConfiguredReport(reportName);
        if (canonicalName is null || report is null) return NotFoundReport<bool>();
        if (await AuthorizeAdministration(InteractiveReportAction.ManageAuthorization, canonicalName, context, ct) is { } denied)
            return InteractiveReportServerResult<bool>.Failed(denied);
        if (report.Authorization?.AllowAnonymous == true
            || report.Authorization?.AdministratorsOnly == true)
            return InteractiveReportServerResult<bool>.Failed(
                Invalid(InteractiveReportErrorCodes.ReportUserGrantConflict));

        var (identity, failure) = await ReadIdentity(readIdentity, ct);
        if (failure is not null) return InteractiveReportServerResult<bool>.Failed(failure);

        try
        {
            await mutation(canonicalName, identity!, ct);
            logging.Logger?.LogInformation(
                "Updated user grant on report '{Report}' for identity '{Identity}' ({Operation}, traceId {TraceId})",
                canonicalName,
                identity,
                operation,
                context.TraceIdentifier);
            return InteractiveReportServerResult<bool>.Success(true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return InteractiveReportServerResult<bool>.Failed(
                Internal(canonicalName, "report user authorization update", context, ex));
        }
    }

    /// <summary>Reads and validates a supplied identity; a valid one is 1 to 400 characters trimmed.</summary>
    private static async Task<(string? Identity, InteractiveReportFailure? Failure)> ReadIdentity(
        Func<CancellationToken, Task<string?>> readIdentity,
        CancellationToken ct)
    {
        string? supplied;
        try
        {
            supplied = await readIdentity(ct);
        }
        catch (JsonException ex)
        {
            return (null, Invalid(InteractiveReportErrorCodes.MalformedAuthorizationRequest, ex.Message));
        }

        var identity = supplied?.Trim();
        return string.IsNullOrEmpty(identity) || identity.Length > 400
            ? (null, Invalid(InteractiveReportErrorCodes.AuthorizationIdentityInvalid))
            : (identity, null);
    }

    /// <summary>Requires administrator authority for an administration operation, hiding denials.</summary>
    private async Task<InteractiveReportFailure?> AuthorizeAdministration(
        InteractiveReportAction action,
        string resourceReportName,
        InteractiveReportRequestContext context,
        CancellationToken ct)
    {
        var denied = await authorization.AuthorizeEndpoint(
            [action],
            new InteractiveReportAuthorizationResource { ReportName = resourceReportName },
            administratorRequired: true,
            hideDenied: true,
            denialDetail: null,
            context,
            ct);
        return denied is null ? null : Failure(denied);
    }

    /// <summary>
    /// Finds a configured report case-insensitively while preserving its canonical configured name —
    /// the only stable identifier a report has, and the spelling every stored row was written from.
    /// </summary>
    private (string? Name, ReportDefinition? Report) FindConfiguredReport(string name)
    {
        var reports = options.CurrentValue.Reports;
        if (string.IsNullOrWhiteSpace(name) || !reports.TryGetValue(name, out var report))
            return (null, null);
        var canonicalName = reports.Keys.First(key =>
            string.Equals(key, name, StringComparison.OrdinalIgnoreCase));
        return (canonicalName, report);
    }

    public async Task<InteractiveReportServerResult<InteractiveReportDocumentExport>> ExportDocument(
        long id,
        InteractiveReportRequestContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (ReportIdentity.Resolve(context.User, options.CurrentValue.IdentityClaim) is null)
            return InteractiveReportServerResult<InteractiveReportDocumentExport>.Failed(new(
                InteractiveReportFailureKind.Unauthenticated,
                InteractiveReportErrorCodes.AuthenticationRequired));

        SavedReport? report;
        try
        {
            report = await savedReports.Get(id, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return InteractiveReportServerResult<InteractiveReportDocumentExport>.Failed(
                Internal(id.ToString(), "saved-report retrieval", context, ex));
        }
        if (report is null) return NotFoundDocument<InteractiveReportDocumentExport>();

        var metadata = report.Metadata();
        var (family, hidden) = await ResolveRowFamily(metadata.ReportName, context, ct);
        if (hidden is not null)
            return InteractiveReportServerResult<InteractiveReportDocumentExport>.Failed(hidden);
        var definition = family!;

        var denied = await AuthorizeActions(
            definition,
            [InteractiveReportAction.DownloadReportDocument],
            new InteractiveReportAuthorizationResource
            {
                ReportName = definition.Name,
                SavedReport = metadata,
            },
            administratorRequired: true,
            hideDenied: true,
            denialDetail: null,
            context,
            ct);
        if (denied is not null)
            return InteractiveReportServerResult<InteractiveReportDocumentExport>.Failed(Failure(denied));

        ReportState? state;
        try
        {
            if (report.Origin == SavedReportOrigin.Configured)
            {
                // The export is the source-controlled envelope, so a configured document is taken
                // from its declaring file as authored — schema caches are deliberately not refreshed.
                // A present file that no longer parses is handled exactly as a load handles it: the
                // optimistic identity is removed and the id reads as not found, not as a server error.
                try
                {
                    state = report.SourceFile is null
                        ? null
                        : configuredDocuments.Find(report.ReportName, report.SourceFile)?.State;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    await synchronizer.RemoveInvalid(report, ex, ct);
                    return NotFoundDocument<InteractiveReportDocumentExport>();
                }
                if (state is null)
                {
                    await synchronizer.RemoveMissing(report, ct);
                    try
                    {
                        await defaultDocuments.CreateMissing(definition, ct);
                    }
                    catch (ReportDocumentBootstrapException)
                    {
                        // The missing configured identity stays a hidden not-found result.
                    }
                    return NotFoundDocument<InteractiveReportDocumentExport>();
                }
            }
            else if (report.IsDefault)
            {
                var contextParameters = await authorization.ResolveContextParameters(definition, context, ct);
                (_, state) = await defaultDocuments.LoadState(
                    report, definition, executor, contextParameters, ct);
            }
            else
            {
                state = JsonSerializer.Deserialize<ReportState>(
                    report.StateJson ?? throw new JsonException("The report document has no state."),
                    IrJson.Options);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ReportDocumentBootstrapException)
        {
            return NotFoundDocument<InteractiveReportDocumentExport>();
        }
        catch (Exception ex)
        {
            return InteractiveReportServerResult<InteractiveReportDocumentExport>.Failed(
                Internal(metadata.ReportName, "report document download", context, ex));
        }

        if (state is null)
            return InteractiveReportServerResult<InteractiveReportDocumentExport>.Failed(
                Internal(
                    metadata.ReportName,
                    "report document download",
                    context,
                    new InvalidOperationException($"Saved report '{id}' has no state document.")));

        logging.Logger?.LogInformation(
            "Exported saved report {Id} ('{Title}') for report '{Report}' (traceId {TraceId})",
            id,
            report.Title,
            metadata.ReportName,
            context.TraceIdentifier);

        return InteractiveReportServerResult<InteractiveReportDocumentExport>.Success(
            new InteractiveReportDocumentExport(
                metadata.ReportName,
                new ReportDocumentFile
                {
                    Title = report.Title,
                    Default = report.IsDefault,
                    State = state,
                }));
    }

    public Task<InteractiveReportServerResult<SavedReportSummary>> SaveDocument(
        long anchorId,
        Func<CancellationToken, Task<SaveReportRequest?>> readRequest,
        InteractiveReportRequestContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(readRequest);
        return CreateDocument(
            anchorId,
            new DocumentCreation(
                [InteractiveReportAction.ReadSavedReport, InteractiveReportAction.CreateSavedReport],
                RequireSavedReportsFeature: true,
                AlwaysAdministrator: false,
                DenialDetail: "Publishing a global report requires authorization.",
                MalformedCode: InteractiveReportErrorCodes.MalformedSaveRequest,
                TitleCode: InteractiveReportErrorCodes.SavedReportTitleInvalid,
                StateCode: InteractiveReportErrorCodes.SavedReportStateRequired,
                Operation: "saved report creation"),
            async token =>
            {
                var request = await readRequest(token);
                return (request?.Title, request?.State, request?.IsGlobal ?? false);
            },
            context,
            ct);
    }

    public Task<InteractiveReportServerResult<SavedReportSummary>> ImportDocument(
        long anchorId,
        Func<CancellationToken, Task<ReportDocumentFile?>> readRequest,
        InteractiveReportRequestContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(readRequest);
        // An import deliberately bypasses the end-user saved-reports feature flag, but not report
        // authorization or document validation. File publication metadata is ignored: the copy lands
        // private and editable, and may be published later through an ordinary update.
        return CreateDocument(
            anchorId,
            new DocumentCreation(
                [InteractiveReportAction.ReadSavedReport, InteractiveReportAction.UploadReportDocument],
                RequireSavedReportsFeature: false,
                AlwaysAdministrator: true,
                DenialDetail: null,
                MalformedCode: InteractiveReportErrorCodes.MalformedReportDocument,
                TitleCode: InteractiveReportErrorCodes.ReportDocumentTitleInvalid,
                StateCode: InteractiveReportErrorCodes.ReportDocumentStateRequired,
                Operation: "report document upload"),
            async token =>
            {
                var document = await readRequest(token);
                return (document?.Title, document?.State, false);
            },
            context,
            ct);
    }

    /// <summary>Describes how one kind of document creation authorizes, parses, and reports failures.</summary>
    private sealed record DocumentCreation(
        IReadOnlyCollection<InteractiveReportAction> Actions,
        bool RequireSavedReportsFeature,
        bool AlwaysAdministrator,
        string? DenialDetail,
        string MalformedCode,
        string TitleCode,
        string StateCode,
        string Operation);

    private async Task<InteractiveReportServerResult<SavedReportSummary>> CreateDocument(
        long anchorId,
        DocumentCreation shape,
        Func<CancellationToken, Task<(string? Title, ReportState? State, bool IsGlobal)>> readRequest,
        InteractiveReportRequestContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var identity = ReportIdentity.Resolve(context.User, options.CurrentValue.IdentityClaim);
        if (identity is null)
            return InteractiveReportServerResult<SavedReportSummary>.Failed(new(
                InteractiveReportFailureKind.Unauthenticated,
                InteractiveReportErrorCodes.AuthenticationRequired));

        // The anchor names the family the new document joins. Only its id is a stable handle, so
        // the family is taken from the row rather than from anything the caller supplies.
        SavedReportMetadata? anchor;
        try
        {
            anchor = await savedReports.GetMetadata(anchorId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return InteractiveReportServerResult<SavedReportSummary>.Failed(
                Internal(anchorId.ToString(), "saved-report retrieval", context, ex));
        }
        if (anchor is null) return NotFoundDocument<SavedReportSummary>();

        var (family, hidden) = await ResolveRowFamily(anchor.ReportName, context, ct);
        if (hidden is not null) return InteractiveReportServerResult<SavedReportSummary>.Failed(hidden);
        var definition = family!;

        // Enforce the saved-reports feature at creation only. Existing rows stay governed by the
        // ownership matrix, so a config change never strands them.
        if (shape.RequireSavedReportsFeature
            && authorization.CheckFeature(definition, ReportFeatures.SavedReports) is { } disabled)
            return InteractiveReportServerResult<SavedReportSummary>.Failed(Failure(disabled));

        (string? Title, ReportState? State, bool IsGlobal) request;
        try
        {
            request = await readRequest(ct);
        }
        catch (JsonException ex)
        {
            return InteractiveReportServerResult<SavedReportSummary>.Failed(
                Invalid(shape.MalformedCode, ex.Message));
        }

        if (TitleFailure(request.Title, shape.TitleCode) is { } titleFailure)
            return InteractiveReportServerResult<SavedReportSummary>.Failed(titleFailure);
        if (request.State is null)
            return InteractiveReportServerResult<SavedReportSummary>.Failed(Invalid(shape.StateCode));

        var candidate = new SavedReportCandidate
        {
            Id = 0,
            ReportName = definition.Name,
            Title = request.Title!.Trim(),
            Public = request.IsGlobal,
            Default = false,
            Owner = identity,
            State = request.State,
        };

        var builtIn = SavedReportAccessPolicy.Read(anchor, identity, administrator: false);
        var denied = await AuthorizeDocumentMutation(
            definition,
            shape.Actions,
            new InteractiveReportAuthorizationResource
            {
                ReportName = definition.Name,
                Candidate = candidate,
            },
            administratorRequired: shape.AlwaysAdministrator || builtIn != SavedReportAccess.Allowed,
            hideDenied: shape.AlwaysAdministrator || builtIn != SavedReportAccess.Allowed,
            denialDetail: shape.DenialDetail,
            () => RequiredAdministratorActions(candidate, current: null, identity),
            context,
            ct);
        if (denied is not null)
            return InteractiveReportServerResult<SavedReportSummary>.Failed(denied);

        if (DefinitionFailure(candidate, shape.TitleCode, shape.StateCode) is { } candidateFailure)
            return InteractiveReportServerResult<SavedReportSummary>.Failed(candidateFailure);
        if (await ValidateSubmittedState(definition, candidate, shape.Operation, context, ct) is { } stateFailure)
            return InteractiveReportServerResult<SavedReportSummary>.Failed(stateFailure);

        // An authorizer may have reassigned the owner, promoted the save to public, or selected it
        // as the family default; the candidate it left behind is what is validated and persisted,
        // so the collision scope and the stored row follow the candidate, not the caller.
        var effectiveOwner = candidate.Owner?.Trim();
        if (candidate.Default && !candidate.Public)
            return InteractiveReportServerResult<SavedReportSummary>.Failed(
                Invalid(InteractiveReportErrorCodes.DefaultReportCannotBeUnset));
        var candidateIsPublic = candidate.Public || candidate.Default;
        if (await savedReports.FindTitleCollision(
                definition.Name, candidate.Title, effectiveOwner, candidateIsPublic, exceptId: null, ct) is { } collision)
            return InteractiveReportServerResult<SavedReportSummary>.Failed(
                TitleConflict(collision, candidate.Title));

        var report = new SavedReport
        {
            Id = 0,
            ReportName = definition.Name,
            Title = candidate.Title.Trim(),
            Owner = effectiveOwner,
            IsGlobal = candidateIsPublic,
            StateJson = JsonSerializer.Serialize(candidate.State, IrJson.Options),
        };
        try
        {
            SavedReport? currentDefault = null;
            if (candidate.Default)
            {
                currentDefault = await savedReports.FindDefault(definition.Name, ct)
                    ?? await defaultDocuments.CreateMissing(definition, ct);
                if (currentDefault.Origin == SavedReportOrigin.Configured)
                    return InteractiveReportServerResult<SavedReportSummary>.Failed(new(
                        InteractiveReportFailureKind.Conflict,
                        InteractiveReportErrorCodes.ConfiguredDefaultControlled));
            }

            await savedReports.Create(report, ct);

            if (currentDefault is not null)
            {
                var promoted = report with { IsDefault = true };
                if (await savedReports.ReplaceDefault(promoted, report, currentDefault, ct))
                    report = promoted;
                else
                    logging.Logger?.LogWarning(
                        "Report {Report}: saved report {Id} was created but a concurrent default change prevented its default selection (traceId {TraceId})",
                        definition.Name,
                        report.Id,
                        context.TraceIdentifier);
            }
        }
        catch (SavedReportTitleConflictException conflict)
        {
            return InteractiveReportServerResult<SavedReportSummary>.Failed(
                await TitleConflictFromStore(conflict, effectiveOwner, candidateIsPublic, exceptId: null, ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ReportDocumentBootstrapException)
        {
            return NotFoundDocument<SavedReportSummary>();
        }
        catch (Exception ex)
        {
            return InteractiveReportServerResult<SavedReportSummary>.Failed(
                Internal(definition.Name, shape.Operation, context, ex));
        }

        logging.Logger?.LogInformation(
            "Created saved report {Id} ('{Title}') for report '{Report}' by '{Owner}' (IsGlobal: {IsGlobal}, Operation: '{Operation}', traceId {TraceId})",
            report.Id,
            report.Title,
            definition.Name,
            effectiveOwner ?? "anonymous",
            candidateIsPublic,
            shape.Operation,
            context.TraceIdentifier);

        return InteractiveReportServerResult<SavedReportSummary>.Success(
            SavedReportSummary.From(report.Metadata(), identity));
    }

    public async Task<InteractiveReportServerResult<SavedReportSummary>> UpdateDocument(
        long id,
        Func<CancellationToken, Task<UpdateSavedReportRequest?>> readRequest,
        InteractiveReportRequestContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(readRequest);
        ArgumentNullException.ThrowIfNull(context);

        SavedReport? current;
        try
        {
            current = await savedReports.Get(id, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return InteractiveReportServerResult<SavedReportSummary>.Failed(
                Internal(id.ToString(), "saved-report retrieval", context, ex));
        }
        if (current is null) return NotFoundDocument<SavedReportSummary>();
        var metadata = current.Metadata();

        var identity = ReportIdentity.Resolve(context.User, options.CurrentValue.IdentityClaim);
        var (family, hidden) = await ResolveRowFamily(metadata.ReportName, context, ct);
        if (hidden is not null) return InteractiveReportServerResult<SavedReportSummary>.Failed(hidden);
        var definition = family!;

        UpdateSavedReportRequest? request;
        try
        {
            request = await readRequest(ct) ?? throw new JsonException("empty body");
        }
        catch (JsonException ex)
        {
            return InteractiveReportServerResult<SavedReportSummary>.Failed(
                Invalid(InteractiveReportErrorCodes.MalformedUpdateRequest, ex.Message));
        }

        var candidate = new SavedReportCandidate
        {
            Id = metadata.Id,
            ReportName = definition.Name,
            Title = request.Title ?? metadata.Title,
            Public = request.IsGlobal ?? metadata.IsGlobal,
            Default = request.IsDefault ?? metadata.IsDefault,
            Owner = request.Owner ?? metadata.Owner,
        };
        if (candidate.Default && !metadata.IsDefault) candidate.Public = true;
        if (request.State is not null) candidate.State = request.State;

        var builtIn = SavedReportAccessPolicy.Modify(metadata, identity, administrator: false);
        var denied = await AuthorizeDocumentMutation(
            definition,
            [InteractiveReportAction.UpdateSavedReport],
            new InteractiveReportAuthorizationResource
            {
                ReportName = definition.Name,
                SavedReport = metadata,
                Candidate = candidate,
            },
            administratorRequired: metadata.Origin == SavedReportOrigin.Configured
                || builtIn != SavedReportAccess.Allowed,
            hideDenied: metadata.Origin != SavedReportOrigin.Configured
                && builtIn == SavedReportAccess.Hidden,
            denialDetail: metadata.Origin == SavedReportOrigin.Configured
                ? "Changing a configured report requires authorization."
                : "Modifying publication or ownership requires authorization.",
            () => RequiredAdministratorActions(candidate, metadata, identity),
            context,
            ct);
        if (denied is not null)
            return InteractiveReportServerResult<SavedReportSummary>.Failed(denied);

        var report = current with { };

        if (metadata.Origin == SavedReportOrigin.Configured)
            return InteractiveReportServerResult<SavedReportSummary>.Failed(new(
                InteractiveReportFailureKind.Forbidden,
                InteractiveReportErrorCodes.ConfiguredReportReadOnly));

        if (metadata.IsDefault && !candidate.Default)
            return InteractiveReportServerResult<SavedReportSummary>.Failed(
                Invalid(InteractiveReportErrorCodes.DefaultReportCannotBeUnset));
        if (candidate.Default && !candidate.Public)
            return InteractiveReportServerResult<SavedReportSummary>.Failed(
                Invalid(InteractiveReportErrorCodes.DefaultReportCannotBeUnset));

        if (DefinitionFailure(
                candidate,
                InteractiveReportErrorCodes.SavedReportTitleInvalid,
                InteractiveReportErrorCodes.SavedReportStateRequired) is { } candidateFailure)
            return InteractiveReportServerResult<SavedReportSummary>.Failed(candidateFailure);
        if (await ValidateSubmittedState(definition, candidate, "saved report update", context, ct) is { } stateFailure)
            return InteractiveReportServerResult<SavedReportSummary>.Failed(stateFailure);

        // A title is not an identifier and collisions are allowed across scopes, so uniqueness is
        // only re-checked when the title or the visibility scope it competes in actually changes.
        var titleChanged = !string.Equals(
            NormalizeTitle(candidate.Title),
            NormalizeTitle(report.Title),
            StringComparison.Ordinal);
        var scopeChanged = candidate.Public != report.IsGlobal || candidate.Default != report.IsDefault;
        var candidateIsPublic = candidate.Public || candidate.Default;
        if ((titleChanged || scopeChanged)
            && await savedReports.FindTitleCollision(
                report.ReportName,
                candidate.Title,
                candidate.Owner,
                candidateIsPublic,
                report.Id,
                ct) is { } collision)
            return InteractiveReportServerResult<SavedReportSummary>.Failed(
                TitleConflict(collision, candidate.Title));

        report.Title = candidate.Title.Trim();
        if (candidate.StateChanged)
            report.StateJson = JsonSerializer.Serialize(candidate.State, IrJson.Options);
        report.IsGlobal = candidate.Public;
        report.IsDefault = candidate.Default;
        report.Owner = candidate.Owner?.Trim();
        if (current.Origin == SavedReportOrigin.Synthetic)
            report.Origin = SavedReportOrigin.User;

        try
        {
            bool updated;
            if (report.IsDefault && !current.IsDefault)
            {
                var currentDefault = await savedReports.FindDefault(report.ReportName, ct)
                    ?? await defaultDocuments.CreateMissing(definition, ct);
                if (currentDefault.Origin == SavedReportOrigin.Configured)
                    return InteractiveReportServerResult<SavedReportSummary>.Failed(new(
                        InteractiveReportFailureKind.Conflict,
                        InteractiveReportErrorCodes.ConfiguredDefaultControlled));
                updated = await savedReports.ReplaceDefault(report, current, currentDefault, ct);
            }
            else
            {
                updated = await savedReports.Update(report, current, ct);
            }

            if (updated)
            {
                logging.Logger?.LogInformation(
                    "Updated saved report {Id} ('{Title}') for report '{Report}' (traceId {TraceId})",
                    report.Id,
                    report.Title,
                    definition.Name,
                    context.TraceIdentifier);
                return InteractiveReportServerResult<SavedReportSummary>.Success(
                    SavedReportSummary.From(report.Metadata(), identity));
            }
            return NotFoundDocument<SavedReportSummary>();
        }
        catch (SavedReportTitleConflictException conflict)
        {
            return InteractiveReportServerResult<SavedReportSummary>.Failed(
                await TitleConflictFromStore(conflict, report.Owner, report.IsPublic, report.Id, ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ReportDocumentBootstrapException)
        {
            return NotFoundDocument<SavedReportSummary>();
        }
        catch (Exception ex)
        {
            return InteractiveReportServerResult<SavedReportSummary>.Failed(
                Internal(definition.Name, "saved report update", context, ex));
        }
    }

    /// <summary>
    /// Clears the base action set, then escalates one administrator action at a time. Each extra
    /// action is a separate decision so a host authorizer sees exactly which privilege a change
    /// demands — publishing, selecting a default, or reassigning an owner — instead of one opaque
    /// bundle.
    /// </summary>
    private async Task<InteractiveReportFailure?> AuthorizeDocumentMutation(
        ReportDefinition definition,
        IReadOnlyCollection<InteractiveReportAction> actions,
        InteractiveReportAuthorizationResource resource,
        bool administratorRequired,
        bool hideDenied,
        string? denialDetail,
        Func<IEnumerable<InteractiveReportAction>> additionalAdministratorActions,
        InteractiveReportRequestContext context,
        CancellationToken ct)
    {
        var denied = await AuthorizeActions(
            definition, actions, resource, administratorRequired, hideDenied, denialDetail, context, ct);
        if (denied is not null) return Failure(denied);

        var canonical = resource with { ReportName = definition.Name };
        var authorized = actions.ToHashSet();
        while (true)
        {
            var next = additionalAdministratorActions()
                .Where(action => !authorized.Contains(action))
                .Select(action => (InteractiveReportAction?)action)
                .FirstOrDefault();
            if (!next.HasValue) break;

            denied = await AuthorizeActions(
                definition,
                [next.Value],
                canonical,
                administratorRequired: true,
                hideDenied,
                denialDetail,
                context,
                ct);
            if (denied is not null) return Failure(denied);
            authorized.Add(next.Value);
        }
        return null;
    }

    /// <summary>Yields the administrator-only actions implied by publication, default, or ownership changes.</summary>
    private static IEnumerable<InteractiveReportAction> RequiredAdministratorActions(
        SavedReportCandidate candidate,
        SavedReportMetadata? current,
        string? originalOwner)
    {
        if (candidate.Public != (current?.IsGlobal ?? false))
            yield return InteractiveReportAction.PublishGlobalReport;
        if (candidate.Default != (current?.IsDefault ?? false))
            yield return InteractiveReportAction.SelectDefaultReport;
        var existingOwner = current is null ? originalOwner : current.Owner;
        if (!string.Equals(candidate.Owner, existingOwner, StringComparison.Ordinal))
            yield return InteractiveReportAction.ChangeSavedReportOwner;
    }

    /// <summary>Rebinds a changed state against the live report and replaces it with refreshed schema caches.</summary>
    private async Task<InteractiveReportFailure?> ValidateSubmittedState(
        ReportDefinition definition,
        SavedReportCandidate candidate,
        string operation,
        InteractiveReportRequestContext context,
        CancellationToken ct)
    {
        if (!candidate.StateChanged) return null;
        if (candidate.State is null)
            return Invalid(InteractiveReportErrorCodes.ReportDefinitionStateRequired);

        try
        {
            var contextParameters = await authorization.ResolveContextParameters(definition, context, ct);
            candidate.State = await executor.RefreshSchemaCaches(
                definition, candidate.State, contextParameters, ct);
            return null;
        }
        catch (ReportValidationException ex)
        {
            logging.Logger?.LogWarning(
                "Report {Report}: {Operation} state validation failed with {ErrorCount} errors (traceId {TraceId})",
                definition.Name,
                operation,
                ex.Errors.Count,
                context.TraceIdentifier);
            return Validation(ex);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Internal(definition.Name, operation, context, ex);
        }
    }

    /// <summary>Rejects a missing or over-long saved-report title.</summary>
    private static InteractiveReportFailure? TitleFailure(string? title, string code)
        => string.IsNullOrWhiteSpace(title) || title.Trim().Length > 200 ? Invalid(code) : null;

    /// <summary>Validates a client-authored candidate independently of live schema binding.</summary>
    private static InteractiveReportFailure? DefinitionFailure(
        SavedReportCandidate candidate,
        string titleCode,
        string stateCode)
    {
        if (TitleFailure(candidate.Title, titleCode) is { } titleFailure) return titleFailure;
        if (candidate.Owner is not null && string.IsNullOrWhiteSpace(candidate.Owner))
            return Invalid(InteractiveReportErrorCodes.SavedReportOwnerInvalid);
        if (candidate.StateChanged && candidate.State is null) return Invalid(stateCode);
        return null;
    }

    /// <summary>Re-reads the colliding row so a storage-detected conflict reports the same shape as a pre-checked one.</summary>
    private async Task<InteractiveReportFailure> TitleConflictFromStore(
        SavedReportTitleConflictException conflict,
        string? owner,
        bool isPublic,
        long? exceptId,
        CancellationToken ct)
    {
        var collision = await savedReports.FindTitleCollision(
            conflict.ReportName, conflict.Title, owner, isPublic, exceptId, ct);
        return collision is not null
            ? TitleConflict(collision, conflict.Title)
            : new(
                InteractiveReportFailureKind.Conflict,
                InteractiveReportErrorCodes.SavedReportTitleConflict,
                $"A saved report named '{conflict.Title.Trim()}' already exists. Replace it if it is available to you, or choose another title.");
    }

    /// <summary>Distinguishes a read-only configured document from an ordinary saved report in a title conflict.</summary>
    private static InteractiveReportFailure TitleConflict(SavedReport collision, string title)
        => collision.Origin == SavedReportOrigin.Configured
            ? new(
                InteractiveReportFailureKind.Conflict,
                InteractiveReportErrorCodes.ConfiguredReportTitleConflict,
                $"'{title.Trim()}' is supplied by a read-only configured report document; choose another title.")
            : new(
                InteractiveReportFailureKind.Conflict,
                InteractiveReportErrorCodes.SavedReportTitleConflict,
                $"A saved report named '{title.Trim()}' already exists. Replace it if it is available to you, or choose another title.");

    private static string NormalizeTitle(string title) => title.Trim().ToUpperInvariant();

    private async Task<InteractiveReportServerResult<ReportResult>> Query(
        string reportName,
        ReportState state,
        SavedReportMetadata? savedReport,
        InteractiveReportAction action,
        bool requireDownload,
        InteractiveReportRequestContext context,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportName);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(context);

        var authorized = await AuthorizeQuery(reportName, action, savedReport, context, ct);
        if (authorized.Failure is not null)
            return InteractiveReportServerResult<ReportResult>.Failed(authorized.Failure);
        var definition = authorized.Definition!;

        if (requireDownload && authorization.CheckFeature(definition, ReportFeatures.Download) is { } disabled)
            return InteractiveReportServerResult<ReportResult>.Failed(Failure(disabled));

        try
        {
            var contextParameters = await authorization.ResolveContextParameters(definition, context, ct);
            var result = await executor.Query(definition, state, contextParameters, ct);
            var truncated = result.Page.Size == 0 && result.TotalRows > result.Rows.Count;
            return InteractiveReportServerResult<ReportResult>.Success(result, truncated);
        }
        catch (ReportValidationException ex)
        {
            return InteractiveReportServerResult<ReportResult>.Failed(Validation(ex));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return InteractiveReportServerResult<ReportResult>.Failed(
                Internal(definition.Name, action == InteractiveReportAction.Export ? "export query" : "query", context, ex));
        }
    }

    /// <summary>
    /// Resolves a report and clears the query gate shared by table queries, exports, and value
    /// lookups. The built-in saved-reports listing substitutes its administrator action so one rule
    /// governs every way a caller can reach report data.
    /// </summary>
    private async Task<(ReportDefinition? Definition, InteractiveReportFailure? Failure)> AuthorizeQuery(
        string reportName,
        InteractiveReportAction action,
        SavedReportMetadata? savedReport,
        InteractiveReportRequestContext context,
        CancellationToken ct)
    {
        var resolved = await authorization.ResolveDefinition(reportName, context, ct);
        if (resolved.Failure is not null) return (null, Failure(resolved.Failure));
        if (resolved.Definition is null)
            return (null, new(
                InteractiveReportFailureKind.NotFound,
                InteractiveReportErrorCodes.ReportNotFound));
        var definition = resolved.Definition;

        var actions = SavedReportsListingDefinition.Matches(reportName)
            ? action == InteractiveReportAction.Export
                ? new[] { InteractiveReportAction.ListAllSavedReports, InteractiveReportAction.Export }
                : new[] { InteractiveReportAction.ListAllSavedReports }
            : [action];
        var denied = await AuthorizeActions(
            definition,
            actions,
            resource: savedReport is null
                ? null
                : new InteractiveReportAuthorizationResource
                {
                    ReportName = definition.Name,
                    SavedReport = savedReport,
                },
            administratorRequired: false,
            hideDenied: false,
            denialDetail: null,
            context,
            ct);
        return denied is null ? (definition, null) : (null, Failure(denied));
    }

    /// <summary>
    /// Records and evaluates one definition-scoped authorization decision. Every client reaches
    /// authorization through here, so the debug trail is the same whichever transport asked.
    /// </summary>
    private async Task<ReportAuthorizationFailure?> AuthorizeActions(
        ReportDefinition definition,
        IReadOnlyCollection<InteractiveReportAction> actions,
        InteractiveReportAuthorizationResource? resource,
        bool administratorRequired,
        bool hideDenied,
        string? denialDetail,
        InteractiveReportRequestContext context,
        CancellationToken ct)
    {
        var names = string.Join(",", actions);
        logging.Logger?.LogDebug(
            "Authorizing report {Report} actions {Actions} (traceId {TraceId})",
            definition.Name,
            names,
            context.TraceIdentifier);
        var denied = await authorization.AuthorizeActions(
            definition, actions, resource, administratorRequired, hideDenied, denialDetail, context, ct);
        logging.Logger?.LogDebug(
            denied is null
                ? "Authorization granted for report {Report} actions {Actions} (traceId {TraceId})"
                : "Authorization denied for report {Report} actions {Actions} (traceId {TraceId})",
            definition.Name,
            names,
            context.TraceIdentifier);
        return denied;
    }

    /// <summary>Flattens structured report-state validation errors into one transport-neutral failure.</summary>
    public static InteractiveReportFailure Validation(ReportValidationException ex)
        => new(
            InteractiveReportFailureKind.Invalid,
            InteractiveReportErrorCodes.ReportStateInvalid,
            string.Join(
                Environment.NewLine,
                ex.Errors.Select(error => string.IsNullOrWhiteSpace(error.Path)
                    ? error.Message
                    : $"{error.Path}: {error.Message}")),
            Validation: ex.Errors
                .GroupBy(error => error.Path)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(error => error.Message).ToArray()));

    private InteractiveReportFailure Internal(
        string reportName,
        string operation,
        InteractiveReportRequestContext context,
        Exception ex)
    {
        var dialect = options.CurrentValue.Reports.TryGetValue(reportName, out var def) && def is not null
            ? def.GetEffectiveDialect()
            : (ReportDialect?)null;

        var dbEx = DbErrorClassifier.UnwrapDbException(ex);
        if (dbEx is not null || ex is System.Net.Sockets.SocketException || ex is TimeoutException)
        {
            var diagnosis = DbErrorClassifier.Classify(dialect ?? ReportDialect.SqlServer, ex);
            logging.Logger?.LogError(
                ex,
                "Report {Report}: {Operation} failed with database error (Category: {Category}, Code: {ProviderCode}, traceId {TraceId}): {Summary}. Hint: {Hint}",
                reportName,
                operation,
                diagnosis.Category,
                diagnosis.ProviderCode ?? "none",
                context.TraceIdentifier,
                diagnosis.Summary,
                diagnosis.RemediationHint ?? "Check database connection, credentials, and permissions.");
        }
        else
        {
            logging.Logger?.LogError(
                ex,
                "Report {Report}: {Operation} failed (traceId {TraceId})",
                reportName,
                operation,
                context.TraceIdentifier);
        }

        return new(
            InteractiveReportFailureKind.Internal,
            InteractiveReportErrorCodes.ReportExecutionFailed,
            TraceIdentifier: context.TraceIdentifier);
    }

    /// <summary>Builds a rejected-input failure carrying its own stable code.</summary>
    private static InteractiveReportFailure Invalid(string code, string? details = null)
        => new(InteractiveReportFailureKind.Invalid, code, details);

    private static InteractiveReportFailure Failure(ReportAuthorizationFailure failure)
        => new(
            failure.Kind switch
            {
                ReportAuthorizationFailureKind.Unauthenticated => InteractiveReportFailureKind.Unauthenticated,
                ReportAuthorizationFailureKind.Forbidden => InteractiveReportFailureKind.Forbidden,
                ReportAuthorizationFailureKind.NotFound => InteractiveReportFailureKind.NotFound,
                _ => InteractiveReportFailureKind.Internal,
            },
            failure.Code,
            failure.Details,
            failure.TraceIdentifier);

    private static InteractiveReportServerResult<T> NotFoundReport<T>()
        => InteractiveReportServerResult<T>.Failed(new(
            InteractiveReportFailureKind.NotFound,
            InteractiveReportErrorCodes.ReportNotFound));

    private static InteractiveReportServerResult<T> NotFoundDocument<T>()
        => InteractiveReportServerResult<T>.Failed(new(
            InteractiveReportFailureKind.NotFound,
            InteractiveReportErrorCodes.SavedReportNotFound));

    /// <summary>
    /// Resolves the report family of a row addressed by id alone. The row is the only thing that
    /// names its family, so it is read before the report gate runs; a family the caller cannot see
    /// must then read exactly like a missing row — the saved-report not-found code, never the
    /// report not-found code — or the id space of hidden reports becomes enumerable.
    /// </summary>
    private async Task<(ReportDefinition? Definition, InteractiveReportFailure? Failure)> ResolveRowFamily(
        string reportName,
        InteractiveReportRequestContext context,
        CancellationToken ct)
    {
        var resolved = await authorization.ResolveDefinition(reportName, context, ct);
        if (resolved.Failure is not null)
            return (null, resolved.Failure.Kind == ReportAuthorizationFailureKind.NotFound
                ? NotFoundDocument<object>().Failure
                : Failure(resolved.Failure));
        if (resolved.Definition is null) return (null, NotFoundDocument<object>().Failure);
        return (resolved.Definition, null);
    }
}
