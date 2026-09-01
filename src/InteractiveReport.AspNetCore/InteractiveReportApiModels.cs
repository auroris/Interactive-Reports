using System.Text.Json;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.SavedReports;

namespace InteractiveReport.AspNetCore;

// These transport models form the JSON contract shared by the built-in HTTP endpoints and client.
// They expose report metadata, mutable document requests, caller identity, and stable error identities;
// none of the authorization hints in these DTOs replace server-side authorization checks.

/// <summary>
/// The single error shape returned by Interactive Reports JSON API endpoints. Code is
/// a stable, language-independent identifier; description and title are English
/// fallback text that clients may replace with localized copy. Details carries optional
/// contextual text that is not a localization key. TraceId is present only when the
/// corresponding server log entry can provide additional diagnostic detail.
/// </summary>
/// <param name="Code">Stable, language-independent error identity.</param>
/// <param name="Description">English fallback description suitable for display.</param>
/// <param name="Title">Optional English fallback title.</param>
/// <param name="Details">Optional request-specific context that is not a localization key.</param>
/// <param name="TraceId">Optional server trace identifier for correlating diagnostic logs.</param>
public sealed record InteractiveReportError(
    string Code,
    string Description,
    string? Title = null,
    string? Details = null,
    string? TraceId = null);

/// <summary>Defines the stable message identities used by <see cref="InteractiveReportError"/>.</summary>
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
    public const string DefaultReportCannotBeUnset = "IR-1312";
    public const string ConfiguredDefaultControlled = "IR-1313";

    public const string MalformedAuthorizationRequest = "IR-1400";
    public const string AuthorizationRestrictionRequired = "IR-1401";
    public const string AuthorizationIdentityInvalid = "IR-1402";
    public const string ReportRestrictionConflict = "IR-1403";
    public const string ReportUserGrantConflict = "IR-1404";

    public const string GraphQlTransportUnsupported = "IR-1500";
}

internal static class InteractiveReportErrorCatalog
{
    /// <summary>
    /// Maps a stable protocol error code to its public title and description.
    /// </summary>
    /// <param name="code">The stable protocol or diagnostic code to return.</param>
    /// <returns>The client-facing title and description for the code.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="code"/> is not a registered protocol error.</exception>
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
        InteractiveReportErrorCodes.DefaultReportCannotBeUnset =>
            ("Default report required", "Select another default report instead of unsetting the current default."),
        InteractiveReportErrorCodes.ConfiguredDefaultControlled =>
            ("Configured default report", "The default report is selected by application configuration and cannot be replaced through the API."),
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

/// <summary>Describes one report's discoverable schema, defaults, presentation, limits, and client capabilities.</summary>
public sealed record InteractiveReportSchema(
    string Name,
    string Title,
    IReadOnlyList<ColumnInfo> Columns,
    InteractiveReportEditLink? EditLink,
    IReadOnlyDictionary<string, InteractiveReportColumnOptions>? ColumnOverrides,
    ReportState DefaultState,
    InteractiveReportCapabilities Capabilities,
    IReadOnlyList<string> Features,
    InteractiveReportLimits Limits,
    InteractiveReportAuthorizationHint Authorization);

/// <summary>Describes an application-owned edit link resolved against the report schema.</summary>
public sealed record InteractiveReportEditLink(string UrlTemplate, string Label, string Target);

/// <summary>Describes presentation behavior applied to one report column.</summary>
public sealed record InteractiveReportColumnOptions(
    bool? HideLabel,
    bool? Sortable,
    bool? Filterable,
    string? HelpText);

/// <summary>Lists expression, aggregate, and chart functions supported by this server.</summary>
public sealed record InteractiveReportCapabilities(
    IReadOnlyList<string> ExpressionFunctions,
    IReadOnlyDictionary<string, IReadOnlyList<string>> AggregateFunctions,
    IReadOnlyDictionary<string, IReadOnlyList<string>> ChartAggregateFunctions);

/// <summary>Reports server-side paging, row, and chart limits for one definition.</summary>
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

/// <summary>Reports the current caller identity and authorization bootstrap diagnostics.</summary>
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

/// <summary>Represents one claim attached to the current caller.</summary>
public sealed record InteractiveReportClaim(string Type, string Value);

/// <summary>Supplies the title, state, and sharing policy for a new saved report.</summary>
public sealed class SaveReportRequest
{
    /// <summary>Gets or sets the required display title.</summary>
    public string? Title { get; set; }
    /// <summary>Gets or sets the required report-state document to persist.</summary>
    public ReportState? State { get; set; }
    /// <summary>Gets or sets whether all authorized report users may load the saved report.</summary>
    public bool IsGlobal { get; set; }
}

/// <summary>Supplies a partial update for an existing saved report; null properties remain unchanged.</summary>
public sealed class UpdateSavedReportRequest
{
    /// <summary>Gets or sets a replacement display title.</summary>
    public string? Title { get; set; }
    /// <summary>Gets or sets a replacement report-state document.</summary>
    public ReportState? State { get; set; }
    /// <summary>Gets or sets a replacement global-sharing flag.</summary>
    public bool? IsGlobal { get; set; }
    /// <summary>Gets or sets whether this report should become the family's default. Only <see langword="true"/> is accepted.</summary>
    public bool? IsDefault { get; set; }
    /// <summary>Gets or sets a replacement owner identity for an administrative reassignment.</summary>
    public string? Owner { get; set; }
}

/// <summary>Identifies one appsettings report configuration visible to the current caller.</summary>
public sealed record ReportConfigurationSummary(string Name, string Title);

/// <summary>Contains saved-report metadata visible to the current caller.</summary>
public sealed record SavedReportSummary(
    [property: System.Text.Json.Serialization.JsonConverter(typeof(ReportDocumentIdJsonConverter))] long Id,
    string ReportName,
    string Title,
    bool IsGlobal,
    bool IsDefault,
    bool Mine,
    bool IsReadOnly,
    DateTime ModifiedUtc)
{
    /// <summary>
    /// Projects persisted saved-report metadata into the caller-facing summary. Every client
    /// adapter shares this projection so ownership and read-only flags cannot drift apart.
    /// </summary>
    /// <param name="report">The persisted metadata to expose.</param>
    /// <param name="caller">The normalized caller identity used to compute the <c>Mine</c> flag.</param>
    /// <returns>The public metadata projection, including ownership and configured-read-only flags.</returns>
    internal static SavedReportSummary From(SavedReportMetadata report, string? caller) => new(
        report.Id,
        report.ReportName,
        report.Title,
        report.IsGlobal,
        report.IsDefault,
        SavedReportAccessPolicy.IsOwner(report, caller),
        report.Origin == SavedReportOrigin.Configured,
        report.ModifiedUtc);
}

/// <summary>Contains a saved report's metadata and report-state document.</summary>
public sealed record SavedReportDocument(SavedReportSummary Summary, JsonElement State);

/// <summary>Supplies an identity for an administrator or per-report authorization grant.</summary>
public sealed class AuthorizationIdentityRequest
{
    /// <summary>Gets or sets the normalized application identity to add or remove.</summary>
    public string? Identity { get; set; }
}

/// <summary>Changes whether a report requires an explicit per-user grant.</summary>
public sealed class ReportRestrictionRequest
{
    /// <summary>Gets or sets the required restricted state.</summary>
    public bool? Restricted { get; set; }
}

/// <summary>Combines configured and database-authored authorization state for administration.</summary>
public sealed record InteractiveReportAuthorizationState(
    IReadOnlyList<string> ConfiguredAdministrators,
    IReadOnlyList<string> DatabaseAdministrators,
    IReadOnlyList<InteractiveReportAuthorizationReport> Reports);

/// <summary>Combines authorization settings and grants for one configured report.</summary>
public sealed record InteractiveReportAuthorizationReport(
    string Name,
    string Title,
    bool Restricted,
    bool ConfiguredRestricted,
    bool DatabaseRestricted,
    bool CanRestrict,
    IReadOnlyList<string> ConfiguredUsers,
    IReadOnlyList<string> DatabaseUsers);
