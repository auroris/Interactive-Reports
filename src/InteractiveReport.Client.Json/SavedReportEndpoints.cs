using System.Text.Json;
using InteractiveReport.AspNetCore;
using InteractiveReport.AspNetCore.Definitions;
using InteractiveReport.Core.Authorization;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Identity;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.SavedReports;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace InteractiveReport.Client.Json;

/// <summary>
/// Implements identity, saved-report, report-document, and authorization-user HTTP operations.
///
/// Authorization matrix:
///   owner                    → read, update title/state, delete
///   anyone (default/global) → read
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
    internal static async Task<IResult> Whoami(HttpContext ctx, CancellationToken ct)
    {
        var described = await EndpointExtensions.Server(ctx).DescribeIdentity(
            EndpointExtensions.Context(ctx), ct);
        return described.Failure is not null
            ? EndpointExtensions.Failure(described.Failure, ctx)
            : Results.Json(described.Value, IrJson.Options);
    }

    // Application-provided authorization user directory.

    /// <summary>
    /// Returns application-provided identity choices after administrator authorization.
    /// </summary>
    /// <param name="ctx">The current HTTP request and response context.</param>
    /// <param name="ct">Cancels authorization and user-provider lookup.</param>
    /// <returns>A normalized JSON user list, a hidden-denial result, or a sanitized provider failure.</returns>
    internal static async Task<IResult> AdminListUsers(HttpContext ctx, CancellationToken ct)
    {
        var listed = await EndpointExtensions.Server(ctx).ListAuthorizationUsers(
            EndpointExtensions.Context(ctx), ct);
        return listed.Failure is not null
            ? EndpointExtensions.Failure(listed.Failure, ctx)
            : Results.Json(listed.Value, IrJson.Options);
    }

    // End-user saved-report surface.

    /// <summary>Lists appsettings report configurations visible to the current caller.</summary>
    /// <param name="ctx">The current HTTP request and response context.</param>
    /// <param name="ct">Cancels definition resolution and authorization.</param>
    /// <returns>A JSON array of the configurations the caller may view, or the first denial when none are.</returns>
    internal static async Task<IResult> ListConfigurations(HttpContext ctx, CancellationToken ct)
    {
        var listed = await EndpointExtensions.Server(ctx).ListConfigurations(EndpointExtensions.Context(ctx), ct);
        return listed.Failure is not null
            ? EndpointExtensions.Failure(listed.Failure, ctx)
            : Results.Json(listed.Value, IrJson.Options);
    }

    /// <summary>
    /// Lists saved reports visible to the caller for one authorized report definition.
    /// </summary>
    /// <param name="name">The appsettings report configuration name.</param>
    /// <param name="ctx">The current HTTP request and response context.</param>
    /// <param name="ct">Cancels authorization, document synchronization, and persistence reads.</param>
    /// <returns>A JSON array containing the complete family for administrators, or public and caller-owned documents otherwise.</returns>
    /// <remarks>Synchronizes configured document identities before listing.</remarks>
    internal static async Task<IResult> ListForReport(string name, HttpContext ctx, CancellationToken ct)
    {
        var listed = await EndpointExtensions.Server(ctx).ListSavedReports(name, EndpointExtensions.Context(ctx), ct);
        return listed.Failure is not null
            ? EndpointExtensions.Failure(listed.Failure, ctx)
            : Results.Json(listed.Value, IrJson.Options);
    }

    /// <summary>
    /// Validates and creates a private or global saved report owned by the current caller.
    /// </summary>
    /// <param name="id">The numeric document id used to select its report family.</param>
    /// <param name="ctx">The current HTTP request and response context.</param>
    /// <param name="ct">Cancels authorization, request-body reading, state validation, and persistence.</param>
    /// <returns>The created summary with HTTP 201, or an authentication, access, validation, or title-conflict result.</returns>
    /// <remarks>Consumes the JSON request body, may refresh schema caches in the submitted state, and inserts one saved-report row.</remarks>
    internal static async Task<IResult> Save(long id, HttpContext ctx, CancellationToken ct)
    {
        var saved = await EndpointExtensions.Server(ctx).SaveDocument(
            id,
            token => JsonSerializer.DeserializeAsync<SaveReportRequest>(
                ctx.Request.Body, IrJson.Options, token).AsTask(),
            EndpointExtensions.Context(ctx),
            ct);
        return saved.Failure is not null
            ? EndpointExtensions.Failure(saved.Failure, ctx)
            : Results.Json(saved.Value, IrJson.Options, statusCode: StatusCodes.Status201Created);
    }

    /// <summary>
    /// Loads one visible saved report and returns its metadata plus its report-state document.
    /// </summary>
    /// <param name="name">The appsettings report configuration name.</param>
    /// <param name="id">The numeric report-document identifier from the route.</param>
    /// <param name="ctx">The current HTTP request and response context.</param>
    /// <param name="ct">Cancels persistence reads, authorization, and missing-file cleanup.</param>
    /// <returns>The saved-report document JSON, or a hidden not-found/access result.</returns>
    /// <remarks>
    /// A transport adapter over <see cref="IInteractiveReportServer.LoadDocument(string, long, InteractiveReportRequestContext, CancellationToken)"/>:
    /// family verification, configured-file reconciliation, default-document auto-repair, and schema-cache
    /// refreshing all live there, so every client adapter loads a document the same way.
    /// </remarks>
    internal static async Task<IResult> Load(
        string name,
        long id,
        HttpContext ctx,
        CancellationToken ct)
    {
        var loaded = await EndpointExtensions.Server(ctx).LoadDocument(name, id, EndpointExtensions.Context(ctx), ct);
        if (loaded.Failure is not null) return EndpointExtensions.Failure(loaded.Failure, ctx);
        var document = loaded.Value!;
        return Results.Json(
            new SavedReportDocument(
                Summary(document.Metadata, Identity(ctx)),
                JsonSerializer.SerializeToElement(document.State, IrJson.Options)),
            IrJson.Options);
    }

    /// <summary>
    /// Applies a partial update to a user report, including selection as the report family's default.
    /// </summary>
    /// <param name="id">The numeric report-document identifier from the route.</param>
    /// <param name="ctx">The current HTTP request and response context.</param>
    /// <param name="ct">Cancels body reading, authorization, validation, and persistence.</param>
    /// <returns>The updated summary, or a not-found, read-only, validation, denial, or title-conflict result.</returns>
    /// <remarks>Consumes the JSON request body and conditionally updates persistence. State changes are rebound and receive refreshed schema caches before storage.</remarks>
    internal static async Task<IResult> Update(long id, HttpContext ctx, CancellationToken ct)
    {
        var updated = await EndpointExtensions.Server(ctx).UpdateDocument(
            id,
            token => JsonSerializer.DeserializeAsync<UpdateSavedReportRequest>(
                ctx.Request.Body, IrJson.Options, token).AsTask(),
            EndpointExtensions.Context(ctx),
            ct);
        return updated.Failure is not null
            ? EndpointExtensions.Failure(updated.Failure, ctx)
            : Results.Json(updated.Value, IrJson.Options);
    }

    /// <summary>
    /// Deletes an authorized user-authored saved report.
    /// </summary>
    /// <param name="id">The numeric report-document identifier from the route.</param>
    /// <param name="ctx">The current HTTP request and response context.</param>
    /// <param name="ct">Cancels persistence reads, authorization, and deletion.</param>
    /// <returns>No content on deletion, or a hidden not-found, denial, or configured-read-only result.</returns>
    /// <remarks>Deletes one persistence row when it still matches the loaded version.</remarks>
    internal static async Task<IResult> Delete(long id, HttpContext ctx, CancellationToken ct)
    {
        var deleted = await EndpointExtensions.Server(ctx).DeleteDocument(
            id, EndpointExtensions.Context(ctx), ct);
        return deleted.Failure is not null
            ? EndpointExtensions.Failure(deleted.Failure, ctx)
            : Results.NoContent();
    }

    // Administrator report-document interchange.

    /// <summary>
    /// Downloads the canonical source-controlled envelope, not the endpoint's
    /// summary/state response wrapper. The resulting file can be placed directly in a report definition's
    /// documentFiles collection after the operator chooses whether it should be the configured default.
    /// </summary>
    /// <param name="id">The numeric report-document identifier from the route.</param>
    /// <param name="ctx">The current HTTP request and response context.</param>
    /// <param name="ct">Cancels persistence reads and administrator authorization.</param>
    /// <returns>An indented JSON file result, or an authentication, hidden-denial, not-found, or server-error result.</returns>
    /// <remarks>Reads and deserializes the stored state; it does not mutate the saved report.</remarks>
    internal static async Task<IResult> AdminDownloadDocument(
        long id,
        HttpContext ctx,
        CancellationToken ct)
    {
        var exported = await EndpointExtensions.Server(ctx).ExportDocument(
            id, EndpointExtensions.Context(ctx), ct);
        if (exported.Failure is not null) return EndpointExtensions.Failure(exported.Failure, ctx);

        var export = exported.Value!;
        var jsonOptions = new JsonSerializerOptions(IrJson.Options) { WriteIndented = true };
        return Results.File(
            JsonSerializer.SerializeToUtf8Bytes(export.Document, jsonOptions),
            "application/json; charset=utf-8",
            DownloadFileName(export.ReportName, export.Document.Title!));
    }

    /// <summary>
    /// Imports a canonical report-document file as a private saved report owned by the
    /// administrator.
    /// </summary>
    /// <param name="id">The numeric report-document id whose family receives the import.</param>
    /// <param name="ctx">The current HTTP request and response context.</param>
    /// <param name="ct">Cancels body reading, administrator authorization, validation, and persistence.</param>
    /// <returns>The imported summary with HTTP 201, or an authentication, hidden-denial, validation, title-conflict, or server-error result.</returns>
    internal static async Task<IResult> AdminUploadDocument(
        long id,
        HttpContext ctx,
        CancellationToken ct)
    {
        var imported = await EndpointExtensions.Server(ctx).ImportDocument(
            id,
            token => JsonSerializer.DeserializeAsync<ReportDocumentFile>(
                ctx.Request.Body, IrJson.Options, token).AsTask(),
            EndpointExtensions.Context(ctx),
            ct);
        return imported.Failure is not null
            ? EndpointExtensions.Failure(imported.Failure, ctx)
            : Results.Json(imported.Value, IrJson.Options, statusCode: StatusCodes.Status201Created);
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
    private static SavedReportSummary Summary(SavedReportMetadata report, string? caller)
        => SavedReportSummary.From(report, caller);

    /// <summary>
    /// Builds a filesystem-neutral JSON download name from a report name and title.
    /// </summary>
    /// <param name="reportName">The canonical configured report name.</param>
    /// <param name="title">The saved-report display title.</param>
    /// <returns>A sanitized filename suitable for Content-Disposition.</returns>
    private static string DownloadFileName(string reportName, string title)
        => InteractiveReportHttpRequest.SafeFileName($"{reportName}.{title}", ".json");
}
