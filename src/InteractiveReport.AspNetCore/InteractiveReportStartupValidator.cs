using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace InteractiveReport.AspNetCore;

/// <summary>
/// Fails host startup on configuration mistakes instead of deferring them to the
/// first request: options materialization (surfacing binder failures), every report
/// definition's validation and connection/dialect resolution, activation of each
/// dataSource-minted connection (proving the provider assembly loads and the
/// connection string parses — unopened, zero I/O). Saved-report storage is optional
/// at host startup and is resolved only when a persistence or administration feature
/// is used. Runs the same pipeline as per-request Find, minus Find's saved-report
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
        if (current.Administrators.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException(
                "InteractiveReport:Administrators entries must be non-empty identity values.");
        if (current.Administrators.Select(identity => identity.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() != current.Administrators.Count)
            throw new InvalidOperationException(
                "InteractiveReport:Administrators contains duplicate identity values.");
        foreach (var (name, configured) in current.Reports)
        {
            var snapshot = ConfigurationReportDefinitionStore.Snapshot(name, configured);
            ConfigurationReportDefinitionStore.Validate(snapshot);
            ConfigurationReportDefinitionStore.ResolveConnection(snapshot, registry);
            ActivationCheck(snapshot.Connection);
        }

        var savedTable = ReportConnectionRegistry.ResolveTableName(
            current.SavedReports, current.SavedReports.TableName);
        var authorizationTable = ReportConnectionRegistry.ResolveTableName(
            current.SavedReports, current.Authorization.TableName);
        if (string.Equals(
                savedTable,
                authorizationTable,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "The effective authorization table name must differ from the saved-report table name.");
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
