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
            // The config-bound default state is a v3 pipeline document.
            DefaultState = new ReportState
            {
                Search = "configured",
                Pipeline =
                [
                    new PipelineStage
                    {
                        Shape = new StageShape { Kind = "source" },
                        Layer = new StageLayer { Filters = [new FilterRule { Expr = "ID = 1" }] },
                    },
                ],
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
        Assert.NotSame(configured.DefaultState!.Pipeline, snapshot.DefaultState!.Pipeline);
        Assert.Equal("source", snapshot.DefaultState.Pipeline![0].Shape!.Kind);

        snapshot.DefaultState.Search = "changed";
        snapshot.DefaultState.Pipeline[0].Layer!.Filters![0].Expr = "ID = 2";
        Assert.Equal("configured", configured.DefaultState.Search);
        Assert.Equal("ID = 1", configured.DefaultState.Pipeline![0].Layer!.Filters![0].Expr);
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

    [Theory]
    [InlineData("teleport", "unknown feature 'teleport'")]
    [InlineData(" ", "blank entry")]
    public async Task Invalid_feature_entries_fail_fast(string feature, string expected)
    {
        var options = new InteractiveReportOptions();
        options.Reports["orders"] = new ReportDefinition
        {
            Connection = "db",
            Dialect = ReportDialect.Sqlite,
            Sql = "select 1 as ID",
            Features = ["search", feature],
        };
        using var store = new ConfigurationReportDefinitionStore(
            new OptionsMonitorStub(options),
            new SchemaCache());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await store.Find("orders"));

        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public async Task Case_colliding_feature_entries_fail_fast()
    {
        var options = new InteractiveReportOptions();
        options.Reports["orders"] = new ReportDefinition
        {
            Connection = "db",
            Dialect = ReportDialect.Sqlite,
            Sql = "select 1 as ID",
            Features = ["download", "DOWNLOAD"],
        };
        using var store = new ConfigurationReportDefinitionStore(
            new OptionsMonitorStub(options),
            new SchemaCache());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await store.Find("orders"));

        Assert.Contains("duplicate entry 'download'", error.Message);
    }

    [Fact]
    public async Task Feature_entries_match_case_insensitively_and_resolve_canonically()
    {
        var options = new InteractiveReportOptions();
        options.Reports["orders"] = new ReportDefinition
        {
            Connection = "db",
            Dialect = ReportDialect.Sqlite,
            Sql = "select 1 as ID",
            Features = ["SEARCH", "controlbreak"],
        };
        using var store = new ConfigurationReportDefinitionStore(
            new OptionsMonitorStub(options),
            new SchemaCache());

        var snapshot = await store.Find("orders");

        Assert.NotNull(snapshot);
        Assert.True(ReportFeatures.IsEnabled(snapshot, ReportFeatures.Search));
        Assert.True(ReportFeatures.IsEnabled(snapshot, ReportFeatures.ControlBreak));
        Assert.False(ReportFeatures.IsEnabled(snapshot, ReportFeatures.Download));
        Assert.Equal(
            [ReportFeatures.Search, ReportFeatures.ControlBreak],
            ReportFeatures.Resolve(snapshot));
    }

    [Fact]
    public async Task An_absent_feature_list_enables_everything()
    {
        var options = new InteractiveReportOptions();
        options.Reports["orders"] = new ReportDefinition
        {
            Connection = "db",
            Dialect = ReportDialect.Sqlite,
            Sql = "select 1 as ID",
        };
        using var store = new ConfigurationReportDefinitionStore(
            new OptionsMonitorStub(options),
            new SchemaCache());

        var snapshot = await store.Find("orders");

        Assert.NotNull(snapshot);
        Assert.Null(snapshot.Features);
        Assert.Equal(ReportFeatures.All, ReportFeatures.Resolve(snapshot));
    }

    [Theory]
    [InlineData(" ", "must not be blank")]
    [InlineData("javascript:alert(1)", "must use http or https")]
    [InlineData("file:///tmp/report.css", "must use http or https")]
    public async Task Invalid_stylesheet_urls_fail_fast(string styleSheet, string expected)
    {
        var options = new InteractiveReportOptions();
        options.Reports["orders"] = new ReportDefinition
        {
            Connection = "db",
            Dialect = ReportDialect.Sqlite,
            Sql = "select 1 as ID",
            StyleSheet = styleSheet,
        };
        using var store = new ConfigurationReportDefinitionStore(
            new OptionsMonitorStub(options),
            new SchemaCache());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await store.Find("orders"));

        Assert.Contains(expected, error.Message);
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

    [Fact]
    public async Task Builtin_saved_reports_name_is_reserved_and_unavailable_without_a_synchronizer()
    {
        var options = new InteractiveReportOptions();
        using var store = new ConfigurationReportDefinitionStore(
            new OptionsMonitorStub(options),
            new SchemaCache());

        // The internal test constructor wires no synchronizer: the built-in is
        // absent rather than synthesized against a store nobody prepared.
        Assert.Null(await store.Find("__saved-reports"));

        options.Reports["__saved-reports"] = new ReportDefinition
        {
            Connection = "db",
            Dialect = ReportDialect.Sqlite,
            Sql = "select 1",
        };
        var reserved = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await store.Find("__saved-reports"));
        Assert.Contains("reserved", reserved.Message);
    }

    [Fact]
    public async Task Reserved_prefix_and_contradictory_authorization_fail_fast()
    {
        var options = new InteractiveReportOptions();
        options.Reports["__mine"] = new ReportDefinition
        {
            Connection = "db",
            Dialect = ReportDialect.Sqlite,
            Sql = "select 1",
        };
        using var store = new ConfigurationReportDefinitionStore(
            new OptionsMonitorStub(options),
            new SchemaCache());
        var reserved = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await store.Find("__mine"));
        Assert.Contains("reserved for built-in", reserved.Message);

        var contradictory = new InteractiveReportOptions();
        contradictory.Reports["orders"] = new ReportDefinition
        {
            Connection = "db",
            Dialect = ReportDialect.Sqlite,
            Sql = "select 1",
            Authorization = new ReportAuthorization { AllowAnonymous = true, AdministratorsOnly = true },
        };
        using var conflicted = new ConfigurationReportDefinitionStore(
            new OptionsMonitorStub(contradictory),
            new SchemaCache());
        var conflict = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await conflicted.Find("orders"));
        Assert.Contains("allowAnonymous and administratorsOnly", conflict.Message);
    }

    [Fact]
    public void Builtin_definition_synthesizes_from_the_saved_reports_options()
    {
        var definition = SavedReportsListingDefinition.Create(new SavedReportsOptions());

        Assert.Equal("__saved-reports", definition.Name);
        Assert.Equal(ServiceCollectionExtensions.DefaultSavedReportsConnection, definition.Connection);
        Assert.Equal(ReportDialect.Sqlite, definition.Dialect);
        Assert.True(definition.Authorization!.AdministratorsOnly);
        Assert.False(definition.Authorization.AllowAnonymous);
        Assert.Null(definition.Features);

        var layer = definition.DefaultState!.Pipeline![0].Layer!;
        Assert.DoesNotContain("ID", layer.Columns!);
        Assert.Equal(
            ["toggleGlobal", "togglePrimary", "reassign", "openState", "download", "delete"],
            new[] { "ACTION_PUBLISH", "ACTION_PRIMARY", "ACTION_REASSIGN", "ACTION_STATE", "ACTION_DOWNLOAD", "ACTION_DELETE" }
                .Select(column => layer.Formats![column].Command));
        Assert.All(layer.Formats!.Values, format =>
        {
            Assert.Equal("action", format.DisplayAs);
            Assert.Equal("ID", format.KeyColumn);
        });

        var explicitTarget = SavedReportsListingDefinition.Create(new SavedReportsOptions
        {
            Connection = "ReportsDb",
            Dialect = ReportDialect.SqlServer,
            TableName = "SAVED",
        });
        Assert.Equal("ReportsDb", explicitTarget.Connection);
        Assert.Equal(ReportDialect.SqlServer, explicitTarget.Dialect);
        Assert.Contains("FROM SAVED", explicitTarget.Sql);
        Assert.Contains("SUBSTRING", explicitTarget.Sql);

        Assert.Throws<InvalidOperationException>(() => SavedReportsListingDefinition.Create(
            new SavedReportsOptions { Connection = "x", TableName = "bad name" }));
    }

    [Theory]
    [InlineData(ReportDialect.Sqlite)]
    [InlineData(ReportDialect.SqlServer)]
    [InlineData(ReportDialect.Oracle)]
    [InlineData(ReportDialect.Postgres)]
    public void Builtin_sql_is_a_plain_select_safe_for_raw_composition(ReportDialect dialect)
    {
        var definition = SavedReportsListingDefinition.Create(new SavedReportsOptions
        {
            Connection = "ReportsDb",
            Dialect = dialect,
        });
        var sql = definition.Sql;

        // The composer wraps this text as a derived table and SqlKata rewrites
        // bracket/brace characters even inside raw SQL — none may appear, and a
        // top-level ORDER BY would break SQL Server.
        Assert.DoesNotContain("[", sql);
        Assert.DoesNotContain("{", sql);
        Assert.DoesNotContain("ORDER BY", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("IR_SAVED_REPORTS", sql);
        foreach (var column in new[]
        {
            "SCOPE", "PRIMARY_STATUS", "MODIFIED", "ACTION_PUBLISH", "ACTION_PRIMARY", "ACTION_REASSIGN",
            "ACTION_STATE", "ACTION_DOWNLOAD", "ACTION_DELETE",
        })
            Assert.Contains(column, sql);
        if (dialect == ReportDialect.Postgres)
            Assert.Contains("FROM \"IR_SAVED_REPORTS\"", sql);
    }

    private sealed class OptionsMonitorStub(InteractiveReportOptions options)
        : IOptionsMonitor<InteractiveReportOptions>
    {
        public InteractiveReportOptions CurrentValue => options;
        public InteractiveReportOptions Get(string? name) => options;
        public IDisposable? OnChange(Action<InteractiveReportOptions, string?> listener) => null;
    }
}
