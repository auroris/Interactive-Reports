using InteractiveReport.Core.SavedReports;

namespace InteractiveReport.AspNetCore.Tests;

public sealed class SavedReportAccessPolicyTests
{
    [Theory]
    [InlineData(false, null, false, (int)SavedReportAccess.Hidden)]
    [InlineData(false, "other", false, (int)SavedReportAccess.Hidden)]
    [InlineData(false, "owner", false, (int)SavedReportAccess.Allowed)]
    [InlineData(true, null, false, (int)SavedReportAccess.Allowed)]
    [InlineData(false, null, true, (int)SavedReportAccess.Allowed)]
    public void Read_encodes_visibility_matrix(
        bool global,
        string? identity,
        bool administrator,
        int expected)
        => Assert.Equal((SavedReportAccess)expected, SavedReportAccessPolicy.Read(Report(global), identity, administrator));

    [Theory]
    [InlineData(false, null, false, (int)SavedReportAccess.Hidden)]
    [InlineData(false, "other", false, (int)SavedReportAccess.Hidden)]
    [InlineData(false, "OWNER", false, (int)SavedReportAccess.Allowed)]
    [InlineData(true, "owner", false, (int)SavedReportAccess.AdministratorRequired)]
    [InlineData(true, "other", true, (int)SavedReportAccess.Allowed)]
    public void Modify_encodes_ownership_and_global_matrix(
        bool global,
        string? identity,
        bool administrator,
        int expected)
        => Assert.Equal((SavedReportAccess)expected, SavedReportAccessPolicy.Modify(Report(global), identity, administrator));

    private static SavedReport Report(bool global) => new()
    {
        Id = "saved-1",
        ReportName = "orders",
        Title = "Orders",
        Owner = "owner",
        IsGlobal = global,
        StateJson = "{}",
    };
}
