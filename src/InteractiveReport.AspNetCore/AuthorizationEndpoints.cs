using System.Text.Json;
using InteractiveReport.Core.Authorization;
using InteractiveReport.Core.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace InteractiveReport.AspNetCore;

/// <summary>Administrator CRUD for database-authored administration and report grants.</summary>
internal static class AuthorizationEndpoints
{
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

    internal static Task<IResult> GrantAdministrator(HttpContext context, CancellationToken ct)
        => MutateIdentity(
            context,
            SavedReportsListingDefinition.Name,
            (store, identity, token) => store.GrantAdministrator(identity, token),
            ct);

    internal static Task<IResult> RevokeAdministrator(HttpContext context, CancellationToken ct)
        => MutateIdentity(
            context,
            SavedReportsListingDefinition.Name,
            async (store, identity, token) =>
            {
                await store.RevokeAdministrator(identity, token);
            },
            ct);

    internal static async Task<IResult> SetReportRestriction(
        string name,
        HttpContext context,
        CancellationToken ct)
    {
        var configured = FindConfiguredReport(context, name);
        if (configured is null) return Results.NotFound();
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
            return BadRequest("Malformed authorization request", ex.Message);
        }
        if (request?.Restricted is null)
            return BadRequest("Malformed authorization request", "restricted is required");
        if (request.Restricted == true
            && (report.Authorization?.AllowAnonymous == true
                || report.Authorization?.AdministratorsOnly == true))
            return BadRequest(
                "Report authorization conflict",
                "An anonymous or administrators-only report cannot also use user restrictions.");

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

    private static async Task<IResult> MutateReportIdentity(
        string name,
        HttpContext context,
        Func<IReportAuthorizationStore, string, string, CancellationToken, Task> mutation,
        CancellationToken ct)
    {
        var configured = FindConfiguredReport(context, name);
        if (configured is null) return Results.NotFound();
        var (canonicalName, report) = configured.Value;
        var denied = await AuthorizeAdministration(context, canonicalName, ct);
        if (denied is not null) return denied;
        if (report.Authorization?.AllowAnonymous == true
            || report.Authorization?.AdministratorsOnly == true)
            return BadRequest(
                "Report authorization conflict",
                "An anonymous or administrators-only report cannot have user grants.");
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
            return (null, BadRequest("Malformed authorization request", ex.Message));
        }

        var identity = request?.Identity?.Trim();
        return string.IsNullOrEmpty(identity) || identity.Length > 400
            ? (null, BadRequest(
                "Malformed authorization request",
                "identity is required (1–400 characters)"))
            : (identity, null);
    }

    private static Task<IResult?> AuthorizeAdministration(
        HttpContext context,
        string resourceReportName,
        CancellationToken ct)
        => context.RequestServices.GetRequiredService<IReportAccessService>()
            .AuthorizeEndpoint(new EndpointAccessRequest
            {
                Actions = [InteractiveReportAction.ManageAuthorization],
                Resource = new InteractiveReportAuthorizationResource
                {
                    ReportName = resourceReportName,
                },
                AdministratorRequired = true,
                HideDenied = true,
            }, context, ct);

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

    private static IReportAuthorizationStore Store(HttpContext context)
        => context.RequestServices.GetRequiredService<IReportAuthorizationStore>();

    private static InteractiveReportOptions Options(HttpContext context)
        => context.RequestServices.GetRequiredService<IOptionsMonitor<InteractiveReportOptions>>()
            .CurrentValue;

    private static IResult BadRequest(string title, string detail)
        => Results.Problem(
            title: title,
            detail: detail,
            statusCode: StatusCodes.Status400BadRequest);

}
