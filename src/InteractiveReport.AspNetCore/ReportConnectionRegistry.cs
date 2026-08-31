using System.Collections.Concurrent;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.SavedReports;
using Microsoft.Extensions.Configuration;

namespace InteractiveReport.AspNetCore;

/// <summary>
/// Report connection registry: resolves every connection a definition can name and the dialect each one implies.
/// Two populations share the namespace: code-registered factories (AddConnection —
/// the dictionaries are captured by reference, so late registrations still work) and
/// synthesized entries minted from report dataSource values. Dialects are derived,
/// never configured: declared at AddConnection, implied by a dataSource's provider
/// token, or sniffed from a factory's connection type — one unopened instance,
/// zero I/O. Sniff results cache permanently: they depend on the factory's code,
/// which no configuration reload can change, and synthesized entries are
/// content-addressed (a ConnectionStrings edit mints a new name, which also rolls
/// the schema cache key), so nothing here needs invalidation.
/// </summary>
internal sealed class ReportConnectionRegistry : IReportConnectionFactory
{
    private sealed record SyntheticEntry(string Token, string ConnectionString, ReportDialect Dialect);

    private readonly IReadOnlyDictionary<string, Func<IServiceProvider, DbConnection>> _codeFactories;
    private readonly IReadOnlyDictionary<string, ReportDialect> _declaredDialects;
    private readonly IServiceProvider _services;
    private readonly IConfiguration _configuration;
    private readonly ConcurrentDictionary<string, SyntheticEntry> _synthetic = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ReportDialect> _sniffed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes the registry over the live code-registration dictionaries and application configuration.
    /// </summary>
    /// <param name="codeFactories">Connection factories keyed by their report-facing names.</param>
    /// <param name="declaredDialects">The dialects explicitly associated with registered connections.</param>
    /// <param name="services">The service provider passed to code-registered connection factories.</param>
    /// <param name="configuration">The application configuration containing named connection strings.</param>
    public ReportConnectionRegistry(
        IReadOnlyDictionary<string, Func<IServiceProvider, DbConnection>> codeFactories,
        IReadOnlyDictionary<string, ReportDialect> declaredDialects,
        IServiceProvider services,
        IConfiguration configuration)
    {
        _codeFactories = codeFactories;
        _declaredDialects = declaredDialects;
        _services = services;
        _configuration = configuration;
    }

    /// <summary>
    /// Creates an unopened report connection from the resolved provider configuration.
    /// </summary>
    /// <param name="name">The registered or synthesized connection name.</param>
    /// <returns>A new unopened connection owned by the caller.</returns>
    /// <exception cref="InvalidOperationException">Thrown when <paramref name="name"/> is unknown.</exception>
    public DbConnection CreateConnection(string name)
    {
        if (_codeFactories.TryGetValue(name, out var factory))
            return factory(_services);
        if (_synthetic.TryGetValue(name, out var entry))
            return ProviderCatalog.CreateConnection(entry.Token, entry.ConnectionString, $"Connection '{name}'");
        throw UnknownConnection(name);
    }

    /// <summary>
    /// Resolves a definition's data source to a synthesized connection name plus its dialect.
    /// <paramref name="owner"/> names the config surface in every error ("Report 'orders'", "SavedReports").
    /// </summary>
    /// <param name="owner">The configuration surface named in validation errors, such as a report name or <c>SavedReports</c>.</param>
    /// <param name="dataSource">A <c>ConnectionStrings</c> key or literal ADO.NET connection string.</param>
    /// <param name="provider">An optional provider token; required unless it can be inferred from named-connection metadata.</param>
    /// <returns>The content-addressed synthetic connection name and its SQL dialect.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the connection string or provider configuration cannot be resolved.</exception>
    /// <remarks>Caches the resolved connection material under the returned synthetic name; it does not open a connection.</remarks>
    public (string ConnectionName, ReportDialect Dialect) ResolveDataSource(string owner, string dataSource, string? provider)
    {
        var value = dataSource.Trim();
        string connectionString;
        string? connectionStringName = null;
        if (!value.Contains('='))
        {
            // Invariant: a bare name references ConnectionStrings — never silently a literal.
            connectionStringName = value;
            connectionString = _configuration.GetConnectionString(connectionStringName)
                ?? throw new InvalidOperationException(
                    $"{owner}: dataSource '{connectionStringName}' has no ConnectionStrings:{connectionStringName} entry — "
                    + "add one, or use a literal connection string (key=value pairs).");
        }
        else
        {
            connectionString = value;
        }

        var token = ResolveProviderToken(owner, provider, connectionStringName);
        ProviderCatalog.TryGetDialect(token, out var dialect);

        var name = "__ir:ds:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{token}|{connectionString}")))[..16].ToLowerInvariant();
        _synthetic.TryAdd(name, new SyntheticEntry(token, connectionString, dialect));
        return (name, dialect);
    }

    /// <summary>
    /// Resolves a dialect declared at registration, implied by a data-source provider, or sniffed from the
    /// factory's connection type.
    /// </summary>
    /// <param name="name">The registered or synthesized connection name.</param>
    /// <returns>The SQL dialect used to compile reports for the connection.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the connection is unknown or its provider type is unsupported.</exception>
    /// <remarks>The first sniff of a code-registered factory creates and disposes one unopened connection, then caches the result.</remarks>
    public ReportDialect ResolveDialect(string name)
    {
        if (_declaredDialects.TryGetValue(name, out var declared))
            return declared;
        if (_synthetic.TryGetValue(name, out var entry))
            return entry.Dialect;
        if (_codeFactories.TryGetValue(name, out var factory))
            return _sniffed.GetOrAdd(name, _ => SniffDialect(name, factory));
        throw UnknownConnection(name);
    }

    /// <summary>
    /// Maps saved-report options to the single concrete store target used by persistence, synchronization,
    /// and the built-in listing report. The
    /// store, the configured-document synchronizer, and the built-in saved-reports listing definition must
    /// all agree on it. The dialect is always derived.
    /// </summary>
    /// <param name="saved">The saved-report storage options to validate and resolve.</param>
    /// <returns>The connection name, derived dialect, auto-create policy, and validated table name.</returns>
    /// <exception cref="InvalidOperationException">Thrown when storage is missing, contradictory, or uses invalid provider/table configuration.</exception>
    public SavedReportStoreConfig ResolveStoreConfig(SavedReportsOptions saved)
    {
        var hasDataSource = !string.IsNullOrWhiteSpace(saved.DataSource);
        var hasConnection = !string.IsNullOrWhiteSpace(saved.Connection);
        if (hasDataSource && hasConnection)
            throw new InvalidOperationException(
                "SavedReports: set dataSource or connection, not both.");
        if (hasDataSource)
        {
            var (name, dialect) = ResolveDataSource("SavedReports", saved.DataSource!, saved.Provider);
            return new SavedReportStoreConfig(
                name,
                dialect,
                saved.AutoCreate,
                ResolveTableName(saved, saved.TableName));
        }
        if (hasConnection)
        {
            if (!string.IsNullOrWhiteSpace(saved.Provider))
                throw new InvalidOperationException(
                    "SavedReports: provider applies to dataSource; remove it when using connection.");
            return new SavedReportStoreConfig(
                saved.Connection!,
                ResolveDialect(saved.Connection!),
                saved.AutoCreate,
                ResolveTableName(saved, saved.TableName));
        }
        if (!string.IsNullOrWhiteSpace(saved.Provider))
            throw new InvalidOperationException(
                "SavedReports: provider applies to dataSource; configure dataSource or remove provider.");
        throw new InvalidOperationException(
            "Saved-report storage is not configured. Set InteractiveReport:SavedReports:DataSource "
            + "to a ConnectionStrings name or literal connection string, or set "
            + "InteractiveReport:SavedReports:Connection to a database registered with AddConnection.");
    }

    /// <summary>
    /// Determines whether saved-report persistence has enough configuration to be enabled.
    /// </summary>
    /// <param name="saved">The saved-report storage options to inspect.</param>
    /// <returns><see langword="true"/> when a persistence store is configured; otherwise, <see langword="false"/>.</returns>
    internal static bool IsStoreConfigured(SavedReportsOptions saved)
        => !string.IsNullOrWhiteSpace(saved.DataSource)
            || !string.IsNullOrWhiteSpace(saved.Connection);

    /// <summary>
    /// Applies the configured table prefix and validates the resulting SQL identifier.
    /// </summary>
    /// <param name="saved">The options supplying the optional table prefix.</param>
    /// <param name="baseName">The unprefixed table name.</param>
    /// <returns>The prefixed, validated table identifier.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the base or combined table name is unsafe.</exception>
    internal static string ResolveTableName(SavedReportsOptions saved, string baseName)
    {
        if (string.IsNullOrWhiteSpace(baseName))
            throw new InvalidOperationException("Saved-report table base names must not be empty.");
        var prefix = saved.TablePrefix ?? "";
        return SavedReportStoreConfig.EnsureValidTableName(prefix + baseName);
    }

    /// <summary>
    /// Resolves an explicit provider or named-connection provider invariant to a canonical token.
    /// </summary>
    /// <param name="owner">The configuration surface named in validation errors.</param>
    /// <param name="provider">The optional explicit provider token.</param>
    /// <param name="connectionStringName">The optional <c>ConnectionStrings</c> key used to find provider metadata.</param>
    /// <returns>The canonical provider token.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no provider is available or the supplied provider is unsupported.</exception>
    private string ResolveProviderToken(string owner, string? provider, string? connectionStringName)
    {
        if (!string.IsNullOrWhiteSpace(provider))
        {
            return ProviderCatalog.CanonicalToken(provider.Trim())
                ?? throw new InvalidOperationException(
                    $"{owner}: unknown provider '{provider.Trim()}' (known: {ProviderCatalog.TokenList}).");
        }

        if (connectionStringName is not null
            && _configuration[$"ConnectionStrings:{connectionStringName}_ProviderName"] is { } invariant
            && !string.IsNullOrWhiteSpace(invariant))
        {
            return ProviderCatalog.TokenForInvariantName(invariant)
                ?? throw new InvalidOperationException(
                    $"{owner}: ConnectionStrings:{connectionStringName}_ProviderName is '{invariant.Trim()}', which is not a recognized "
                    + $"ADO.NET provider (recognized: {ProviderCatalog.SupportedInvariantNames}). Set provider explicitly "
                    + $"({ProviderCatalog.TokenList}).");
        }

        throw new InvalidOperationException(
            $"{owner}: set provider to one of {ProviderCatalog.TokenList}"
            + (connectionStringName is not null
                ? $" (or add a ConnectionStrings '{connectionStringName}_ProviderName' entry)."
                : "."));
    }

    /// <summary>
    /// Detects a dialect from the concrete connection type produced by a code-registered factory.
    /// </summary>
    /// <param name="name">The registered name used in diagnostic messages.</param>
    /// <param name="factory">The factory from which to obtain one connection for type inspection.</param>
    /// <returns>The dialect mapped from the connection's runtime type.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the factory fails or the connection type is unsupported.</exception>
    /// <remarks>Creates and disposes one connection without opening it.</remarks>
    private ReportDialect SniffDialect(string name, Func<IServiceProvider, DbConnection> factory)
    {
        DbConnection connection;
        try
        {
            connection = factory(_services);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Connection '{name}': the connection factory failed while detecting the dialect — {ex.Message}", ex);
        }
        using (connection)
        {
            return ProviderCatalog.FromConnectionType(connection.GetType())
                ?? throw new InvalidOperationException(
                    $"Connection '{name}' creates a {connection.GetType().FullName}, which is not a recognized ADO.NET "
                    + $"provider connection. Declare its dialect: AddConnection(\"{name}\", factory, ReportDialect.…).");
        }
    }

    /// <summary>
    /// Creates the configuration error for an unregistered report connection.
    /// </summary>
    /// <param name="name">The unregistered connection name included in the message.</param>
    /// <returns>An exception explaining how to register or replace the missing connection.</returns>
    private static InvalidOperationException UnknownConnection(string name) => new(
        $"No connection named '{name}' is registered. Register it with "
        + $"AddInteractiveReports(...).AddConnection(\"{name}\", ...), or give the report a dataSource instead.");
}
