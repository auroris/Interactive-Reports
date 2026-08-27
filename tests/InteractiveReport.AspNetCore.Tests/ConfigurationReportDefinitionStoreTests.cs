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

    // ---- definition edit link + per-column overrides ----

    private static ConfigurationReportDefinitionStore StoreFor(ReportDefinition def)
    {
        var options = new InteractiveReportOptions();
        options.Reports["orders"] = def;
        return new ConfigurationReportDefinitionStore(new OptionsMonitorStub(options), new SchemaCache());
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

    private sealed class OptionsMonitorStub(InteractiveReportOptions options)
        : IOptionsMonitor<InteractiveReportOptions>
    {
        public InteractiveReportOptions CurrentValue => options;
        public InteractiveReportOptions Get(string? name) => options;
        public IDisposable? OnChange(Action<InteractiveReportOptions, string?> listener) => null;
    }
}
