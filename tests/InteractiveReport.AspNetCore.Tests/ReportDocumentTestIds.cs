using System.Text.Json;
using InteractiveReport.Core.Definitions;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;
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
        var report = new SavedReport
        {
            ReportName = definition.Name,
            Owner = null,
            Title = definition.Title ?? ColumnModel.Prettify(definition.Name),
            IsDefault = true,
            IsGlobal = true,
            StateJson = JsonSerializer.Serialize(ReportDocumentDefaults.Create(definition), IrJson.Options),
            ModifiedUtc = DateTime.UtcNow,
            Origin = SavedReportOrigin.User,
        };
        await store.Create(report);
        return report.Id;
    }
}
