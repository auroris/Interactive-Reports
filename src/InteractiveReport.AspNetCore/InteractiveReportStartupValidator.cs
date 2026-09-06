using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InteractiveReport.AspNetCore;

/// <summary>
/// Fails host startup on configuration mistakes instead of deferring them to the
/// first request: options materialization (surfacing binder failures), every report
/// definition's validation and connection/dialect resolution, activation of each
/// dataSource-minted connection (proving the provider assembly loads and the
/// connection string parses — unopened, zero I/O), and the presence of authorization services
/// whenever a policy is named. Saved-report storage is optional at host startup and
/// is resolved only when a persistence or administration feature is used. Runs the same
/// pipeline as per-request Find, minus Find's saved-report synchronization side effects;
/// configuration reloads after startup are covered by that per-request path.
/// Code-registered factories are deliberately not invoked here beyond what dialect
/// sniffing needs — a declared dialect keeps side-effecting factories untouched.
/// </summary>
internal sealed class InteractiveReportStartupValidator(
    IOptionsMonitor<InteractiveReportOptions> options,
    ReportConnectionRegistry registry,
    ConfiguredReportDocumentStore configuredDocuments,
    IServiceProvider services,
    InteractiveReportLogging logging) : IHostedService
{
    /// <summary>
    /// Validates the initial options snapshot and logs startup success or failure.
    /// </summary>
    /// <param name="cancellationToken">Cancels policy resolution; the rest of validation is synchronous.</param>
    /// <returns>A task that completes when validation succeeds.</returns>
    /// <remarks>Instantiates and disposes connections synthesized from configured data sources, but does not open them.</remarks>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logging.Logger?.LogInformation("Interactive Reports startup validation started");
        try
        {
            Validate();
            await ValidatePolicies(cancellationToken);
            logging.Logger?.LogInformation(
                "Interactive Reports startup validation completed for {ReportCount} reports",
                options.CurrentValue.Reports.Count);
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
        if (current.AdministratorPolicy is not null && string.IsNullOrWhiteSpace(current.AdministratorPolicy))
            throw new InvalidOperationException(
                "InteractiveReport:AdministratorPolicy must be a policy name when specified.");
        if (current.UserDirectory.MaxResults is < 1 or > 1000)
            throw new InvalidOperationException(
                "InteractiveReport:UserDirectory:MaxResults must be between 1 and 1000.");
        if (current.UserDirectory.CacheSeconds < 0)
            throw new InvalidOperationException(
                "InteractiveReport:UserDirectory:CacheSeconds cannot be negative.");
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
        var administratorTable = ReportConnectionRegistry.ResolveTableName(
            current.SavedReports, current.Authorization.TableName);
        if (string.Equals(
                savedTable,
                administratorTable,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "The effective administrator table name must differ from the saved-report table name.");
    }

    /// <summary>
    /// Checks the named authorization policies. Missing authorization services cannot appear after
    /// the container is built, so that fails the host; a policy the provider does not know yet is
    /// only a warning, because a dynamic policy provider may resolve it once requests arrive.
    /// </summary>
    /// <param name="ct">Cancels policy resolution.</param>
    /// <exception cref="InvalidOperationException">Thrown when a policy is named but the host registered no authorization services.</exception>
    private async Task ValidatePolicies(CancellationToken ct)
    {
        var current = options.CurrentValue;
        var policies = new List<(string Name, string Owner)>();
        if (!string.IsNullOrWhiteSpace(current.AdministratorPolicy))
            policies.Add((current.AdministratorPolicy.Trim(), "InteractiveReport:AdministratorPolicy"));
        foreach (var (name, configured) in current.Reports)
        {
            if (!string.IsNullOrWhiteSpace(configured.Authorization?.Policy))
                policies.Add((configured.Authorization.Policy.Trim(), $"Report '{name}'"));
        }
        if (policies.Count == 0) return;

        if (services.GetService<IAuthorizationService>() is null)
            throw new InvalidOperationException(
                $"{policies[0].Owner} names authorization policy '{policies[0].Name}' but the host has not registered authorization services (builder.Services.AddAuthorization()).");

        var provider = services.GetService<IAuthorizationPolicyProvider>();
        if (provider is null) return;
        foreach (var (name, owner) in policies)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await provider.GetPolicyAsync(name) is null)
                    logging.Logger?.LogWarning(
                        "{Owner} names authorization policy '{Policy}', which the policy provider does not know at startup. It is evaluated per request; a provider that resolves it later is fine, a typo is not.",
                        owner,
                        name);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logging.Logger?.LogWarning(
                    ex,
                    "{Owner} names authorization policy '{Policy}', which the policy provider could not resolve at startup. It is evaluated per request.",
                    owner,
                    name);
            }
        }
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
