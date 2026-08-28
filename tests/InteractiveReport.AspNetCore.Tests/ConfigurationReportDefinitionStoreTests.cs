using System.Data.Common;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.SavedReports;
using InteractiveReport.Core.Schema;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace InteractiveReport.AspNetCore.Tests;

public sealed class ConfigurationReportDefinitionStoreTests
{
    /// <summary>A registry with one declared connection, "db" — what every fixture definition names.</summary>
    private static ReportConnectionRegistry TestRegistry()
    {
        var factories = new Dictionary<string, Func<IServiceProvider, DbConnection>>(StringComparer.OrdinalIgnoreCase)
        {
            ["db"] = _ => new SqliteConnection("Data Source=:memory:"),
        };
        var dialects = new Dictionary<string, ReportDialect>(StringComparer.OrdinalIgnoreCase)
        {
            ["db"] = ReportDialect.Sqlite,
        };
        return new ReportConnectionRegistry(
            factories, dialects, NullServices.Instance, new ConfigurationBuilder().Build());
    }

    private sealed class NullServices : IServiceProvider
    {
        public static readonly NullServices Instance = new();
        public object? GetService(Type serviceType) => null;
    }

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
            new SchemaCache(), TestRegistry());

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
            new SchemaCache(), TestRegistry());

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
            new SchemaCache(), TestRegistry());

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
            new SchemaCache(), TestRegistry());

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
            new SchemaCache(), TestRegistry());

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
            new SchemaCache(), TestRegistry());

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
            new SchemaCache(), TestRegistry());

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
            new SchemaCache(), TestRegistry());

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
            new SchemaCache(), TestRegistry());

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
            new SchemaCache(), TestRegistry());

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
            new SchemaCache(), TestRegistry());

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
            new SchemaCache(), TestRegistry());
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
            new SchemaCache(), TestRegistry());
        var conflict = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await conflicted.Find("orders"));
        Assert.Contains("allowAnonymous and administratorsOnly", conflict.Message);
    }

    [Fact]
    public void Named_user_authorization_rejects_conflicting_or_ambiguous_configuration()
    {
        static ReportDefinition Definition(ReportAuthorization authorization) => new()
        {
            Name = "orders",
            Connection = "db",
            Dialect = ReportDialect.Sqlite,
            Sql = "select 1",
            Authorization = authorization,
        };

        ConfigurationReportDefinitionStore.Validate(Definition(new ReportAuthorization
        {
            Restricted = true,
            Users = ["alice", "bob"],
        }));

        var anonymous = Assert.Throws<InvalidOperationException>(() =>
            ConfigurationReportDefinitionStore.Validate(Definition(new ReportAuthorization
            {
                AllowAnonymous = true,
                Restricted = true,
            })));
        Assert.Contains("allowAnonymous and restricted", anonymous.Message);

        var administrators = Assert.Throws<InvalidOperationException>(() =>
            ConfigurationReportDefinitionStore.Validate(Definition(new ReportAuthorization
            {
                AdministratorsOnly = true,
                Users = ["alice"],
            })));
        Assert.Contains("users cannot be combined with administratorsOnly", administrators.Message);

        var duplicates = Assert.Throws<InvalidOperationException>(() =>
            ConfigurationReportDefinitionStore.Validate(Definition(new ReportAuthorization
            {
                Users = ["alice", " ALICE "],
            })));
        Assert.Contains("duplicate identity", duplicates.Message);
    }

    [Fact]
    public void Builtin_definition_synthesizes_from_the_resolved_store_config()
    {
        var definition = SavedReportsListingDefinition.Create(new SavedReportStoreConfig(
            "ReportsDb", ReportDialect.Sqlite));

        Assert.Equal("__saved-reports", definition.Name);
        Assert.Equal("ReportsDb", definition.Connection);
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

        var explicitTarget = SavedReportsListingDefinition.Create(new SavedReportStoreConfig(
            "ReportsDb", ReportDialect.SqlServer, TableName: "SAVED"));
        Assert.Equal("ReportsDb", explicitTarget.Connection);
        Assert.Equal(ReportDialect.SqlServer, explicitTarget.Dialect);
        Assert.Contains("FROM SAVED", explicitTarget.Sql);
        Assert.Contains("SUBSTRING", explicitTarget.Sql);

        Assert.Throws<InvalidOperationException>(() => SavedReportsListingDefinition.Create(
            new SavedReportStoreConfig("x", ReportDialect.Sqlite, TableName: "bad name")));
    }

    [Theory]
    [InlineData(ReportDialect.Sqlite)]
    [InlineData(ReportDialect.SqlServer)]
    [InlineData(ReportDialect.Oracle)]
    [InlineData(ReportDialect.Postgres)]
    public void Builtin_sql_is_a_plain_select_safe_for_raw_composition(ReportDialect dialect)
    {
        var definition = SavedReportsListingDefinition.Create(
            new SavedReportStoreConfig("ReportsDb", dialect));
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

    // ---- definition edit link + per-column overrides ----

    private static ConfigurationReportDefinitionStore StoreFor(ReportDefinition def)
    {
        var options = new InteractiveReportOptions();
        options.Reports["orders"] = def;
        return new ConfigurationReportDefinitionStore(new OptionsMonitorStub(options), new SchemaCache(), TestRegistry());
    }

    private static ReportDefinition OrdersDefinition() => new()
    {
        Connection = "db",
        Dialect = ReportDialect.Sqlite,
        Sql = "select 1 as ORDER_ID, 'x' as NOTES",
    };

    [Theory]
    [InlineData(" ", "editLink.urlTemplate is required")]
    [InlineData("/orders/edit", "at least one {COLUMN} placeholder")]
    [InlineData("/orders/{}/edit", "empty placeholder")]
    [InlineData("/orders/{ORDER_ID/edit", "'{' without a matching '}'")]
    [InlineData("/orders/{A{B}}/edit", "nested '{'")]
    [InlineData("javascript:{ORDER_ID}", "must use http or https")]
    [InlineData("file:///orders/{ORDER_ID}", "must use http or https")]
    public async Task Invalid_edit_link_templates_fail_fast(string template, string expected)
    {
        var def = OrdersDefinition();
        def.EditLink = new ReportEditLink { UrlTemplate = template };
        using var store = StoreFor(def);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await store.Find("orders"));

        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public async Task Oversized_edit_link_template_fails_fast()
    {
        var def = OrdersDefinition();
        def.EditLink = new ReportEditLink { UrlTemplate = "/o/{ID}/" + new string('x', 2048) };
        using var store = StoreFor(def);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await store.Find("orders"));

        Assert.Contains("at most 2048 characters", error.Message);
    }

    [Theory]
    [InlineData(" ", null, "editLink.label must not be blank")]
    [InlineData(null, "middle", "editLink.target must be '_self' or '_blank'")]
    public async Task Invalid_edit_link_label_or_target_fails_fast(string? label, string? target, string expected)
    {
        var def = OrdersDefinition();
        def.EditLink = new ReportEditLink { UrlTemplate = "/orders/{ORDER_ID}/edit", Label = label, Target = target };
        using var store = StoreFor(def);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await store.Find("orders"));

        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public async Task Edit_link_accepts_relative_and_absolute_http_templates()
    {
        foreach (var template in new[]
        {
            "/orders/{ORDER_ID}/edit",
            "orders/edit?id={ORDER_ID}",
            "https://apps.example.com/orders/{ORDER_ID}",
            "{ORDER_ID}/edit",
        })
        {
            var def = OrdersDefinition();
            def.EditLink = new ReportEditLink { UrlTemplate = template, Label = "Edit order", Target = "_BLANK" };
            using var store = StoreFor(def);

            var snapshot = await store.Find("orders");

            Assert.Equal(template, snapshot!.EditLink!.UrlTemplate);
        }
    }

    [Theory]
    [InlineData(" ", "blank column name")]
    [InlineData("ORDER_ID", "must not be blank — use hideLabel")]
    public async Task Blank_column_override_entries_fail_fast(string name, string expected)
    {
        var def = OrdersDefinition();
        def.Columns = new() { [name] = new ReportColumnOverride { Label = name == " " ? "Fine" : " " } };
        using var store = StoreFor(def);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await store.Find("orders"));

        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public async Task Case_colliding_column_override_keys_fail_fast()
    {
        var def = OrdersDefinition();
        def.Columns = new Dictionary<string, ReportColumnOverride>(StringComparer.Ordinal)
        {
            ["ORDER_ID"] = new() { Sortable = false },
            ["order_id"] = new() { Filterable = false },
        };
        using var store = StoreFor(def);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await store.Find("orders"));

        Assert.Contains("duplicate column", error.Message);
    }

    [Fact]
    public async Task Blank_help_text_fails_fast()
    {
        var def = OrdersDefinition();
        def.Columns = new() { ["ORDER_ID"] = new ReportColumnOverride { HelpText = "  " } };
        using var store = StoreFor(def);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await store.Find("orders"));

        Assert.Contains("helpText must not be blank", error.Message);
    }

    [Fact]
    public async Task A_label_in_both_maps_fails_fast()
    {
        var def = OrdersDefinition();
        def.ColumnLabels = new() { ["ORDER_ID"] = "Order #" };
        def.Columns = new() { ["order_id"] = new ReportColumnOverride { Label = "Ticket" } };
        using var store = StoreFor(def);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await store.Find("orders"));

        Assert.Contains("configure it in one place", error.Message);
    }

    [Fact]
    public async Task Default_state_contradicting_a_sort_restriction_fails_fast()
    {
        var sortingDef = OrdersDefinition();
        sortingDef.Columns = new() { ["NOTES"] = new ReportColumnOverride { Sortable = false } };
        sortingDef.DefaultState = new ReportState
        {
            Pipeline =
            [
                new PipelineStage
                {
                    Shape = new StageShape { Kind = "source" },
                    Layer = new StageLayer { Sorts = [new SortRule { Col = "NOTES" }] },
                },
            ],
        };
        using var sorting = StoreFor(sortingDef);
        var sortError = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await sorting.Find("orders"));
        Assert.Contains("is not sortable", sortError.Message);

        var breakingDef = OrdersDefinition();
        breakingDef.Columns = new() { ["NOTES"] = new ReportColumnOverride { Sortable = false } };
        breakingDef.DefaultState = new ReportState
        {
            Pipeline =
            [
                new PipelineStage
                {
                    Shape = new StageShape { Kind = "source" },
                    Layer = new StageLayer { Breaks = ["notes"] },
                },
            ],
        };
        using var breaking = StoreFor(breakingDef);
        var breakError = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await breaking.Find("orders"));
        Assert.Contains("control breaks imply sorting", breakError.Message);
    }

    [Fact]
    public async Task Edit_link_and_column_overrides_round_trip_the_snapshot()
    {
        var def = OrdersDefinition();
        def.EditLink = new ReportEditLink
        {
            UrlTemplate = "/orders/{ORDER_ID}/edit",
            Label = "Edit order",
            Target = "_blank",
        };
        def.Columns = new()
        {
            ["NOTES"] = new ReportColumnOverride
            {
                HideLabel = true,
                Sortable = false,
                Filterable = false,
                HelpText = "Free-form notes.",
            },
            ["ORDER_ID"] = new ReportColumnOverride { Label = "Order #" },
        };
        using var store = StoreFor(def);

        var snapshot = await store.Find("orders");

        // Snapshot() serializes through IrJson — a property this drops is silently
        // lost on every Find, so the round-trip is the load-bearing assertion.
        Assert.NotNull(snapshot);
        Assert.NotSame(def.EditLink, snapshot.EditLink);
        Assert.Equal("/orders/{ORDER_ID}/edit", snapshot.EditLink!.UrlTemplate);
        Assert.Equal("Edit order", snapshot.EditLink.Label);
        Assert.Equal("_blank", snapshot.EditLink.Target);
        Assert.NotSame(def.Columns, snapshot.Columns);
        Assert.Contains("NOTES", snapshot.Columns!.Keys);   // map key casing intact
        var notes = snapshot.Columns["NOTES"];
        Assert.True(notes.HideLabel);
        Assert.False(notes.Sortable);
        Assert.False(notes.Filterable);
        Assert.Equal("Free-form notes.", notes.HelpText);
        Assert.Equal("Order #", snapshot.Columns["ORDER_ID"].Label);
        Assert.Equal("Order #", snapshot.GetEffectiveColumnLabels()!["ORDER_ID"]);
    }

    [Fact]
    public async Task Consistency_is_opt_in_and_round_trips_the_definition_snapshot()
    {
        var defaultDefinition = OrdersDefinition();
        using var defaultStore = StoreFor(defaultDefinition);
        Assert.Equal(ReportConsistency.None, (await defaultStore.Find("orders"))!.Consistency);

        var snapshotDefinition = OrdersDefinition();
        snapshotDefinition.Consistency = ReportConsistency.Snapshot;
        using var snapshotStore = StoreFor(snapshotDefinition);
        Assert.Equal(ReportConsistency.Snapshot, (await snapshotStore.Find("orders"))!.Consistency);
    }

    [Fact]
    public void Unknown_consistency_strategy_fails_definition_validation()
    {
        var definition = OrdersDefinition();
        definition.Name = "orders";
        definition.Consistency = (ReportConsistency)99;

        var error = Assert.Throws<InvalidOperationException>(
            () => ConfigurationReportDefinitionStore.Validate(definition));

        Assert.Contains("unknown consistency strategy", error.Message);
        Assert.Contains("none, snapshot", error.Message);
    }

    // ---- dataSource + derived dialect ----

    private static ConfigurationReportDefinitionStore StoreWith(
        ReportDefinition def,
        params (string Key, string Value)[] configuration)
    {
        var options = new InteractiveReportOptions();
        options.Reports["orders"] = def;
        var registry = new ReportConnectionRegistry(
            new Dictionary<string, Func<IServiceProvider, System.Data.Common.DbConnection>>(StringComparer.OrdinalIgnoreCase)
            {
                ["db"] = _ => new SqliteConnection("Data Source=:memory:"),
            },
            new Dictionary<string, ReportDialect>(StringComparer.OrdinalIgnoreCase),
            NullServices.Instance,
            new ConfigurationBuilder()
                .AddInMemoryCollection(configuration.ToDictionary(p => p.Key, p => (string?)p.Value))
                .Build());
        return new ConfigurationReportDefinitionStore(new OptionsMonitorStub(options), new SchemaCache(), registry);
    }

    [Fact]
    public async Task Data_source_and_connection_are_exclusive_and_one_is_required()
    {
        using var both = StoreWith(new ReportDefinition
        {
            Connection = "db",
            DataSource = "Data Source=:memory:",
            Sql = "select 1 as ID",
        });
        var bothError = await Assert.ThrowsAsync<InvalidOperationException>(async () => await both.Find("orders"));
        Assert.Contains("not both", bothError.Message);

        using var neither = StoreWith(new ReportDefinition { Sql = "select 1 as ID" });
        var neitherError = await Assert.ThrowsAsync<InvalidOperationException>(async () => await neither.Find("orders"));
        Assert.Contains("a data source is required", neitherError.Message);
        Assert.Contains("dataSource", neitherError.Message);
    }

    [Fact]
    public async Task Provider_belongs_to_data_sources_and_reserved_connection_prefixes_fail()
    {
        using var withProvider = StoreWith(new ReportDefinition
        {
            Connection = "db",
            Provider = "sqlite",
            Sql = "select 1 as ID",
        });
        var providerError = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await withProvider.Find("orders"));
        Assert.Contains("provider applies to dataSource", providerError.Message);

        using var reserved = StoreWith(new ReportDefinition
        {
            Connection = "__ir:ds:abc",
            Sql = "select 1 as ID",
        });
        var reservedError = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await reserved.Find("orders"));
        Assert.Contains("'__ir:' are reserved", reservedError.Message);
    }

    [Fact]
    public async Task Dialects_derive_from_the_connection_and_supersede_configured_leftovers()
    {
        // No dialect configured — the new normal.
        using var derived = StoreWith(new ReportDefinition { Connection = "db", Sql = "select 1 as ID" });
        var snapshot = await derived.Find("orders");
        Assert.Equal(ReportDialect.Sqlite, snapshot!.Dialect);
        Assert.Equal(ReportDialect.Sqlite, snapshot.GetEffectiveDialect());

        // A leftover configured dialect — even a wrong one — is silently superseded
        // by the connection's own: dialect stopped being a per-report choice.
        using var leftover = StoreWith(new ReportDefinition
        {
            Connection = "db",
            Dialect = ReportDialect.Oracle,
            Sql = "select 1 as ID",
        });
        var corrected = await leftover.Find("orders");
        Assert.Equal(ReportDialect.Sqlite, corrected!.Dialect);
    }

    [Fact]
    public async Task Data_source_reports_resolve_to_synthetic_connections()
    {
        using var literal = StoreWith(new ReportDefinition
        {
            DataSource = "Data Source=:memory:",
            Provider = "sqlite",
            Sql = "select 1 as ID",
        });
        var snapshot = await literal.Find("orders");
        Assert.StartsWith("__ir:ds:", snapshot!.Connection);
        Assert.Equal(ReportDialect.Sqlite, snapshot.Dialect);

        using var named = StoreWith(
            new ReportDefinition { DataSource = "AppDb", Sql = "select 1 as ID" },
            ("ConnectionStrings:AppDb", "Data Source=:memory:"),
            ("ConnectionStrings:AppDb_ProviderName", "Microsoft.Data.Sqlite"));
        var resolved = await named.Find("orders");
        Assert.StartsWith("__ir:ds:", resolved!.Connection);
        Assert.Equal(ReportDialect.Sqlite, resolved.Dialect);
        Assert.Equal("AppDb", resolved.DataSource);
    }

    // ---- canonical names, reserved routes, ORDER BY lint ----

    [Fact]
    public async Task Find_returns_the_configured_casing_as_the_canonical_name()
    {
        var options = new InteractiveReportOptions();
        options.Reports["Orders"] = new ReportDefinition
        {
            Connection = "db",
            Sql = "select 1 as ID",
        };
        using var store = new ConfigurationReportDefinitionStore(
            new OptionsMonitorStub(options),
            new SchemaCache(), TestRegistry());

        var snapshot = await store.Find("oRdErS");

        // The configured key becomes REPORT_NAME in saved-report rows and the filter
        // that finds them again: alternate casing stays accepted at the boundary but
        // must never leak into persistence on case-sensitive databases.
        Assert.NotNull(snapshot);
        Assert.Equal("Orders", snapshot.Name);
    }

    [Theory]
    [InlineData("ui")]
    [InlineData("Saved")]
    [InlineData("whoami")]
    [InlineData("ADMIN")]
    public async Task Route_shadowed_report_names_fail_fast(string name)
    {
        var options = new InteractiveReportOptions();
        options.Reports[name] = new ReportDefinition
        {
            Connection = "db",
            Sql = "select 1 as ID",
        };
        using var store = new ConfigurationReportDefinitionStore(
            new OptionsMonitorStub(options),
            new SchemaCache(), TestRegistry());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await store.Find(name));

        Assert.Contains("reserved names are ui, saved, whoami, admin", error.Message);
    }

    [Theory]
    [InlineData("SELECT 'order by' AS TXT FROM T", false)]
    [InlineData("SELECT 1 AS ID -- order by note", false)]
    [InlineData("SELECT 1 AS ID /* order by note */", false)]
    [InlineData("SELECT \"ORDER BY\" FROM T", false)]
    [InlineData("SELECT [order by] FROM T", false)]
    [InlineData("SELECT * FROM (SELECT ID FROM T ORDER BY ID) Q", false)]
    [InlineData("SELECT SUM(X) OVER (ORDER BY ID) AS R FROM T", false)]
    [InlineData("/* /* nested */ order by */ SELECT 1 AS ID", false)]
    [InlineData("SELECT ID FROM T ORDER BY ID", true)]
    [InlineData("SELECT ID FROM T ORDER/* split */BY ID", true)]
    [InlineData("SELECT (1) AS X /* ) */ FROM T ORDER BY 1", true)]
    [InlineData("SELECT 1 UNION SELECT 2 ORDER BY 1", true)]
    [InlineData("SELECT ID FROM T ORDER BY ID;", true)]
    public void Top_level_order_by_detection_is_comment_and_string_aware(string sql, bool detected)
    {
        Assert.Equal(detected, SqlTopLevelScanner.HasTopLevelOrderBy(sql));
    }

    [Fact]
    public async Task Order_by_lint_accepts_the_phrase_as_data_and_rejects_the_clause()
    {
        var options = new InteractiveReportOptions();
        options.Reports["good"] = new ReportDefinition
        {
            Connection = "db",
            Sql = "SELECT 'order by' AS HINT FROM T",
        };
        options.Reports["bad"] = new ReportDefinition
        {
            Connection = "db",
            Sql = "SELECT ID FROM T ORDER BY ID",
        };
        using var store = new ConfigurationReportDefinitionStore(
            new OptionsMonitorStub(options),
            new SchemaCache(), TestRegistry());

        Assert.NotNull(await store.Find("good"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await store.Find("bad"));
        Assert.Contains("must not end with ORDER BY", error.Message);
    }

    private sealed class OptionsMonitorStub(InteractiveReportOptions options)
        : IOptionsMonitor<InteractiveReportOptions>
    {
        public InteractiveReportOptions CurrentValue => options;
        public InteractiveReportOptions Get(string? name) => options;
        public IDisposable? OnChange(Action<InteractiveReportOptions, string?> listener) => null;
    }
}
