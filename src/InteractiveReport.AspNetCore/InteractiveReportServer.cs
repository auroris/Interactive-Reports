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
