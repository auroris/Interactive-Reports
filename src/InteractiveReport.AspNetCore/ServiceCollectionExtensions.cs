using System.Data.Common;
using InteractiveReport.Core.Definitions;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.SavedReports;
using InteractiveReport.Core.Schema;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace InteractiveReport.AspNetCore;

public static class ServiceCollectionExtensions
{
    /// <summary>Sentinel connection name for the zero-config local SQLite saved-report store.</summary>
    internal const string DefaultSavedReportsConnection = "__ir:saved-reports-default";

    public static InteractiveReportBuilder AddInteractiveReports(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName = "InteractiveReport")
    {
        services.Configure<InteractiveReportOptions>(configuration.GetSection(sectionName));

        var builder = new InteractiveReportBuilder(services);

        // Zero-config default for saved reports: a local SQLite file under App_Data.
        builder.Connections[DefaultSavedReportsConnection] = sp =>
        {
            var env = sp.GetRequiredService<IHostEnvironment>();
            var dir = Path.Combine(env.ContentRootPath, "App_Data");
            Directory.CreateDirectory(dir);
            return new SqliteConnection($"Data Source={Path.Combine(dir, "interactivereport.saved.db")}");
        };

        services.AddSingleton<SchemaCache>();
        services.AddSingleton<ConfiguredReportDocumentStore>();
        services.AddSingleton<IReportDefinitionStore, ConfigurationReportDefinitionStore>();
        services.AddSingleton<IReportConnectionFactory>(sp => new DelegateConnectionFactory(builder.Connections, sp));
        services.AddSingleton<ReportExecutor>();
        services.TryAddSingleton<IContextParameterResolver, ClaimContextParameterResolver>();

        services.AddSingleton<ISavedReportStore>(sp => new SqlSavedReportStore(
            () => ResolveStoreConfig(
                sp.GetRequiredService<IOptionsMonitor<InteractiveReportOptions>>().CurrentValue.SavedReports),
            sp.GetRequiredService<IReportConnectionFactory>()));
        services.AddSingleton<ConfiguredReportDocumentSynchronizer>();

        return builder;
    }

    /// <summary>
    /// The one mapping from SavedReports options to a concrete store target. The
    /// store, the configured-document synchronizer, and the built-in saved-reports
    /// listing definition must all agree on it.
    /// </summary>
    internal static SavedReportStoreConfig ResolveStoreConfig(SavedReportsOptions saved)
        => saved.Connection is null
            ? new SavedReportStoreConfig(DefaultSavedReportsConnection, Core.Model.ReportDialect.Sqlite, saved.AutoCreate, saved.TableName)
            : new SavedReportStoreConfig(saved.Connection, saved.Dialect, saved.AutoCreate, saved.TableName);
}

public sealed class InteractiveReportBuilder
{
    private readonly IServiceCollection _services;

    internal Dictionary<string, Func<IServiceProvider, DbConnection>> Connections { get; }
        = new(StringComparer.OrdinalIgnoreCase);

    internal InteractiveReportBuilder(IServiceCollection services) => _services = services;

    /// <summary>Maps a definition's named connection to a DbConnection factory (returned unopened).</summary>
    public InteractiveReportBuilder AddConnection(string name, Func<IServiceProvider, DbConnection> factory)
    {
        Connections[name] = factory;
        return this;
    }

    /// <summary>Replaces the default claims-based context parameter resolver.</summary>
    public InteractiveReportBuilder UseContextParameterResolver<TResolver>()
        where TResolver : class, IContextParameterResolver
    {
        _services.Replace(ServiceDescriptor.Singleton<IContextParameterResolver, TResolver>());
        return this;
    }

    /// <summary>
    /// Adds an application authorization callback. Multiple callbacks and the native
    /// ASP.NET Core adapter compose with AND semantics. Built-in ownership and
    /// configured-administrator rules remain in force.
    /// </summary>
    public InteractiveReportBuilder UseAuthorization(InteractiveReportAuthorizationCallback callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        _services.AddSingleton<IInteractiveReportAuthorizer>(
            new CallbackInteractiveReportAuthorizer(callback));
        return this;
    }

    /// <summary>
    /// Sends each operation through ASP.NET Core resource-based authorization using
    /// InteractiveReportAuthorizationRequirement and
    /// InteractiveReportAuthorizationResource.
    /// </summary>
    public InteractiveReportBuilder UseAspNetCoreAuthorization()
    {
        _services.AddAuthorization();
        _services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IInteractiveReportAuthorizer,
            AspNetCoreInteractiveReportAuthorizer>());
        return this;
    }
}
