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
    [InlineData(false, "OWNER", false, (int)SavedReportAccess.Hidden)]
    [InlineData(false, "owner", false, (int)SavedReportAccess.Allowed)]
    [InlineData(true, "owner", false, (int)SavedReportAccess.Allowed)]
    [InlineData(true, "other", true, (int)SavedReportAccess.Allowed)]
    public void Modify_encodes_ownership_and_global_matrix(
        bool global,
        string? identity,
        bool administrator,
        int expected)
        => Assert.Equal((SavedReportAccess)expected, SavedReportAccessPolicy.Modify(Report(global), identity, administrator));

    [Fact]
    public void Primary_is_public_and_remains_owner_managed()
    {
        var report = Report(global: false);
        report.IsPrimary = true;

        Assert.Equal(SavedReportAccess.Allowed, SavedReportAccessPolicy.Read(report, identity: null, administrator: false));
        Assert.Equal(SavedReportAccess.Allowed,
            SavedReportAccessPolicy.Modify(report, "owner", administrator: false));
        Assert.Equal(SavedReportAccess.Allowed,
            SavedReportAccessPolicy.Modify(report, "other", administrator: true));
    }

    private static SavedReport Report(bool global) => new()
    {
        Id = 1,
        ReportName = "orders",
        Title = "Orders",
        Owner = "owner",
        IsGlobal = global,
        StateJson = "{}",
    };
}
