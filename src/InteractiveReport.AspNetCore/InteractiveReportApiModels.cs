using System.Text.Json;
using InteractiveReport.Core.Model;

namespace InteractiveReport.AspNetCore;

/// <summary>The discoverable definition and client capabilities of one report.</summary>
public sealed record InteractiveReportSchema(
    string Name,
    string Title,
    string? StyleSheet,
    IReadOnlyList<ColumnInfo> Columns,
    InteractiveReportEditLink? EditLink,
    IReadOnlyDictionary<string, InteractiveReportColumnOptions>? ColumnOverrides,
    ReportState DefaultState,
    int StateVersion,
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
