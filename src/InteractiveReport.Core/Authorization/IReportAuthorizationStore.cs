using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Authorization;

/// <summary>Represents one database-authored authorization entry.</summary>
/// <param name="Id">The stable row identifier.</param>
/// <param name="Kind">The grant or restriction kind.</param>
/// <param name="ReportName">The report affected by a restriction or user grant.</param>
/// <param name="Identity">The identity affected by an administrator or report-user grant.</param>
/// <param name="ModifiedUtc">The persisted modification timestamp.</param>
public sealed record ReportAuthorizationEntry(
    string Id,
    ReportAuthorizationEntryKind Kind,
    string? ReportName,
    string? Identity,
    DateTime ModifiedUtc);

/// <summary>Identifies the meaning and required fields of an authorization row.</summary>
public enum ReportAuthorizationEntryKind
{
    /// <summary>Grants administrator authority to an identity.</summary>
    Administrator,
    /// <summary>Marks a report as requiring explicit user grants.</summary>
    ReportRestriction,
    /// <summary>Grants one identity access to one restricted report.</summary>
    ReportUser,
}

/// <summary>Contains the database portion of one report's effective access decision.</summary>
/// <param name="Restricted">Whether a database row marks the report as restricted.</param>
/// <param name="UserGranted">Whether the supplied identity has a database grant for the report.</param>
public sealed record DatabaseReportAccess(bool Restricted, bool UserGranted);

/// <summary>Contains the database portion of an administrator access decision.</summary>
/// <param name="Configured">Whether any database administrator grants exist.</param>
/// <param name="UserGranted">Whether the supplied identity has a database administrator grant.</param>
public sealed record DatabaseAdministratorAccess(bool Configured, bool UserGranted);

/// <summary>
/// Persists database authorization rows beside saved reports. Configuration grants are
/// composed by the ASP.NET Core layer and are deliberately not copied into this store.
/// </summary>
public interface IReportAuthorizationStore
{
    /// <summary>
    /// Lists every persisted authorization entry, including restricted-report settings.
    /// </summary>
    /// <param name="ct">Signals that the operation should be canceled; defaults to <c>default</c>.</param>
    /// <returns>A task containing all entries in stable presentation order.</returns>
    Task<IReadOnlyList<ReportAuthorizationEntry>> ListAll(CancellationToken ct = default);
    /// <summary>
    /// Loads a database-administrator grant by identity.
    /// </summary>
    /// <param name="identity">The canonical identity to check, or <see langword="null"/> when unavailable.</param>
    /// <param name="ct">Signals that the operation should be canceled; defaults to <c>default</c>.</param>
    /// <returns>A task containing both whether database administrators exist and whether this identity is granted.</returns>
    Task<DatabaseAdministratorAccess> GetAdministratorAccess(
        string? identity,
        CancellationToken ct = default);
    /// <summary>
    /// Loads a report-user grant by report and identity.
    /// </summary>
    /// <param name="reportName">The configured report name to check.</param>
    /// <param name="identity">The canonical identity to check, or <see langword="null"/> when unavailable.</param>
    /// <param name="ct">Signals that the operation should be canceled; defaults to <c>default</c>.</param>
    /// <returns>A task containing both the database restriction marker and matching user grant.</returns>
    Task<DatabaseReportAccess> GetReportAccess(
        string reportName,
        string? identity,
        CancellationToken ct = default);

    /// <summary>
    /// Creates an administrator grant if it does not already exist.
    /// </summary>
    /// <param name="identity">The canonical identity to grant.</param>
    /// <param name="ct">Signals that the operation should be canceled; defaults to <c>default</c>.</param>
    /// <returns>A task that completes after the administrator grant exists in persistence.</returns>
    Task GrantAdministrator(string identity, CancellationToken ct = default);
    /// <summary>
    /// Removes an administrator grant.
    /// </summary>
    /// <param name="identity">The canonical identity to revoke.</param>
    /// <param name="ct">Signals that the operation should be canceled; defaults to <c>default</c>.</param>
    /// <returns>A task whose result is <see langword="true"/> when an administrator grant was removed; otherwise, <see langword="false"/>.</returns>
    Task<bool> RevokeAdministrator(string identity, CancellationToken ct = default);
    /// <summary>
    /// Creates or removes the restriction marker for a report.
    /// </summary>
    /// <param name="reportName">The configured report name to update.</param>
    /// <param name="restricted">Whether the report should require explicit grants.</param>
    /// <param name="ct">Signals that the operation should be canceled; defaults to <c>default</c>.</param>
    /// <returns>A task that completes after the report restriction marker is created or removed.</returns>
    Task SetReportRestricted(string reportName, bool restricted, CancellationToken ct = default);
    /// <summary>
    /// Creates a report-user grant if it does not already exist.
    /// </summary>
    /// <param name="reportName">The restricted report to grant.</param>
    /// <param name="identity">The canonical identity to grant.</param>
    /// <param name="ct">Signals that the operation should be canceled; defaults to <c>default</c>.</param>
    /// <returns>A task that completes after the report-user grant exists in persistence.</returns>
    Task GrantReportUser(string reportName, string identity, CancellationToken ct = default);
    /// <summary>
    /// Removes a report-user grant.
    /// </summary>
    /// <param name="reportName">The restricted report to revoke.</param>
    /// <param name="identity">The canonical identity to revoke.</param>
    /// <param name="ct">Signals that the operation should be canceled; defaults to <c>default</c>.</param>
    /// <returns>A task whose result is <see langword="true"/> when a report-user grant was removed; otherwise, <see langword="false"/>.</returns>
    Task<bool> RevokeReportUser(string reportName, string identity, CancellationToken ct = default);
}

/// <summary>Identifies the authorization table on the resolved saved-report connection.</summary>
/// <param name="ConnectionName">The connection registry name.</param>
/// <param name="Dialect">The SQL dialect used by the connection.</param>
/// <param name="AutoCreate">Whether the store may create or upgrade its table.</param>
/// <param name="TableName">The validated effective table name.</param>
public sealed record ReportAuthorizationStoreConfig(
    string ConnectionName,
    ReportDialect Dialect,
    bool AutoCreate = true,
    string TableName = "IR_REPORT_AUTHORIZATION");
