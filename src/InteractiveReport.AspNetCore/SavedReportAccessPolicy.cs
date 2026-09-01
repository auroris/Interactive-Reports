using InteractiveReport.Core.SavedReports;

namespace InteractiveReport.AspNetCore;

/// <summary>Represents the deliberately non-distinguishing result of a saved-report access check.</summary>
internal enum SavedReportAccess
{
    /// <summary>The requested operation is permitted.</summary>
    Allowed,
    /// <summary>The row must be treated as absent to avoid disclosing its existence.</summary>
    Hidden,
}

/// <summary>Applies the saved-report ownership, publication, and administrator matrix independently of HTTP.</summary>
internal static class SavedReportAccessPolicy
{
    /// <summary>
    /// Evaluates whether an identity may read a complete saved report.
    /// </summary>
    /// <param name="report">The complete saved report being requested.</param>
    /// <param name="identity">The caller's canonical identity, or <see langword="null"/> when unavailable.</param>
    /// <param name="administrator">Whether the caller has administrative authority.</param>
    /// <returns><see cref="SavedReportAccess.Allowed"/> for an administrator, owner, primary report, or global report; otherwise, <see cref="SavedReportAccess.Hidden"/>.</returns>
    public static SavedReportAccess Read(SavedReport report, string? identity, bool administrator)
        => administrator || report.IsPublic || IsOwner(report, identity)
            ? SavedReportAccess.Allowed
            : SavedReportAccess.Hidden;

    /// <summary>
    /// Evaluates whether an identity may modify a complete saved report.
    /// </summary>
    /// <param name="report">The complete saved report being modified.</param>
    /// <param name="identity">The caller's canonical identity, or <see langword="null"/> when unavailable.</param>
    /// <param name="administrator">Whether the caller has administrative authority.</param>
    /// <returns><see cref="SavedReportAccess.Allowed"/> for an administrator or owner; otherwise, <see cref="SavedReportAccess.Hidden"/>.</returns>
    public static SavedReportAccess Modify(SavedReport report, string? identity, bool administrator)
        => administrator || IsOwner(report, identity)
            ? SavedReportAccess.Allowed
            : SavedReportAccess.Hidden;

    /// <summary>
    /// Evaluates whether an identity may read saved-report metadata.
    /// </summary>
    /// <param name="report">The saved-report metadata being requested.</param>
    /// <param name="identity">The caller's canonical identity, or <see langword="null"/> when unavailable.</param>
    /// <param name="administrator">Whether the caller has administrative authority.</param>
    /// <returns><see cref="SavedReportAccess.Allowed"/> for an administrator, owner, primary report, or global report; otherwise, <see cref="SavedReportAccess.Hidden"/>.</returns>
    public static SavedReportAccess Read(SavedReportMetadata report, string? identity, bool administrator)
        => administrator || report.IsPublic || IsOwner(report, identity)
            ? SavedReportAccess.Allowed
            : SavedReportAccess.Hidden;

    /// <summary>
    /// Evaluates whether an identity may modify saved-report metadata.
    /// </summary>
    /// <param name="report">The saved-report metadata being modified.</param>
    /// <param name="identity">The caller's canonical identity, or <see langword="null"/> when unavailable.</param>
    /// <param name="administrator">Whether the caller has administrative authority.</param>
    /// <returns><see cref="SavedReportAccess.Allowed"/> for an administrator or owner; otherwise, <see cref="SavedReportAccess.Hidden"/>.</returns>
    public static SavedReportAccess Modify(SavedReportMetadata report, string? identity, bool administrator)
        => administrator || IsOwner(report, identity)
            ? SavedReportAccess.Allowed
            : SavedReportAccess.Hidden;

    /// <summary>
    /// Determines whether the identity owns the complete saved report.
    /// </summary>
    /// <param name="report">The complete saved report whose owner is being checked.</param>
    /// <param name="identity">The caller's canonical identity, or <see langword="null"/> when unavailable.</param>
    /// <returns><see langword="true"/> when the identity owns the saved report; otherwise, <see langword="false"/>.</returns>
    public static bool IsOwner(SavedReport report, string? identity)
        => identity is not null
           && string.Equals(report.Owner, identity, StringComparison.Ordinal);

    /// <summary>
    /// Determines whether the identity owns the saved-report metadata row.
    /// </summary>
    /// <param name="report">The saved-report metadata whose owner is being checked.</param>
    /// <param name="identity">The caller's canonical identity, or <see langword="null"/> when unavailable.</param>
    /// <returns><see langword="true"/> when the identity owns the saved report; otherwise, <see langword="false"/>.</returns>
    public static bool IsOwner(SavedReportMetadata report, string? identity)
        => identity is not null
           && string.Equals(report.Owner, identity, StringComparison.Ordinal);
}
