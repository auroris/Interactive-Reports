using System.Text.Json;
using InteractiveReport.Core.Model;

namespace InteractiveReport.AspNetCore;

/// <summary>
/// The single error shape returned by Interactive Reports JSON API endpoints. Code is
/// a stable, language-independent identifier; description and title are English
/// fallback text that clients may replace with localized copy. Details carries optional
/// contextual text that is not a localization key. TraceId is present only when the
/// corresponding server log entry can provide additional diagnostic detail.
/// </summary>
public sealed record InteractiveReportError(
    string Code,
    string Description,
    string? Title = null,
    string? Details = null,
    string? TraceId = null);

/// <summary>Stable message identities used by <see cref="InteractiveReportError"/>.</summary>
public static class InteractiveReportErrorCodes
{
    public const string AuthenticationRequired = "IR-1000";
    public const string ReportNotFound = "IR-1001";
    public const string SavedReportNotFound = "IR-1002";
    public const string EndpointNotFound = "IR-1003";
    public const string AuthorizationDenied = "IR-1004";
    public const string AuthorizationFailed = "IR-1005";

    public const string FeatureDisabled = "IR-1100";
    public const string UnsupportedExportFormat = "IR-1101";

    public const string MalformedReportState = "IR-1200";
    public const string ReportStateInvalid = "IR-1201";
    public const string ReportExecutionFailed = "IR-1202";

    public const string MalformedSaveRequest = "IR-1300";
    public const string SavedReportTitleInvalid = "IR-1301";
    public const string SavedReportStateRequired = "IR-1302";
    public const string MalformedUpdateRequest = "IR-1303";
    public const string SavedReportOwnerInvalid = "IR-1304";
    public const string MalformedReportDocument = "IR-1305";
    public const string ReportDocumentTitleInvalid = "IR-1306";
    public const string ReportDocumentStateRequired = "IR-1307";
    public const string ReportDefinitionStateRequired = "IR-1308";
    public const string SavedReportTitleConflict = "IR-1309";
    public const string ConfiguredReportTitleConflict = "IR-1310";
    public const string ConfiguredReportReadOnly = "IR-1311";

    public const string MalformedAuthorizationRequest = "IR-1400";
    public const string AuthorizationRestrictionRequired = "IR-1401";
    public const string AuthorizationIdentityInvalid = "IR-1402";
    public const string ReportRestrictionConflict = "IR-1403";
    public const string ReportUserGrantConflict = "IR-1404";

    public const string GraphQlTransportUnsupported = "IR-1500";
}

internal static class InteractiveReportErrorCatalog
{
    internal static (string Title, string Description) Find(string code) => code switch
    {
        InteractiveReportErrorCodes.AuthenticationRequired =>
            ("Authentication required", "Sign in to perform this operation."),
        InteractiveReportErrorCodes.ReportNotFound =>
            ("Report not found", "The report was not found or you are not allowed to access it."),
        InteractiveReportErrorCodes.SavedReportNotFound =>
            ("Saved report not found", "The saved report was not found or you are not allowed to access it."),
        InteractiveReportErrorCodes.EndpointNotFound =>
            ("Endpoint not found", "The requested endpoint is not available."),
        InteractiveReportErrorCodes.AuthorizationDenied =>
            ("Authorization denied", "You are not allowed to perform this operation."),
        InteractiveReportErrorCodes.AuthorizationFailed =>
            ("Report authorization failed", "An unexpected error occurred while authorizing the report operation."),
        InteractiveReportErrorCodes.FeatureDisabled =>
            ("Feature disabled", "This feature is not enabled for the report."),
        InteractiveReportErrorCodes.UnsupportedExportFormat =>
            ("Unsupported export format", "The requested export format is not supported."),
        InteractiveReportErrorCodes.MalformedReportState =>
            ("Malformed report state document", "The report state document is not valid JSON."),
        InteractiveReportErrorCodes.ReportStateInvalid =>
            ("Report state failed validation", "One or more report settings are invalid."),
        InteractiveReportErrorCodes.ReportExecutionFailed =>
            ("Report execution failed", "An unexpected error occurred while processing the report."),
        InteractiveReportErrorCodes.MalformedSaveRequest =>
            ("Malformed save request", "The save request is not valid JSON."),
        InteractiveReportErrorCodes.SavedReportTitleInvalid =>
            ("Invalid saved report title", "Enter a title between 1 and 200 characters."),
        InteractiveReportErrorCodes.SavedReportStateRequired =>
            ("Saved report state required", "The save request must include report state."),
        InteractiveReportErrorCodes.MalformedUpdateRequest =>
            ("Malformed update request", "The update request is not valid JSON."),
        InteractiveReportErrorCodes.SavedReportOwnerInvalid =>
            ("Invalid saved report owner", "The owner must be a non-empty identity value."),
        InteractiveReportErrorCodes.MalformedReportDocument =>
            ("Malformed report document", "The report document is not valid JSON."),
        InteractiveReportErrorCodes.ReportDocumentTitleInvalid =>
            ("Invalid report document title", "Enter a document title between 1 and 200 characters."),
        InteractiveReportErrorCodes.ReportDocumentStateRequired =>
            ("Report document state required", "The report document must include report state."),
        InteractiveReportErrorCodes.ReportDefinitionStateRequired =>
            ("Report definition state required", "The report definition must include report state."),
        InteractiveReportErrorCodes.SavedReportTitleConflict =>
            ("Saved report title conflict", "A saved report with this title already exists."),
        InteractiveReportErrorCodes.ConfiguredReportTitleConflict =>
            ("Configured report title conflict", "A read-only configured report uses this title."),
        InteractiveReportErrorCodes.ConfiguredReportReadOnly =>
            ("Read-only report", "Configured report documents cannot be updated or deleted. Use Save As to create an editable copy."),
        InteractiveReportErrorCodes.MalformedAuthorizationRequest =>
            ("Malformed authorization request", "The authorization request is not valid JSON."),
        InteractiveReportErrorCodes.AuthorizationRestrictionRequired =>
            ("Restriction value required", "The authorization request must include a restriction value."),
        InteractiveReportErrorCodes.AuthorizationIdentityInvalid =>
            ("Invalid identity", "Enter an identity between 1 and 400 characters."),
        InteractiveReportErrorCodes.ReportRestrictionConflict =>
            ("Report authorization conflict", "Anonymous and administrators-only reports cannot use user restrictions."),
        InteractiveReportErrorCodes.ReportUserGrantConflict =>
            ("Report authorization conflict", "Anonymous and administrators-only reports cannot have user grants."),
        InteractiveReportErrorCodes.GraphQlTransportUnsupported =>
            ("Unsupported GraphQL transport", "Interactive Reports GraphQL supports HTTP GET and POST queries only."),
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown Interactive Reports error code."),
    };
}

/// <summary>The discoverable definition and client capabilities of one report.</summary>
public sealed record InteractiveReportSchema(
    string Name,
    string Title,
    string? StyleSheet,
    IReadOnlyList<ColumnInfo> Columns,
    InteractiveReportEditLink? EditLink,
    IReadOnlyDictionary<string, InteractiveReportColumnOptions>? ColumnOverrides,
    ReportState DefaultState,
    InteractiveReportCapabilities Capabilities,
    IReadOnlyList<string> Features,
    InteractiveReportLimits Limits,
    InteractiveReportAuthorizationHint Authorization);

/// <summary>An application-owned edit link resolved against the report schema.</summary>
public sealed record InteractiveReportEditLink(string UrlTemplate, string Label, string Target);

/// <summary>Presentation behavior applied to one report column.</summary>
public sealed record InteractiveReportColumnOptions(
    bool? HideLabel,
    bool? Sortable,
    bool? Filterable,
    string? HelpText);

/// <summary>Expression and aggregate functions supported by this server.</summary>
public sealed record InteractiveReportCapabilities(
    IReadOnlyList<string> ExpressionFunctions,
    IReadOnlyDictionary<string, IReadOnlyList<string>> AggregateFunctions,
    IReadOnlyDictionary<string, IReadOnlyList<string>> ChartAggregateFunctions);

/// <summary>Server-side limits for one report definition.</summary>
public sealed record InteractiveReportLimits(
    int DefaultPageSize,
    int MaxPageSize,
    int MaxRows,
    int MaxChartPoints);

/// <summary>
/// A presentation hint for administration controls. It is not an authorization grant;
/// each administration request is independently authorized.
/// </summary>
public sealed record InteractiveReportAuthorizationHint(bool MayRequestAdministration);

/// <summary>The current caller identity and authorization bootstrap diagnostics.</summary>
public sealed record InteractiveReportIdentity(
    bool Authenticated,
    string? Identity,
    bool IsAdministrator,
    bool ConfiguredAdministrator,
    bool DatabaseAdministrator,
    bool AdministratorListConfigured,
    bool ApplicationAuthorizationConfigured,
    string? Name,
    string? AuthenticationType,
    IReadOnlyList<InteractiveReportClaim> Claims);

/// <summary>One claim attached to the current caller.</summary>
public sealed record InteractiveReportClaim(string Type, string Value);

/// <summary>Creates a saved report for a configured report definition.</summary>
public sealed class SaveReportRequest
{
    public string? Title { get; set; }
    public ReportState? State { get; set; }
    public bool IsGlobal { get; set; }
    public bool IsPrimary { get; set; }
}

/// <summary>Changes selected properties of an existing saved report.</summary>
public sealed class UpdateSavedReportRequest
{
    public string? Title { get; set; }
    public ReportState? State { get; set; }
    public bool? IsGlobal { get; set; }
    public bool? IsPrimary { get; set; }
    public string? Owner { get; set; }
}

/// <summary>Saved-report metadata visible to the current caller.</summary>
public sealed record SavedReportSummary(
    string Id,
    string ReportName,
    string Title,
    bool IsGlobal,
    bool IsPrimary,
    bool Mine,
    bool IsReadOnly,
    DateTime ModifiedUtc);

/// <summary>A saved report's metadata and versioned report-state document.</summary>
public sealed record SavedReportDocument(SavedReportSummary Summary, JsonElement State);

/// <summary>An identity used by administrator and per-report authorization grants.</summary>
public sealed class AuthorizationIdentityRequest
{
    public string? Identity { get; set; }
}

/// <summary>Changes whether a report requires an explicit per-user grant.</summary>
public sealed class ReportRestrictionRequest
{
    public bool? Restricted { get; set; }
}

/// <summary>The configured and database-authored authorization state.</summary>
public sealed record InteractiveReportAuthorizationState(
    IReadOnlyList<string> ConfiguredAdministrators,
    IReadOnlyList<string> DatabaseAdministrators,
    IReadOnlyList<InteractiveReportAuthorizationReport> Reports);

/// <summary>Authorization settings and grants for one configured report.</summary>
public sealed record InteractiveReportAuthorizationReport(
    string Name,
    string Title,
    bool Restricted,
    bool ConfiguredRestricted,
    bool DatabaseRestricted,
    bool CanRestrict,
    IReadOnlyList<string> ConfiguredUsers,
    IReadOnlyList<string> DatabaseUsers);
