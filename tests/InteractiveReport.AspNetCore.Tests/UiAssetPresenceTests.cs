namespace InteractiveReport.AspNetCore.Tests;

/// <summary>
/// The embedded-resource glob is silent when Ui\dist is empty; the csproj guard
/// covers Release builds and packing, and this locks the Debug/test path plus the
/// LogicalName scheme the asset endpoint depends on.
/// </summary>
public sealed class UiAssetPresenceTests
{
    [Theory]
    [InlineData("InteractiveReport.Client.Json.Ui.ir.js")]
    [InlineData("InteractiveReport.Client.Json.Ui.ir-admin.js")]
    [InlineData("InteractiveReport.Client.Json.Ui.ir-chart.js")]
    public void The_browser_bundles_are_embedded_under_their_logical_names(string resource)
    {
        Assert.Contains(resource, typeof(UiEndpoints).Assembly.GetManifestResourceNames());
    }
}
