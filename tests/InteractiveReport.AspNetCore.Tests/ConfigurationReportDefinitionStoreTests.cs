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

    [Fact]
    public async Task List_rejects_case_insensitive_duplicate_report_names()
    {
        static ReportDefinition Definition() => new()
        {
            Connection = "db",
            Dialect = ReportDialect.Sqlite,
            Sql = "select 1 as ID",
        };

        var options = new InteractiveReportOptions
        {
            Reports = new Dictionary<string, ReportDefinition>(StringComparer.Ordinal)
            {
                ["orders"] = Definition(),
                ["ORDERS"] = Definition(),
            },
        };
        using var store = new ConfigurationReportDefinitionStore(
            new OptionsMonitorStub(options),
            new SchemaCache());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await store.List());

        Assert.Contains("unique", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Column_labels_round_trip_the_snapshot_with_key_casing_intact()
    {
        var configured = new ReportDefinition
        {
            Connection = "db",
            Dialect = ReportDialect.Sqlite,
            Sql = "select 1 as ORDER_ID",
            ColumnLabels = new() { ["ORDER_ID"] = "Order #" },
        };
        var options = new InteractiveReportOptions();
        options.Reports["orders"] = configured;
        using var store = new ConfigurationReportDefinitionStore(
            new OptionsMonitorStub(options),
            new SchemaCache());

        var snapshot = await store.Find("orders");

        Assert.NotNull(snapshot);
        Assert.NotSame(configured.ColumnLabels, snapshot.ColumnLabels);
        Assert.Contains("ORDER_ID", snapshot.ColumnLabels!.Keys);   // Web JSON options must not camel-case map keys
        Assert.Equal("Order #", snapshot.ColumnLabels["ORDER_ID"]);
    }

    [Theory]
    [InlineData(" ", "Order #", "blank column name")]
    [InlineData("ORDER_ID", " ", "must not be blank")]
    public async Task Blank_column_label_entries_fail_fast(string name, string label, string expected)
    {
        var options = new InteractiveReportOptions();
        options.Reports["orders"] = new ReportDefinition
        {
            Connection = "db",
            Dialect = ReportDialect.Sqlite,
            Sql = "select 1 as ORDER_ID",
            ColumnLabels = new() { [name] = label },
        };
        using var store = new ConfigurationReportDefinitionStore(
            new OptionsMonitorStub(options),
            new SchemaCache());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await store.Find("orders"));

        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public async Task Case_colliding_column_labels_fail_fast()
    {
        var options = new InteractiveReportOptions();
        options.Reports["orders"] = new ReportDefinition
        {
            Connection = "db",
            Dialect = ReportDialect.Sqlite,
            Sql = "select 1 as ORDER_ID",
            ColumnLabels = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ORDER_ID"] = "Order #",
                ["order_id"] = "Ticket",
            },
        };
        using var store = new ConfigurationReportDefinitionStore(
            new OptionsMonitorStub(options),
            new SchemaCache());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await store.Find("orders"));

        Assert.Contains("duplicate column", error.Message);
    }

    [Fact]
    public async Task Find_rejects_out_of_range_chart_point_limits()
    {
        var options = new InteractiveReportOptions();
        options.Reports["orders"] = new ReportDefinition
        {
            Connection = "db",
            Dialect = ReportDialect.Sqlite,
            Sql = "select 1 as ID",
            MaxChartPoints = 0,
        };
        using var store = new ConfigurationReportDefinitionStore(
            new OptionsMonitorStub(options),
            new SchemaCache());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await store.Find("orders"));

        Assert.Contains("maxChartPoints", error.Message);
    }

    private sealed class OptionsMonitorStub(InteractiveReportOptions options)
        : IOptionsMonitor<InteractiveReportOptions>
    {
        public InteractiveReportOptions CurrentValue => options;
        public InteractiveReportOptions Get(string? name) => options;
        public IDisposable? OnChange(Action<InteractiveReportOptions, string?> listener) => null;
    }
}
