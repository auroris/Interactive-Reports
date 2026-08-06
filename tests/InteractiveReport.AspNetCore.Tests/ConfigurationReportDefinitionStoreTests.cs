using InteractiveReport.Core.Model;
using InteractiveReport.Core.Schema;
using Microsoft.Extensions.Options;

namespace InteractiveReport.AspNetCore.Tests;

public sealed class ConfigurationReportDefinitionStoreTests
{
    [Fact]
    public async Task Find_returns_a_detached_snapshot_without_mutating_options()
    {
        var configured = new ReportDefinition
        {
            Connection = "db",
            Dialect = ReportDialect.Sqlite,
            Sql = "select 1 as ID",
            DefaultState = new ReportState
            {
                Search = "configured",
                Filters = [new FilterRule { Expr = "ID = 1" }],
            },
        };
        var options = new InteractiveReportOptions();
        options.Reports["orders"] = configured;
        using var store = new ConfigurationReportDefinitionStore(
            new OptionsMonitorStub(options),
            new SchemaCache());

        var snapshot = await store.Find("orders");

        Assert.NotNull(snapshot);
        Assert.Equal("", configured.Name);
        Assert.Equal("orders", snapshot.Name);
        Assert.NotSame(configured, snapshot);
        Assert.NotSame(configured.DefaultState, snapshot.DefaultState);
        Assert.NotSame(configured.DefaultState!.Filters, snapshot.DefaultState!.Filters);

        snapshot.DefaultState.Search = "changed";
        snapshot.DefaultState.Filters![0].Expr = "ID = 2";
        Assert.Equal("configured", configured.DefaultState.Search);
        Assert.Equal("ID = 1", configured.DefaultState.Filters![0].Expr);
    }

    private sealed class OptionsMonitorStub(InteractiveReportOptions options)
        : IOptionsMonitor<InteractiveReportOptions>
    {
        public InteractiveReportOptions CurrentValue => options;
        public InteractiveReportOptions Get(string? name) => options;
        public IDisposable? OnChange(Action<InteractiveReportOptions, string?> listener) => null;
    }
}
