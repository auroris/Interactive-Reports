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

/// <summary>
/// Applies the saved-report ownership, publication, and administrator matrix independently of HTTP.
/// Every decision is made from <see cref="ISavedReportAccessSubject"/>, so the complete row and its
/// metadata projection are judged by the same rule rather than by parallel copies of it.
/// </summary>
internal static class SavedReportAccessPolicy
{
    /// <summary>
    /// Evaluates whether an identity may read a saved report.
    /// </summary>
    /// <param name="report">The saved report, or its metadata, being requested.</param>
    /// <param name="identity">The caller's canonical identity, or <see langword="null"/> when unavailable.</param>
    /// <param name="administrator">Whether the caller has administrative authority.</param>
    /// <returns><see cref="SavedReportAccess.Allowed"/> for an administrator, owner, default report, or global report; otherwise, <see cref="SavedReportAccess.Hidden"/>.</returns>
    public static SavedReportAccess Read(
        ISavedReportAccessSubject report,
        string? identity,
        bool administrator)
        => administrator || report.IsPublic || IsOwner(report, identity)
            ? SavedReportAccess.Allowed
            : SavedReportAccess.Hidden;

    /// <summary>
    /// Evaluates whether an identity may modify a saved report. Publication does not grant this:
    /// a globally readable document is still only its owner's to change.
    /// </summary>
    /// <param name="report">The saved report, or its metadata, being modified.</param>
    /// <param name="identity">The caller's canonical identity, or <see langword="null"/> when unavailable.</param>
    /// <param name="administrator">Whether the caller has administrative authority.</param>
    /// <returns><see cref="SavedReportAccess.Allowed"/> for an administrator or owner; otherwise, <see cref="SavedReportAccess.Hidden"/>.</returns>
    public static SavedReportAccess Modify(
        ISavedReportAccessSubject report,
        string? identity,
        bool administrator)
        => administrator || IsOwner(report, identity)
            ? SavedReportAccess.Allowed
            : SavedReportAccess.Hidden;

    /// <summary>
    /// Determines whether the identity owns the saved report. Ownership compares ordinally: storage
    /// collations vary, but an authorization decision must not.
    /// </summary>
    /// <param name="report">The saved report, or its metadata, whose owner is being checked.</param>
    /// <param name="identity">The caller's canonical identity, or <see langword="null"/> when unavailable.</param>
    /// <returns><see langword="true"/> when the identity owns the saved report; otherwise, <see langword="false"/>.</returns>
    public static bool IsOwner(ISavedReportAccessSubject report, string? identity)
        => identity is not null
           && string.Equals(report.Owner, identity, StringComparison.Ordinal);
}
