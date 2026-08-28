using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Authorization;

/// <summary>One database-authored authorization entry.</summary>
public sealed record ReportAuthorizationEntry(
    string Id,
    ReportAuthorizationEntryKind Kind,
    string? ReportName,
    string? Identity,
    DateTime ModifiedUtc);

public enum ReportAuthorizationEntryKind
{
    Administrator,
    ReportRestriction,
    ReportUser,
}

/// <summary>Database portion of one report's effective access decision.</summary>
public sealed record DatabaseReportAccess(bool Restricted, bool UserGranted);
public sealed record DatabaseAdministratorAccess(bool Configured, bool UserGranted);

/// <summary>
/// Database authorization rows stored beside saved reports. Configuration grants are
/// composed by the ASP.NET Core layer and are deliberately not copied into this store.
/// </summary>
public interface IReportAuthorizationStore
{
    Task<IReadOnlyList<ReportAuthorizationEntry>> ListAll(CancellationToken ct = default);
    Task<DatabaseAdministratorAccess> GetAdministratorAccess(
        string? identity,
        CancellationToken ct = default);
    Task<bool> HasAdministrators(CancellationToken ct = default);
    Task<bool> IsAdministrator(string identity, CancellationToken ct = default);
    Task<DatabaseReportAccess> GetReportAccess(
        string reportName,
        string? identity,
        CancellationToken ct = default);

    Task GrantAdministrator(string identity, CancellationToken ct = default);
    Task<bool> RevokeAdministrator(string identity, CancellationToken ct = default);
    Task SetReportRestricted(string reportName, bool restricted, CancellationToken ct = default);
    Task GrantReportUser(string reportName, string identity, CancellationToken ct = default);
    Task<bool> RevokeReportUser(string reportName, string identity, CancellationToken ct = default);
}

/// <summary>Authorization table on the resolved saved-report connection.</summary>
public sealed record ReportAuthorizationStoreConfig(
    string ConnectionName,
    ReportDialect Dialect,
    bool AutoCreate = true,
    string TableName = "IR_REPORT_AUTHORIZATION");
