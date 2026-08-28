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
/// Resolves every connection a definition can name and the dialect each one implies.
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

    public DbConnection CreateConnection(string name)
    {
        if (_codeFactories.TryGetValue(name, out var factory))
            return factory(_services);
        if (_synthetic.TryGetValue(name, out var entry))
            return ProviderCatalog.CreateConnection(entry.Token, entry.ConnectionString, $"Connection '{name}'");
        throw UnknownConnection(name);
    }

    /// <summary>
    /// Resolves a definition's dataSource to a registered connection name plus its
    /// dialect. <paramref name="owner"/> names the config surface in every error
    /// ("Report 'orders'", "SavedReports").
    /// </summary>
    public (string ConnectionName, ReportDialect Dialect) ResolveDataSource(string owner, string dataSource, string? provider)
    {
        var value = dataSource.Trim();
        string connectionString;
        string? connectionStringName = null;
        if (!value.Contains('='))
        {
            // A bare name references ConnectionStrings — never silently a literal.
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

    /// <summary>Declared at AddConnection → implied by the dataSource token → sniffed from the factory's connection type.</summary>
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
    /// The one mapping from SavedReports options to a concrete store target. The
    /// store, the configured-document synchronizer, and the built-in saved-reports
    /// listing definition must all agree on it. The dialect is always derived.
    /// </summary>
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
            return new SavedReportStoreConfig(name, dialect, saved.AutoCreate, saved.TableName);
        }
        if (hasConnection)
            return new SavedReportStoreConfig(saved.Connection!, ResolveDialect(saved.Connection!), saved.AutoCreate, saved.TableName);
        return new SavedReportStoreConfig(
            ServiceCollectionExtensions.DefaultSavedReportsConnection,
            ReportDialect.Sqlite,
            saved.AutoCreate,
            saved.TableName);
    }

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

    private static InvalidOperationException UnknownConnection(string name) => new(
        $"No connection named '{name}' is registered. Register it with "
        + $"AddInteractiveReports(...).AddConnection(\"{name}\", ...), or give the report a dataSource instead.");
}
