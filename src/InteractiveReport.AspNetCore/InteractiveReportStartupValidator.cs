using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace InteractiveReport.AspNetCore;

/// <summary>
/// Fails host startup on configuration mistakes instead of deferring them to the
/// first request: options materialization (surfacing binder failures), every report
/// definition's validation and connection/dialect resolution, activation of each
/// dataSource-minted connection (proving the provider assembly loads and the
/// connection string parses — unopened, zero I/O), and the saved-report store
/// target. Runs the same pipeline as per-request Find, minus Find's saved-report
/// synchronization side effects; configuration reloads after startup are covered by
/// that per-request path. Code-registered factories are deliberately not invoked
/// here beyond what dialect sniffing needs — a declared dialect keeps side-effecting
/// factories untouched.
/// </summary>
internal sealed class InteractiveReportStartupValidator(
    IOptionsMonitor<InteractiveReportOptions> options,
    ReportConnectionRegistry registry) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var current = options.CurrentValue;
        foreach (var (name, configured) in current.Reports)
        {
            var snapshot = ConfigurationReportDefinitionStore.Snapshot(name, configured);
            ConfigurationReportDefinitionStore.Validate(snapshot);
            ConfigurationReportDefinitionStore.ResolveConnection(snapshot, registry);
            ActivationCheck(snapshot.Connection);
        }

        ActivationCheck(registry.ResolveStoreConfig(current.SavedReports).ConnectionName);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Instantiate-and-dispose dataSource connections only; host factories stay untouched.</summary>
    private void ActivationCheck(string connectionName)
    {
        if (connectionName.StartsWith("__ir:ds:", StringComparison.Ordinal))
            registry.CreateConnection(connectionName).Dispose();
    }
}
