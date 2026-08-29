using System.Data.Common;
using InteractiveReport.Core.Authorization;
using InteractiveReport.Core.Definitions;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Export;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.SavedReports;
using InteractiveReport.Core.Schema;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InteractiveReport.AspNetCore;

public static class ServiceCollectionExtensions
{
    public static InteractiveReportBuilder AddInteractiveReports(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName = "InteractiveReport")
    {
        services.Configure<InteractiveReportOptions>(configuration.GetSection(sectionName));

        var logging = new InteractiveReportLogging();
        services.AddSingleton(logging);
        var builder = new InteractiveReportBuilder(services, logging);

        services.AddSingleton(sp => new SchemaCache(logging.For<SchemaCache>()));
        services.AddSingleton<ConfiguredReportDocumentStore>();
        services.AddSingleton(sp => new ReportConnectionRegistry(
            builder.Connections,
            builder.ConnectionDialects,
            sp,
            sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton<IReportConnectionFactory>(sp => sp.GetRequiredService<ReportConnectionRegistry>());
        services.AddSingleton<IReportAuthorizationStore>(sp => new SqlReportAuthorizationStore(
            () =>
            {
                var options = sp.GetRequiredService<IOptionsMonitor<InteractiveReportOptions>>().CurrentValue;
                var saved = sp.GetRequiredService<ReportConnectionRegistry>()
                    .ResolveStoreConfig(options.SavedReports);
                return new ReportAuthorizationStoreConfig(
                    saved.ConnectionName,
                    saved.Dialect,
                    saved.AutoCreate,
                    ReportConnectionRegistry.ResolveTableName(
                        options.SavedReports,
                        options.Authorization.TableName));
            },
            sp.GetRequiredService<IReportConnectionFactory>(),
            logging.For<SqlReportAuthorizationStore>()));
        services.AddSingleton<IReportDefinitionStore>(sp => new ConfigurationReportDefinitionStore(
            sp.GetRequiredService<IOptionsMonitor<InteractiveReportOptions>>(),
            sp.GetRequiredService<SchemaCache>(),
            sp.GetRequiredService<ReportConnectionRegistry>(),
            sp.GetRequiredService<ConfiguredReportDocumentSynchronizer>(),
            sp.GetRequiredService<ISavedReportStore>()));
        services.AddSingleton(sp => new ReportExecutor(
            sp.GetRequiredService<IReportConnectionFactory>(),
            sp.GetRequiredService<SchemaCache>(),
            logging.For<ReportExecutor>()));
        services.AddSingleton<IReportFileExporter, ReportFileExporter>();
        services.AddSingleton<IReportAccessService, ReportAccessService>();
        services.TryAddSingleton<IContextParameterResolver, ClaimContextParameterResolver>();

        services.AddSingleton<ISavedReportStore>(sp => new SqlSavedReportStore(
            () => sp.GetRequiredService<ReportConnectionRegistry>().ResolveStoreConfig(
                sp.GetRequiredService<IOptionsMonitor<InteractiveReportOptions>>().CurrentValue.SavedReports),
            sp.GetRequiredService<IReportConnectionFactory>(),
            logging.For<SqlSavedReportStore>()));
        services.AddSingleton(sp => new ConfiguredReportDocumentSynchronizer(
            sp.GetRequiredService<ConfiguredReportDocumentStore>(),
            sp.GetRequiredService<ISavedReportStore>(),
            sp.GetRequiredService<IOptionsMonitor<InteractiveReportOptions>>(),
            sp.GetRequiredService<ReportConnectionRegistry>(),
            logging.For<ConfiguredReportDocumentSynchronizer>()));
        services.AddHostedService<InteractiveReportStartupValidator>();

        return builder;
    }
}

public sealed class InteractiveReportBuilder
{
    private readonly IServiceCollection _services;
    private readonly InteractiveReportLogging _logging;

    internal Dictionary<string, Func<IServiceProvider, DbConnection>> Connections { get; }
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Dialects declared at registration; connections absent here are sniffed by connection type.</summary>
    internal Dictionary<string, ReportDialect> ConnectionDialects { get; }
        = new(StringComparer.OrdinalIgnoreCase);

    internal InteractiveReportBuilder(
        IServiceCollection services,
        InteractiveReportLogging logging)
    {
        _services = services;
        _logging = logging;
    }

    /// <summary>
    /// Sends every Interactive Reports log event to one host-owned logger. Omitting
    /// this and the logger argument on MapInteractiveReports keeps the package silent.
    /// The host remains responsible for levels, providers, destinations, and scopes.
    /// </summary>
    public InteractiveReportBuilder UseLogger(ILogger logger)
    {
        _logging.Use(logger);
        return this;
    }

    /// <summary>
    /// Maps a definition's named connection to a DbConnection factory (returned
    /// unopened). The SQL dialect is detected from the factory's connection type —
    /// one unopened instance is created and disposed the first time it is needed.
    /// </summary>
    public InteractiveReportBuilder AddConnection(string name, Func<IServiceProvider, DbConnection> factory)
    {
        Connections[name] = factory;
        ConnectionDialects.Remove(name);   // re-registration is last-write-wins for the dialect too
        return this;
    }

    /// <summary>
    /// Like <see cref="AddConnection(string, Func{IServiceProvider, DbConnection})"/>,
    /// declaring the dialect explicitly — for factories returning wrapper or custom
    /// connection types (profilers, instrumentation) that dialect detection cannot
    /// recognize, or whose creation has side effects detection should not trigger.
    /// </summary>
    public InteractiveReportBuilder AddConnection(
        string name,
        Func<IServiceProvider, DbConnection> factory,
        ReportDialect dialect)
    {
        Connections[name] = factory;
        ConnectionDialects[name] = dialect;
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
    /// Registers the application user directory used by administration account
    /// selectors. The provider may be scoped and may return no entries to retain
    /// free-form identity entry. It supplies choices only and does not authorize them.
    /// </summary>
    public InteractiveReportBuilder UseUserProvider<TProvider>()
        where TProvider : class, IInteractiveReportUserProvider
    {
        _services.Replace(ServiceDescriptor.Scoped<IInteractiveReportUserProvider, TProvider>());
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
