using InteractiveReport.Core.SavedReports;

namespace InteractiveReport.AspNetCore;

internal enum SavedReportAccess
{
    Allowed,
    Hidden,
}

/// <summary>The saved-report ownership/global/admin matrix, independent of HTTP.</summary>
internal static class SavedReportAccessPolicy
{
    public static SavedReportAccess Read(SavedReport report, string? identity, bool administrator)
        => administrator || report.IsPrimary || report.IsGlobal || IsOwner(report, identity)
            ? SavedReportAccess.Allowed
            : SavedReportAccess.Hidden;

    public static SavedReportAccess Modify(SavedReport report, string? identity, bool administrator)
        => administrator || IsOwner(report, identity)
            ? SavedReportAccess.Allowed
            : SavedReportAccess.Hidden;

    public static SavedReportAccess Read(SavedReportMetadata report, string? identity, bool administrator)
        => administrator || report.IsPrimary || report.IsGlobal || IsOwner(report, identity)
            ? SavedReportAccess.Allowed
            : SavedReportAccess.Hidden;

    public static SavedReportAccess Modify(SavedReportMetadata report, string? identity, bool administrator)
        => administrator || IsOwner(report, identity)
            ? SavedReportAccess.Allowed
            : SavedReportAccess.Hidden;

    public static bool IsOwner(SavedReport report, string? identity)
        => identity is not null
           && string.Equals(report.Owner, identity, StringComparison.Ordinal);

    public static bool IsOwner(SavedReportMetadata report, string? identity)
        => identity is not null
           && string.Equals(report.Owner, identity, StringComparison.Ordinal);
}
