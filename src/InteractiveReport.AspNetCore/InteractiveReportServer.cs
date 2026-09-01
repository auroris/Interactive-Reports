using System.Text.Json;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Identity;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.SavedReports;
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

/// <summary>A loaded, authorized report document ready for a client to mutate and query.</summary>
public sealed record InteractiveReportLoadedDocument(
    string ReportName,
    SavedReportAuthorizationResource Metadata,
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

    Task<InteractiveReportServerResult<ReportResult>> Query(
        string reportName,
        ReportState state,
        InteractiveReportRequestContext context,
        CancellationToken ct = default);

    Task<InteractiveReportServerResult<ReportResult>> Query(
        InteractiveReportLoadedDocument document,
        InteractiveReportRequestContext context,
        CancellationToken ct = default);

    Task<InteractiveReportServerResult<ReportResult>> QueryForDownload(
        string reportName,
        ReportState state,
        InteractiveReportRequestContext context,
        CancellationToken ct = default);
}

internal sealed class InteractiveReportServer(
    IReportAuthorizationService authorization,
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
                var failure = await authorization.AuthorizeActions(
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

        if (summaries.Count == 0 && firstDenial is not null)
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

        var denied = await authorization.AuthorizeActions(
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

        var visible = family
            .Where(report => VisibleTo(report, identity, administrator))
            .OrderByDescending(report => report.IsDefault)
            .ThenByDescending(report => report.IsGlobal)
            .ThenBy(report => report.Title, StringComparer.OrdinalIgnoreCase)
            .Select(report => SavedReportSummary.From(report.Metadata(), identity))
            .ToList();
        return InteractiveReportServerResult<IReadOnlyList<SavedReportSummary>>.Success(visible);
    }

    public async Task<InteractiveReportServerResult<InteractiveReportLoadedDocument>> LoadDocument(
        long id,
        InteractiveReportRequestContext context,
        CancellationToken ct = default)
    {
        if (id <= 0)
            return InteractiveReportServerResult<InteractiveReportLoadedDocument>.Failed(
                Invalid("The saved-report id must be positive."));

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

        var resolved = await authorization.ResolveDefinition(saved.ReportName, context, ct);
        if (resolved.Failure is not null)
            return InteractiveReportServerResult<InteractiveReportLoadedDocument>.Failed(
                Failure(resolved.Failure));
        if (resolved.Definition is null) return NotFoundDocument<InteractiveReportLoadedDocument>();
        var definition = resolved.Definition;

        var metadata = saved.Metadata();
        var identity = ReportIdentity.Resolve(context.User, options.CurrentValue.IdentityClaim);
        var builtIn = SavedReportAccessPolicy.Read(metadata, identity, administrator: false);
        var denied = await authorization.AuthorizeActions(
            definition,
            [InteractiveReportAction.ReadSavedReport],
            new InteractiveReportAuthorizationResource
            {
                ReportName = definition.Name,
                SavedReport = new SavedReportAuthorizationResource(
                    metadata.Id,
                    metadata.Title,
                    metadata.Owner,
                    metadata.IsGlobal,
                    metadata.IsDefault,
                    metadata.Origin),
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
                (_, state) = await defaultDocuments.LoadState(
                    saved, definition, executor, contextParameters, ct);
            }
            else
            {
                state = JsonSerializer.Deserialize<ReportState>(
                        saved.StateJson ?? throw new JsonException("state is null"),
                        IrJson.Options)
                    ?? throw new JsonException("state is null");
            }

            return InteractiveReportServerResult<InteractiveReportLoadedDocument>.Success(
                new InteractiveReportLoadedDocument(
                    definition.Name,
                    new SavedReportAuthorizationResource(
                        metadata.Id,
                        metadata.Title,
                        metadata.Owner,
                        metadata.IsGlobal,
                        metadata.IsDefault,
                        metadata.Origin),
                    state));
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
        return Query(
            document.ReportName,
            document.State,
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

    private async Task<InteractiveReportServerResult<ReportResult>> Query(
        string reportName,
        ReportState state,
        SavedReportAuthorizationResource? savedReport,
        InteractiveReportAction action,
        bool requireDownload,
        InteractiveReportRequestContext context,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportName);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(context);

        var resolved = await authorization.ResolveDefinition(reportName, context, ct);
        if (resolved.Failure is not null)
            return InteractiveReportServerResult<ReportResult>.Failed(Failure(resolved.Failure));
        if (resolved.Definition is null) return NotFoundReport<ReportResult>();
        var definition = resolved.Definition;

        var actions = SavedReportsListingDefinition.Matches(reportName)
            ? action == InteractiveReportAction.Export
                ? new[] { InteractiveReportAction.ListAllSavedReports, InteractiveReportAction.Export }
                : new[] { InteractiveReportAction.ListAllSavedReports }
            : [action];
        var denied = await authorization.AuthorizeActions(
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
        if (denied is not null)
            return InteractiveReportServerResult<ReportResult>.Failed(Failure(denied));

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
            var validation = ex.Errors
                .GroupBy(error => error.Path)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(error => error.Message).ToArray());
            var details = string.Join(
                Environment.NewLine,
                ex.Errors.Select(error => string.IsNullOrWhiteSpace(error.Path)
                    ? error.Message
                    : $"{error.Path}: {error.Message}"));
            return InteractiveReportServerResult<ReportResult>.Failed(new InteractiveReportFailure(
                InteractiveReportFailureKind.Invalid,
                InteractiveReportErrorCodes.ReportStateInvalid,
                details,
                Validation: validation));
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

    private InteractiveReportFailure Internal(
        string reportName,
        string operation,
        InteractiveReportRequestContext context,
        Exception ex)
    {
        logging.Logger?.LogError(
            ex,
            "Report {Report}: {Operation} failed (traceId {TraceId})",
            reportName,
            operation,
            context.TraceIdentifier);
        return new(
            InteractiveReportFailureKind.Internal,
            InteractiveReportErrorCodes.ReportExecutionFailed,
            TraceIdentifier: context.TraceIdentifier);
    }

    /// <summary>
    /// Applies administrator, saved-report publication, and exact-owner visibility after configured
    /// reconciliation has consumed the database's complete, unfiltered family snapshot.
    /// </summary>
    private static bool VisibleTo(SavedReport report, string? identity, bool administrator)
        => administrator
            || report.IsPublic
            || (identity is not null
                && string.Equals(report.Owner, identity, StringComparison.Ordinal));

    private static InteractiveReportFailure Invalid(string details)
        => new(
            InteractiveReportFailureKind.Invalid,
            InteractiveReportErrorCodes.MalformedReportState,
            details);

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
}
