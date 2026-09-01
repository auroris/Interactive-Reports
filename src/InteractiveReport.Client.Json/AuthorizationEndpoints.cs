using System.Text.Json;
using InteractiveReport.AspNetCore;
using InteractiveReport.Core.Authorization;
using InteractiveReport.Core.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace InteractiveReport.Client.Json;

/// <summary>
/// Implements administrator-only HTTP operations for database-authored administrators,
/// report restrictions, and per-user grants. Every operation reauthorizes the caller,
/// hides denied resources behind not-found responses, and translates persistence failures
/// to the common Interactive Reports error contract.
/// </summary>
internal static class AuthorizationEndpoints
{
    /// <summary>
    /// Lists configured and database-authored authorization state after verifying administrator access.
    /// </summary>
    /// <param name="context">The current HTTP request and response context.</param>
    /// <param name="ct">Cancels authorization and database access.</param>
    /// <returns>A JSON authorization snapshot, a hidden-denial response, or a standardized server error.</returns>
    /// <remarks>Reads all authorization rows and writes the selected HTTP response; it does not mutate authorization state.</remarks>
    internal static async Task<IResult> List(HttpContext context, CancellationToken ct)
    {
        var denied = await AuthorizeAdministration(
            context, SavedReportsListingDefinition.Name, ct);
        if (denied is not null) return denied;

        IReadOnlyList<ReportAuthorizationEntry> entries;
        try
        {
            entries = await Store(context).ListAll(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return EndpointExtensions.ServerError(
                context, SavedReportsListingDefinition.Name, "authorization listing", ex);
        }

        var options = Options(context);
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

        var reports = options.Reports.Select(pair =>
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

        return Results.Json(new InteractiveReportAuthorizationState(
            ConfiguredAdministrators: options.Administrators
                .Select(identity => identity.Trim())
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            DatabaseAdministrators: databaseAdministrators,
            Reports: reports), IrJson.Options);
    }

    /// <summary>
    /// Grants database-authored administrator access to the identity in the request body.
    /// </summary>
    /// <param name="context">The current HTTP request and response context.</param>
    /// <param name="ct">Cancels authorization, request-body reading, and persistence.</param>
    /// <returns>No content on success, or a validation, denial, or server-error result.</returns>
    /// <remarks>Inserts an administrator grant when the caller is authorized.</remarks>
    internal static Task<IResult> GrantAdministrator(HttpContext context, CancellationToken ct)
        => MutateIdentity(
            context,
            SavedReportsListingDefinition.Name,
            (store, identity, token) => store.GrantAdministrator(identity, token),
            ct);

    /// <summary>
    /// Revokes database-authored administrator access from the identity in the request body.
    /// </summary>
    /// <param name="context">The current HTTP request and response context.</param>
    /// <param name="ct">Cancels authorization, request-body reading, and persistence.</param>
    /// <returns>No content on success, or a validation, denial, or server-error result.</returns>
    /// <remarks>Deletes an administrator grant when the caller is authorized.</remarks>
    internal static Task<IResult> RevokeAdministrator(HttpContext context, CancellationToken ct)
        => MutateIdentity(
            context,
            SavedReportsListingDefinition.Name,
            async (store, identity, token) =>
            {
                await store.RevokeAdministrator(identity, token);
            },
            ct);

    /// <summary>
    /// Enables or disables the database-authored restriction for one configured report.
    /// </summary>
    /// <param name="name">The case-insensitive configured report name from the route.</param>
    /// <param name="context">The current HTTP request and response context.</param>
    /// <param name="ct">Cancels authorization, request-body reading, and persistence.</param>
    /// <returns>No content on success, or a not-found, validation, denial, or server-error result.</returns>
    /// <remarks>Updates the report-restriction row after rejecting configurations that cannot use user grants.</remarks>
    internal static async Task<IResult> SetReportRestriction(
        string name,
        HttpContext context,
        CancellationToken ct)
    {
        var configured = FindConfiguredReport(context, name);
        if (configured is null) return EndpointExtensions.ReportNotFound();
        var (canonicalName, report) = configured.Value;
        var denied = await AuthorizeAdministration(context, canonicalName, ct);
        if (denied is not null) return denied;

        ReportRestrictionRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<ReportRestrictionRequest>(
                context.Request.Body, IrJson.Options, ct);
        }
        catch (JsonException ex)
        {
            return BadRequest(
                InteractiveReportErrorCodes.MalformedAuthorizationRequest,
                ex.Message);
        }
        if (request?.Restricted is null)
            return BadRequest(
                InteractiveReportErrorCodes.AuthorizationRestrictionRequired);
        if (request.Restricted == true
            && (report.Authorization?.AllowAnonymous == true
                || report.Authorization?.AdministratorsOnly == true))
            return BadRequest(
                InteractiveReportErrorCodes.ReportRestrictionConflict);

        try
        {
            await Store(context).SetReportRestricted(canonicalName, request.Restricted.Value, ct);
            return Results.NoContent();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return EndpointExtensions.ServerError(
                context, canonicalName, "report restriction update", ex);
        }
    }

    /// <summary>
    /// Grants the request-body identity database-authored access to one configured report.
    /// </summary>
    /// <param name="name">The case-insensitive configured report name from the route.</param>
    /// <param name="context">The current HTTP request and response context.</param>
    /// <param name="ct">Cancels authorization, request-body reading, and persistence.</param>
    /// <returns>No content on success, or a not-found, validation, denial, or server-error result.</returns>
    /// <remarks>Inserts a per-report user grant when the report supports user restrictions.</remarks>
    internal static Task<IResult> GrantReportUser(
        string name,
        HttpContext context,
        CancellationToken ct)
        => MutateReportIdentity(
            name,
            context,
            (store, reportName, identity, token) =>
                store.GrantReportUser(reportName, identity, token),
            ct);

    /// <summary>
    /// Revokes the request-body identity's database-authored access to one configured report.
    /// </summary>
    /// <param name="name">The case-insensitive configured report name from the route.</param>
    /// <param name="context">The current HTTP request and response context.</param>
    /// <param name="ct">Cancels authorization, request-body reading, and persistence.</param>
    /// <returns>No content on success, or a not-found, validation, denial, or server-error result.</returns>
    /// <remarks>Deletes a per-report user grant when the report supports user restrictions.</remarks>
    internal static Task<IResult> RevokeReportUser(
        string name,
        HttpContext context,
        CancellationToken ct)
        => MutateReportIdentity(
            name,
            context,
            async (store, reportName, identity, token) =>
            {
                await store.RevokeReportUser(reportName, identity, token);
            },
            ct);

    /// <summary>
    /// Runs a validated administrator-identity mutation behind the common administration check.
    /// </summary>
    /// <param name="context">The current HTTP request and response context.</param>
    /// <param name="resourceReportName">The report resource used for application authorization and error logging.</param>
    /// <param name="mutation">The persistence operation to invoke with the trimmed identity.</param>
    /// <param name="ct">Cancels authorization, request-body reading, and persistence.</param>
    /// <returns>No content on success, or the first validation, denial, or server-error result.</returns>
    /// <remarks>Consumes the JSON request body and may mutate the authorization store.</remarks>
    private static async Task<IResult> MutateIdentity(
        HttpContext context,
        string resourceReportName,
        Func<IReportAuthorizationStore, string, CancellationToken, Task> mutation,
        CancellationToken ct)
    {
        var denied = await AuthorizeAdministration(context, resourceReportName, ct);
        if (denied is not null) return denied;
        var (identity, malformed) = await ReadIdentity(context, ct);
        if (malformed is not null) return malformed;

        try
        {
            await mutation(Store(context), identity!, ct);
            return Results.NoContent();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return EndpointExtensions.ServerError(
                context, resourceReportName, "administrator authorization update", ex);
        }
    }

    /// <summary>
    /// Runs a validated per-report identity mutation for a configured report that supports user grants.
    /// </summary>
    /// <param name="name">The case-insensitive configured report name from the route.</param>
    /// <param name="context">The current HTTP request and response context.</param>
    /// <param name="mutation">The persistence operation to invoke with the canonical report name and trimmed identity.</param>
    /// <param name="ct">Cancels authorization, request-body reading, and persistence.</param>
    /// <returns>No content on success, or the first not-found, validation, denial, or server-error result.</returns>
    /// <remarks>Consumes the JSON request body and may mutate the authorization store.</remarks>
    private static async Task<IResult> MutateReportIdentity(
        string name,
        HttpContext context,
        Func<IReportAuthorizationStore, string, string, CancellationToken, Task> mutation,
        CancellationToken ct)
    {
        var configured = FindConfiguredReport(context, name);
        if (configured is null) return EndpointExtensions.ReportNotFound();
        var (canonicalName, report) = configured.Value;
        var denied = await AuthorizeAdministration(context, canonicalName, ct);
        if (denied is not null) return denied;
        if (report.Authorization?.AllowAnonymous == true
            || report.Authorization?.AdministratorsOnly == true)
            return BadRequest(
                InteractiveReportErrorCodes.ReportUserGrantConflict);
        var (identity, malformed) = await ReadIdentity(context, ct);
        if (malformed is not null) return malformed;

        try
        {
            await mutation(Store(context), canonicalName, identity!, ct);
            return Results.NoContent();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return EndpointExtensions.ServerError(
                context, canonicalName, "report user authorization update", ex);
        }
    }

    /// <summary>
    /// Reads and validates the identity supplied to an authorization endpoint.
    /// </summary>
    /// <param name="context">The current HTTP request and response context.</param>
    /// <param name="ct">Cancels JSON deserialization.</param>
    /// <returns>The trimmed identity and no error, or a null identity and a malformed/invalid request result.</returns>
    /// <remarks>Consumes the request body. A valid identity contains 1 to 400 characters after trimming.</remarks>
    private static async Task<(string? Identity, IResult? Error)> ReadIdentity(
        HttpContext context,
        CancellationToken ct)
    {
        AuthorizationIdentityRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<AuthorizationIdentityRequest>(
                context.Request.Body, IrJson.Options, ct);
        }
        catch (JsonException ex)
        {
            return (null, BadRequest(
                InteractiveReportErrorCodes.MalformedAuthorizationRequest,
                ex.Message));
        }

        var identity = request?.Identity?.Trim();
        return string.IsNullOrEmpty(identity) || identity.Length > 400
            ? (null, BadRequest(
                InteractiveReportErrorCodes.AuthorizationIdentityInvalid))
            : (identity, null);
    }

    /// <summary>
    /// Verifies report-administrator access for an authorization endpoint.
    /// </summary>
    /// <param name="context">The current HTTP request and response context.</param>
    /// <param name="resourceReportName">The report name carried to application authorization as the protected resource.</param>
    /// <param name="ct">Cancels application and database authorization checks.</param>
    /// <returns><see langword="null"/> when access is granted; otherwise, the hidden-denial or authorization-error result.</returns>
    private static async Task<IResult?> AuthorizeAdministration(
        HttpContext context,
        string resourceReportName,
        CancellationToken ct)
    {
        var denied = await EndpointExtensions.Authorization(context).AuthorizeEndpoint(
            [InteractiveReportAction.ManageAuthorization],
            new InteractiveReportAuthorizationResource { ReportName = resourceReportName },
            administratorRequired: true,
            hideDenied: true,
            denialDetail: null,
            EndpointExtensions.Context(context),
            ct);
        return denied is null ? null : EndpointExtensions.AuthorizationFailure(denied);
    }

    /// <summary>
    /// Finds a configured report case-insensitively while preserving its canonical configured name.
    /// </summary>
    /// <param name="context">The current HTTP request and response context.</param>
    /// <param name="name">The report name from the route.</param>
    /// <returns>The canonical name and definition, or <see langword="null"/> when no report matches.</returns>
    private static KeyValuePair<string, ReportDefinition>? FindConfiguredReport(
        HttpContext context,
        string name)
    {
        var reports = Options(context).Reports;
        if (!reports.TryGetValue(name, out var report)) return null;
        var canonicalName = reports.Keys.First(key =>
            string.Equals(key, name, StringComparison.OrdinalIgnoreCase));
        return new KeyValuePair<string, ReportDefinition>(canonicalName, report);
    }

    /// <summary>
    /// Resolves the configured report-authorization store from request services.
    /// </summary>
    /// <param name="context">The current HTTP request and response context.</param>
    /// <returns>The configured report-authorization store.</returns>
    private static IReportAuthorizationStore Store(HttpContext context)
        => context.RequestServices.GetRequiredService<IReportAuthorizationStore>();

    /// <summary>
    /// Resolves the current Interactive Reports options from request services.
    /// </summary>
    /// <param name="context">The current HTTP request and response context.</param>
    /// <returns>The interactive report options.</returns>
    private static InteractiveReportOptions Options(HttpContext context)
        => context.RequestServices.GetRequiredService<IOptionsMonitor<InteractiveReportOptions>>()
            .CurrentValue;

    /// <summary>
    /// Creates a standardized validation-error response.
    /// </summary>
    /// <param name="code">The stable protocol or diagnostic code to return.</param>
    /// <param name="details">Optional request-specific diagnostic context.</param>
    /// <returns>A JSON HTTP 400 result using the catalog title and description for <paramref name="code"/>.</returns>
    private static IResult BadRequest(
        string code,
        string? details = null)
        => EndpointExtensions.Error(
            code,
            StatusCodes.Status400BadRequest,
            details);

}
