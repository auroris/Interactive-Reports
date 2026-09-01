using InteractiveReport.Core.Definitions;
using InteractiveReport.Core.SavedReports;
using Microsoft.Extensions.DependencyInjection;

namespace InteractiveReport.AspNetCore.Tests;

internal static class ReportDocumentTestIds
{
    internal static async Task<long> Default(IServiceProvider services, string reportName)
    {
        await services.GetRequiredService<ConfiguredReportDocumentSynchronizer>().EnsureSynced();
        var store = services.GetRequiredService<ISavedReportStore>();
        if (await store.FindDefault(reportName) is { } existing) return existing.Id;

        var definition = await services.GetRequiredService<IReportDefinitionStore>().Find(reportName)
            ?? throw new InvalidOperationException($"Test report '{reportName}' is not configured.");
        return (await services.GetRequiredService<DefaultReportDocumentService>()
            .CreateMissing(definition, CancellationToken.None)).Id;
    }
}
