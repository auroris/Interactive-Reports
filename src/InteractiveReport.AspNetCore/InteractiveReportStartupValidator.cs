using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
    ReportConnectionRegistry registry,
    ConfiguredReportDocumentStore configuredDocuments,
    InteractiveReportLogging logging) : IHostedService
{
    /// <summary>
    /// Validates the initial options snapshot and logs startup success or failure.
    /// </summary>
    /// <param name="cancellationToken">Accepted by the hosted-service contract; validation is synchronous.</param>
    /// <returns>A completed task when validation succeeds.</returns>
    /// <remarks>Instantiates and disposes connections synthesized from configured data sources, but does not open them.</remarks>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        logging.Logger?.LogInformation("Interactive Reports startup validation started");
        try
        {
            Validate();
            logging.Logger?.LogInformation(
                "Interactive Reports startup validation completed for {ReportCount} reports",
                options.CurrentValue.Reports.Count);
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            logging.Logger?.LogCritical(ex, "Interactive Reports startup validation failed");
            throw;
        }
    }

    /// <summary>
    /// Validates the current Interactive Reports configuration before serving requests.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when identities, definitions, connections, dialects, or effective persistence table names are invalid.</exception>
    private void Validate()
    {
        var current = options.CurrentValue;
        if (current.Administrators.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException(
                "InteractiveReport:Administrators entries must be non-empty identity values.");
        if (current.Administrators.Select(identity => identity.Trim())
            .Distinct(StringComparer.Ordinal).Count() != current.Administrators.Count)
            throw new InvalidOperationException(
                "InteractiveReport:Administrators contains duplicate identity values.");
        foreach (var (name, configured) in current.Reports)
        {
            var snapshot = ConfigurationReportDefinitionStore.Snapshot(name, configured);
            ConfigurationReportDefinitionStore.Validate(snapshot);
            ConfigurationReportDefinitionStore.ResolveConnection(snapshot, registry);
            ActivationCheck(snapshot.Connection);
            configuredDocuments.ValidateDefaults(snapshot.Name, snapshot.DocumentFiles);
            logging.Logger?.LogDebug(
                "Validated report definition '{Report}' (Connection: '{Connection}', Dialect: {Dialect})",
                snapshot.Name,
                snapshot.Connection,
                snapshot.GetEffectiveDialect());
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
    }

    /// <summary>
    /// Completes immediately because the startup validator owns no background work.
    /// </summary>
    /// <param name="cancellationToken">Accepted by the hosted-service contract and otherwise ignored.</param>
    /// <returns>A completed task.</returns>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Instantiates and disposes data-source-derived connections to verify provider activation; host factories stay untouched.
    /// </summary>
    /// <param name="connectionName">The resolved registry name, including the internal data-source prefix when applicable.</param>
    private void ActivationCheck(string connectionName)
    {
        if (connectionName.StartsWith("__ir:ds:", StringComparison.Ordinal))
        {
            registry.CreateConnection(connectionName).Dispose();
            logging.Logger?.LogDebug(
                "Verified provider activation for synthesized data source '{Connection}'",
                connectionName);
        }
    }
}
