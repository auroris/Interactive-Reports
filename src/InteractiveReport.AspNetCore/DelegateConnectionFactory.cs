using System.Data.Common;
using InteractiveReport.Core.Execution;

namespace InteractiveReport.AspNetCore;

internal sealed class DelegateConnectionFactory : IReportConnectionFactory
{
    private readonly IReadOnlyDictionary<string, Func<IServiceProvider, DbConnection>> _factories;
    private readonly IServiceProvider _services;

    public DelegateConnectionFactory(
        IReadOnlyDictionary<string, Func<IServiceProvider, DbConnection>> factories,
        IServiceProvider services)
    {
        _factories = factories;
        _services = services;
    }

    public DbConnection CreateConnection(string name)
    {
        if (!_factories.TryGetValue(name, out var factory))
            throw new InvalidOperationException(
                $"No connection named '{name}' is registered. Register it with AddInteractiveReports(...).AddConnection(\"{name}\", ...).");
        return factory(_services);
    }
}
