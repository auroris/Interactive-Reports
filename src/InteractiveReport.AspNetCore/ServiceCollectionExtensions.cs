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

/// <summary>Registers the Interactive Reports service graph with an ASP.NET Core host.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers Interactive Reports engine and transport services from one configuration section.
    /// </summary>
    /// <param name="services">The service collection in which to register Interactive Reports dependencies.</param>
    /// <param name="configuration">The application configuration containing Interactive Reports settings.</param>
    /// <param name="sectionName">The configuration section containing <see cref="InteractiveReportOptions"/>.</param>
    /// <returns>A builder for connection, logging, context, user-directory, and authorization integrations.</returns>
    /// <remarks>Mutates <paramref name="services"/> and defers option validation until host startup.</remarks>
    /// <example>
    /// <code><![CDATA[
    /// var reports = builder.Services.AddInteractiveReports(builder.Configuration);
    /// reports.AddConnection("MainDb", sp =>
    ///     new SqlConnection(sp.GetRequiredService<IConfiguration>().GetConnectionString("MainDb")));
    /// ]]></code>
    /// </example>
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
            sp.GetRequiredService<ConfiguredReportDocumentSynchronizer>()));
        services.AddSingleton(sp => new ReportExecutor(
            sp.GetRequiredService<IReportConnectionFactory>(),
            sp.GetRequiredService<SchemaCache>(),
            logging.For<ReportExecutor>()));
        services.AddSingleton<IReportFileExporter, ReportFileExporter>();
        services.AddSingleton<IReportAccessService, ReportAccessService>();
        services.AddSingleton<DefaultReportDocumentService>();
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

/// <summary>Collects host integrations that augment the Interactive Reports service registration.</summary>
public sealed class InteractiveReportBuilder
{
    private readonly IServiceCollection _services;
    private readonly InteractiveReportLogging _logging;

    /// <summary>Gets code-registered connection factories keyed by case-insensitive connection name.</summary>
    internal Dictionary<string, Func<IServiceProvider, DbConnection>> Connections { get; }
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets explicitly registered dialects; other connection dialects are detected from their types.</summary>
    internal Dictionary<string, ReportDialect> ConnectionDialects { get; }
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Creates a builder over the host service collection and package logging coordinator.
    /// </summary>
    /// <param name="services">The service collection in which to register Interactive Reports dependencies.</param>
    /// <param name="logging">The package logging coordinator that receives the host logger.</param>
    internal InteractiveReportBuilder(
        IServiceCollection services,
        InteractiveReportLogging logging)
    {
        _services = services;
        _logging = logging;
    }

    /// <summary>
    /// Sends every Interactive Reports log event to one host-owned logger. Omitting this and the
    /// logger argument on MapInteractiveReports keeps the package silent. The host remains responsible for
    /// levels, providers, destinations, and scopes.
    /// </summary>
    /// <param name="logger">The host-provided logger that receives diagnostic events; <see langword="null"/> disables logging.</param>
    /// <returns>This builder for further registration.</returns>
    /// <remarks>Replaces the current package logging sink.</remarks>
    public InteractiveReportBuilder UseLogger(ILogger logger)
    {
        _logging.Use(logger);
        return this;
    }

    /// <summary>
    /// Maps a definition's named connection to a factory that returns an unopened <see cref="DbConnection"/>.
    /// The SQL dialect is detected from the factory's connection type — one unopened instance is created and
    /// disposed the first time it is needed.
    /// </summary>
    /// <param name="name">The case-insensitive connection name used by report definitions.</param>
    /// <param name="factory">The callback that creates a new unopened connection from the runtime service provider.</param>
    /// <returns>This builder for further registration.</returns>
    /// <remarks>Replaces any existing factory and removes any previously declared dialect for <paramref name="name"/>.</remarks>
    /// <example>
    /// <code><![CDATA[
    /// reports.AddConnection("MainDb", sp => new SqlConnection(connectionString));
    /// ]]></code>
    /// </example>
    public InteractiveReportBuilder AddConnection(string name, Func<IServiceProvider, DbConnection> factory)
    {
        Connections[name] = factory;
        ConnectionDialects.Remove(name); // Provider constraint: re-registration is last-write-wins for the dialect too.
        return this;
    }

    /// <summary>
    /// Registers a connection factory like <see cref="AddConnection(string, Func{IServiceProvider, DbConnection})"/>,
    /// declaring the dialect explicitly — for factories returning wrapper or custom connection types
    /// (profilers, instrumentation) that dialect detection cannot recognize, or whose creation has side
    /// effects detection should not trigger.
    /// </summary>
    /// <param name="name">The case-insensitive connection name used by report definitions.</param>
    /// <param name="factory">The callback that creates a new unopened connection from the runtime service provider.</param>
    /// <param name="dialect">The explicitly declared dialect, bypassing type inspection.</param>
    /// <returns>This builder for further registration.</returns>
    /// <remarks>Replaces any existing factory and declared dialect for <paramref name="name"/>.</remarks>
    public InteractiveReportBuilder AddConnection(
        string name,
        Func<IServiceProvider, DbConnection> factory,
        ReportDialect dialect)
    {
        Connections[name] = factory;
        ConnectionDialects[name] = dialect;
        return this;
    }

    /// <summary>
    /// Replaces the default claims-based context parameter resolver.
    /// </summary>
    /// <typeparam name="TResolver">The singleton resolver implementation.</typeparam>
    /// <returns>This builder for further registration.</returns>
    public InteractiveReportBuilder UseContextParameterResolver<TResolver>()
        where TResolver : class, IContextParameterResolver
    {
        _services.Replace(ServiceDescriptor.Singleton<IContextParameterResolver, TResolver>());
        return this;
    }

    /// <summary>
    /// Registers the application user directory used by administration account selectors.
    /// The provider may be scoped and may return no entries to retain free-form identity entry. It supplies
    /// choices only and does not authorize them.
    /// </summary>
    /// <typeparam name="TProvider">The scoped application user-directory implementation.</typeparam>
    /// <returns>This builder for further registration.</returns>
    public InteractiveReportBuilder UseUserProvider<TProvider>()
        where TProvider : class, IInteractiveReportUserProvider
    {
        _services.Replace(ServiceDescriptor.Scoped<IInteractiveReportUserProvider, TProvider>());
        return this;
    }

    /// <summary>
    /// Adds an application authorization callback. Multiple callbacks and the native
    /// ASP.NET Core adapter compose with AND semantics. Built-in ownership and configured-administrator
    /// rules remain in force.
    /// </summary>
    /// <param name="callback">The host callback invoked for each protected operation.</param>
    /// <returns>This builder for further registration.</returns>
    /// <remarks>Adds a singleton authorizer; it does not replace previously registered authorizers.</remarks>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="callback"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code><![CDATA[
    /// reports.UseAuthorization((request, ct) => ValueTask.FromResult(
    ///     request.Action is InteractiveReportAction.ViewReport or InteractiveReportAction.Query
    ///         ? request.User.IsInRole("ReportingUsers")
    ///         : request.User.IsInRole("ReportAdministrators")));
    /// ]]></code>
    /// </example>
    public InteractiveReportBuilder UseAuthorization(InteractiveReportAuthorizationCallback callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        _services.AddSingleton<IInteractiveReportAuthorizer>(
            new CallbackInteractiveReportAuthorizer(callback));
        return this;
    }

    /// <summary>
    /// Sends each operation through ASP.NET Core resource-based
    /// authorization using InteractiveReportAuthorizationRequirement and
    /// InteractiveReportAuthorizationResource.
    /// </summary>
    /// <returns>This builder for further registration.</returns>
    /// <remarks>Adds authorization services and one authorizer adapter if it is not already registered.</remarks>
    public InteractiveReportBuilder UseAspNetCoreAuthorization()
    {
        _services.AddAuthorization();
        _services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IInteractiveReportAuthorizer,
            AspNetCoreInteractiveReportAuthorizer>());
        return this;
    }
}
// ASP.NET Core composition entrypoint: registration installs the engine, persistence, authorization,
// synchronization, startup validation, and optional host adapters as singleton or scoped services.
// The returned builder collects connection factories and integrations before the provider is built.
