using System.Data.Common;
using InteractiveReport.Core.Definitions;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;
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
        // The dialect is declared, never sniffed — the factory has a directory-creating
        // side effect that dialect detection must not trigger.
        builder.Connections[DefaultSavedReportsConnection] = sp =>
        {
            var env = sp.GetRequiredService<IHostEnvironment>();
            var dir = Path.Combine(env.ContentRootPath, "App_Data");
            Directory.CreateDirectory(dir);
            return new SqliteConnection($"Data Source={Path.Combine(dir, "interactivereport.saved.db")}");
        };
        builder.ConnectionDialects[DefaultSavedReportsConnection] = ReportDialect.Sqlite;

        services.AddSingleton<SchemaCache>();
        services.AddSingleton<ConfiguredReportDocumentStore>();
        services.AddSingleton(sp => new ReportConnectionRegistry(
            builder.Connections,
            builder.ConnectionDialects,
            sp,
            sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton<IReportConnectionFactory>(sp => sp.GetRequiredService<ReportConnectionRegistry>());
        services.AddSingleton<IReportDefinitionStore>(sp => new ConfigurationReportDefinitionStore(
            sp.GetRequiredService<IOptionsMonitor<InteractiveReportOptions>>(),
            sp.GetRequiredService<SchemaCache>(),
            sp.GetRequiredService<ReportConnectionRegistry>(),
            sp.GetRequiredService<ConfiguredReportDocumentSynchronizer>(),
            sp.GetRequiredService<ISavedReportStore>()));
        services.AddSingleton<ReportExecutor>();
        services.TryAddSingleton<IContextParameterResolver, ClaimContextParameterResolver>();

        services.AddSingleton<ISavedReportStore>(sp => new SqlSavedReportStore(
            () => sp.GetRequiredService<ReportConnectionRegistry>().ResolveStoreConfig(
                sp.GetRequiredService<IOptionsMonitor<InteractiveReportOptions>>().CurrentValue.SavedReports),
            sp.GetRequiredService<IReportConnectionFactory>()));
        services.AddSingleton(sp => new ConfiguredReportDocumentSynchronizer(
            sp.GetRequiredService<ConfiguredReportDocumentStore>(),
            sp.GetRequiredService<ISavedReportStore>(),
            sp.GetRequiredService<IOptionsMonitor<InteractiveReportOptions>>(),
            sp.GetRequiredService<ReportConnectionRegistry>()));
        services.AddHostedService<InteractiveReportStartupValidator>();

        return builder;
    }
}

public sealed class InteractiveReportBuilder
{
    private readonly IServiceCollection _services;

    internal Dictionary<string, Func<IServiceProvider, DbConnection>> Connections { get; }
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Dialects declared at registration; connections absent here are sniffed by connection type.</summary>
    internal Dictionary<string, ReportDialect> ConnectionDialects { get; }
        = new(StringComparer.OrdinalIgnoreCase);

    internal InteractiveReportBuilder(IServiceCollection services) => _services = services;

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
