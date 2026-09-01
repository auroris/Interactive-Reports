using System.Data.Common;
using InteractiveReport.Core.Model;
using Microsoft.Extensions.Configuration;

namespace InteractiveReport.AspNetCore.Tests;

public sealed class OracleVersionDetectionTests
{
    [Fact]
    public void SetDetectedDialect_overrides_declared_dialect_resolution()
    {
        var connectionName = "CustomConn";
        var factories = new Dictionary<string, Func<IServiceProvider, DbConnection>>(StringComparer.OrdinalIgnoreCase);
        var declaredDialects = new Dictionary<string, ReportDialect>(StringComparer.OrdinalIgnoreCase)
        {
            [connectionName] = ReportDialect.Oracle,
        };
        var registry = new ReportConnectionRegistry(factories, declaredDialects, NullServices.Instance, new ConfigurationBuilder().Build());

        Assert.Equal(ReportDialect.Oracle, registry.ResolveDialect(connectionName));

        registry.SetDetectedDialect(connectionName, ReportDialect.Oracle11g);
        Assert.Equal(ReportDialect.Oracle11g, registry.ResolveDialect(connectionName));
    }

    private sealed class NullServices : IServiceProvider
    {
        public static readonly NullServices Instance = new();
        public object? GetService(Type serviceType) => null;
    }
}
