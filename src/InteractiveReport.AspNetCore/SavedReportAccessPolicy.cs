using InteractiveReport.Core.SavedReports;

namespace InteractiveReport.AspNetCore;

internal enum SavedReportAccess
{
    Allowed,
    Hidden,
    AdministratorRequired,
}

/// <summary>The saved-report ownership/global/admin matrix, independent of HTTP.</summary>
internal static class SavedReportAccessPolicy
{
    public static SavedReportAccess Read(SavedReport report, string? identity, bool administrator)
        => administrator || report.IsGlobal || IsOwner(report, identity)
            ? SavedReportAccess.Allowed
            : SavedReportAccess.Hidden;

    public static SavedReportAccess Modify(SavedReport report, string? identity, bool administrator)
    {
        if (administrator) return SavedReportAccess.Allowed;
        if (!IsOwner(report, identity)) return SavedReportAccess.Hidden;
        return report.IsGlobal ? SavedReportAccess.AdministratorRequired : SavedReportAccess.Allowed;
    }

    public static bool IsOwner(SavedReport report, string? identity)
        => identity is not null
           && string.Equals(report.Owner, identity, StringComparison.OrdinalIgnoreCase);
}
