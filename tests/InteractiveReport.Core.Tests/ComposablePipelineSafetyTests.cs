using InteractiveReport.Core.Composition;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Schema;
using InteractiveReport.Core.Validation;
using Microsoft.Data.Sqlite;
using SqlKata;
using SqlKata.Compilers;

namespace InteractiveReport.Core.Tests;

public sealed class ComposablePipelineSafetyTests : IClassFixture<SqliteE2EFixture>
{
    private static readonly IReadOnlyDictionary<string, object?> NoParams =
        new Dictionary<string, object?>();

    private readonly ReportExecutor _executor;

    public ComposablePipelineSafetyTests(SqliteE2EFixture database)
        => _executor = new ReportExecutor(database, new SchemaCache());

    [Fact]
    public void Physical_aliases_skip_definition_column_names()
    {
        var schema = ReportSchema.Create(
            "collision",
            [TestFixtures.Col("__irc0", typeof(string))]);
        var definition = new ReportDefinition
        {
            Name = "collision",
            Connection = "TestDb",
            Dialect = ReportDialect.Sqlite,
            Sql = "SELECT __irc0 FROM collision",
        };

        var relation = ComposableSqlRelation.Definition(definition, schema);

        Assert.Equal("__irc1", relation.Names.Column());
    }

    [Fact]
    public void Pivot_totals_match_case_and_type_colliding_keys_by_raw_identity()
    {
        var keys = new List<PivotColumnKey>
        {
            CountKey(["A"], "__count@[\"A\"]"),
            CountKey(["a"], "__count@[\"a\"]~2"),
            CountKey([1], "__count@[\"1\"]"),
            CountKey(["1"], "__count@[\"1\"]~2"),
        };
        var groups = new List<PivotGroup>
        {
            new([], ["A"], 11, []),
            new([], ["a"], 12, []),
            new([], [1], 13, []),
            new([], ["1"], 14, []),
        };

        var totals = ReportExecutor.BuildPivotTotals(groups, [], keys);

        Assert.Equal(11L, totals["__count@[\"A\"]"]["count"]);
        Assert.Equal(12L, totals["__count@[\"a\"]~2"]["count"]);
        Assert.Equal(13L, totals["__count@[\"1\"]"]["count"]);
        Assert.Equal(14L, totals["__count@[\"1\"]~2"]["count"]);
    }

    [Fact]
    public void Pivot_binary_keys_compare_by_content()
    {
        Assert.True(ComposableTableCompiler.PivotKeysEqual(
            [new byte[] { 1, 2, 3 }],
            [new byte[] { 1, 2, 3 }]));
        Assert.False(ComposableTableCompiler.PivotKeysEqual(
            [new byte[] { 1, 2, 3 }],
            [new byte[] { 1, 2, 4 }]));
    }

    [Fact]
    public void Relation_nesting_counts_search_and_median_wrappers()
    {
        var definition = new ReportDefinition
        {
            Name = "nesting",
            Connection = "TestDb",
            Dialect = ReportDialect.Sqlite,
            Sql = "SELECT STATUS, AMOUNT FROM ORDERS",
        };
        var schema = ReportSchema.Create(
            "nesting",
            [TestFixtures.Col("STATUS", typeof(string)), TestFixtures.Col("AMOUNT", typeof(decimal))]);
        var source = ComposableSqlRelation.Definition(definition, schema);

        var searched = ComposableSqlPlanner.ApplySearch(source, "open");
        var grouped = ComposableSqlPlanner.Group(
            searched,
            "median",
            [schema.Lookup["STATUS"]],
            [new ValidMetric("m1", schema.Lookup["AMOUNT"], AggregateFn.Median)],
            ReportDialect.Sqlite);

        Assert.Equal(1, searched.NestingDepth);
        Assert.Equal(3, grouped.NestingDepth);
    }

    [Fact]
    public void Command_parameter_budget_counts_compiled_and_context_bindings_together()
    {
        var query = new Query("source").Select("value");
        for (var index = 0; index < CommandBuilder.MaxParameters - 1; index++)
            query.Where("value", index);
        var compiled = new SqlServerCompiler().Compile(query);
        var context = new Dictionary<string, object?>
        {
            ["tenant"] = 1,
            ["actor"] = 2,
        };
        using var connection = new SqliteConnection("Data Source=:memory:");

        var exception = Assert.Throws<ReportValidationException>(() => CommandBuilder.Build(
            connection,
            compiled,
            context,
            30,
            ReportDialect.SqlServer));

        Assert.Contains(exception.Errors, error =>
            error.Path == "query"
            && error.Message.Contains($"{CommandBuilder.MaxParameters}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Terminal_aggregate_projection_width_is_bounded()
    {
        var columns = Enumerable.Range(0, 130)
            .Select(index => TestFixtures.Col($"N{index}", typeof(decimal)))
            .ToList();
        var definition = new ReportDefinition
        {
            Name = "wide-aggregates",
            Connection = "TestDb",
            Dialect = ReportDialect.Oracle,
            Sql = "SELECT 1",
        };
        var state = new ReportState
        {
            ActiveTable = "wide",
            Tables = new Dictionary<string, ReportTable>
            {
                ["wide"] = new()
                {
                    From = "definition",
                    Composables =
                    [
                        new TableComposable
                        {
                            Kind = "aggregate",
                            Aggregates = columns.SelectMany(column =>
                                Enum.GetValues<AggregateFn>().Select(function => new AggregateRule
                                {
                                    Col = column.Name,
                                    Fn = function,
                                })).ToList(),
                        },
                    ],
                },
            },
        };
        var compiler = Compiler(definition, state, ReportSchema.Create("wide-aggregates", columns));

        var raw = await compiler.Compile("wide", default);
        var exception = Assert.Throws<ReportValidationException>(() => compiler.CompleteForTarget(raw));

        Assert.Contains(exception.Errors, error =>
            error.Message.Contains("terminal aggregates", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Median_terminal_helper_projection_width_is_bounded()
    {
        var columns = Enumerable.Range(0, 301)
            .Select(index => TestFixtures.Col($"N{index}", typeof(decimal)))
            .ToList();
        var definition = new ReportDefinition
        {
            Name = "wide-median-helpers",
            Connection = "TestDb",
            Dialect = ReportDialect.Oracle,
            Sql = "SELECT 1",
        };
        var state = new ReportState
        {
            ActiveTable = "wide",
            Tables = new Dictionary<string, ReportTable>
            {
                ["wide"] = new()
                {
                    From = "definition",
                    Composables =
                    [
                        new TableComposable
                        {
                            Kind = "aggregate",
                            Aggregates = columns.Select(column => new AggregateRule
                            {
                                Col = column.Name,
                                Fn = AggregateFn.Median,
                            }).ToList(),
                        },
                    ],
                },
            },
        };
        var compiler = Compiler(definition, state, ReportSchema.Create("wide-median-helpers", columns));

        var raw = await compiler.Compile("wide", default);
        var exception = Assert.Throws<ReportValidationException>(() => compiler.CompleteForTarget(raw));

        Assert.Contains(exception.Errors, error =>
            error.Message.Contains("median ranking", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Grouped_relation_output_width_is_bounded_before_planning()
    {
        var columns = Enumerable.Range(0, ComposableTableCompiler.MaxGeneratedColumns)
            .Select(index => TestFixtures.Col($"D{index}", typeof(string)))
            .ToList();
        var definition = new ReportDefinition
        {
            Name = "wide-group",
            Connection = "TestDb",
            Dialect = ReportDialect.Oracle,
            Sql = "SELECT 1",
        };
        var state = new ReportState
        {
            ActiveTable = "wide",
            Tables = new Dictionary<string, ReportTable>
            {
                ["wide"] = new()
                {
                    From = "definition",
                    Composables =
                    [
                        new TableComposable
                        {
                            Kind = "group",
                            By = columns.Select(column => column.Name).ToList(),
                        },
                    ],
                },
            },
        };
        var compiler = Compiler(definition, state, ReportSchema.Create("wide-group", columns));

        var exception = await Assert.ThrowsAsync<ReportValidationException>(() => compiler.Compile("wide", default));

        Assert.Contains(exception.Errors, error =>
            error.Message.Contains("grouped relation", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Missing_active_table_fails_before_any_named_table_discovery()
    {
        var definition = new ReportDefinition
        {
            Name = "missing-active",
            Connection = "E2E",
            Dialect = ReportDialect.Sqlite,
            Sql = "SELECT ORDER_ID, CUSTOMER, STATUS, AMOUNT, NOTES FROM ORDERS",
        };
        var state = new ReportState
        {
            Tables = new Dictionary<string, ReportTable>
            {
                ["pivot"] = new()
                {
                    From = "definition",
                    Composables =
                    [
                        new TableComposable
                        {
                            Kind = "pivot",
                            Rows = ["CUSTOMER"],
                            Cols = ["STATUS"],
                        },
                    ],
                },
            },
        };

        var exception = await Assert.ThrowsAsync<ReportValidationException>(() =>
            _executor.RefreshSchemaCaches(definition, state, NoParams));

        Assert.Contains(exception.Errors, error => error.Path == "activeTable");
    }

    [Fact]
    public async Task Intermediate_metric_format_overrides_root_provenance_for_the_next_shape()
    {
        var definition = new ReportDefinition
        {
            Name = "format-provenance",
            Connection = "E2E",
            Dialect = ReportDialect.Sqlite,
            Sql = "SELECT ORDER_ID, CUSTOMER, STATUS, AMOUNT, NOTES FROM ORDERS",
        };
        var state = TestFixtures.Doc(
            source: new TestFixtures.StageLayer
            {
                Formats = new Dictionary<string, ColumnFormat>
                {
                    ["AMOUNT"] = new() { Mask = "currency:USD" },
                },
            },
            tail:
            [
                TestFixtures.Group(
                    ["STATUS"],
                    [TestFixtures.Metric("m1", "AMOUNT", AggregateFn.Sum)],
                    new TestFixtures.StageLayer
                    {
                        Formats = new Dictionary<string, ColumnFormat>
                        {
                            ["m1"] = new()
                            {
                                DisplayAs = "link",
                                UrlColumn = "STATUS",
                                Mask = "decimal2",
                            },
                        },
                    }),
                TestFixtures.Group(
                    ["STATUS"],
                    [TestFixtures.Metric("m2", "m1", AggregateFn.Sum)],
                    new TestFixtures.StageLayer { Columns = ["m2"] }),
            ]);

        var query = await _executor.Query(definition, state, NoParams);
        var export = await _executor.Export(definition, state, NoParams);

        Assert.Equal(
            "m1",
            query.Document!.Tables![state.ActiveTable!].Schema!
                .Single(column => column.Name == "m2").FormatSource);
        Assert.Equal(
            "6,000.00",
            export.Rows[0]["m2"]);
    }

    [Fact]
    public async Task Parent_terminal_validation_does_not_leak_into_a_child_target()
    {
        var definition = new ReportDefinition
        {
            Name = "terminal-isolation",
            Connection = "E2E",
            Dialect = ReportDialect.Sqlite,
            Sql = "SELECT ORDER_ID, CUSTOMER, STATUS, AMOUNT, NOTES FROM ORDERS",
        };
        var state = new ReportState
        {
            ActiveTable = "child",
            Page = new PageRequest { Index = 1, Size = 0 },
            Tables = new Dictionary<string, ReportTable>
            {
                ["parent"] = new()
                {
                    From = "definition",
                    // A non-null advisory cache keeps this inactive table out of the
                    // refresh-target set. Its terminal rule must not enter child binding.
                    Schema = [new ColumnInfo("FORGED", "Forged", "text", false)],
                    Composables =
                    [
                        new TableComposable
                        {
                            Kind = "highlight",
                            Highlights =
                            [
                                new HighlightRule
                                {
                                    Id = "broken-parent",
                                    Expr = "UNKNOWN_COLUMN > 0",
                                    Style = new HighlightStyle { Bg = "red" },
                                },
                            ],
                        },
                    ],
                },
                ["child"] = new()
                {
                    From = "parent",
                    Composables =
                    [
                        new TableComposable
                        {
                            Kind = "filter",
                            Filters = [new FilterRule { Expr = "AMOUNT >= 0" }],
                        },
                    ],
                },
            },
        };

        var result = await _executor.Query(definition, state, NoParams);

        Assert.Equal(10, result.TotalRows);
        Assert.DoesNotContain(result.Ignored, item => item.Kind == "highlight");
    }

    [Fact]
    public async Task Pivot_limits_use_discovered_cardinality_not_the_configured_ceiling()
    {
        var definition = PivotDefinition();
        definition.MaxPivotColumns = 1_000;
        var groups = Enumerable.Range(0, 4)
            .Select(index => new PivotGroup(["row"], [$"key-{index}"], 1, [10m]))
            .ToList();
        var compiler = PivotCompiler(definition, groups);

        var plan = compiler.CompleteForTarget(await compiler.Compile("pivot", default));

        Assert.Equal(5, plan.Relation.Schema.Count);
    }

    [Fact]
    public async Task Pivot_typed_key_collision_suffixes_do_not_depend_on_discovery_order()
    {
        var numericFirst = await PivotBindings(
            [
                new PivotGroup(["row"], [1], 1, [10m]),
                new PivotGroup(["row"], ["1"], 1, [20m]),
            ]);
        var textFirst = await PivotBindings(
            [
                new PivotGroup(["row"], ["1"], 1, [20m]),
                new PivotGroup(["row"], [1], 1, [10m]),
            ]);

        Assert.Equal(
            numericFirst.Select(value => (value?.GetType(), value?.ToString())),
            textFirst.Select(value => (value?.GetType(), value?.ToString())));
    }

    private static PivotColumnKey CountKey(object?[] values, string publicName)
        => new(
            values,
            [new PivotCellColumn(
                "__count",
                new ColumnModel
                {
                    Name = publicName,
                    Label = publicName,
                    ClrType = typeof(long),
                })]);

    private static ReportDefinition PivotDefinition()
        => new()
        {
            Name = "pivot-safety",
            Connection = "TestDb",
            Dialect = ReportDialect.Sqlite,
            Sql = "SELECT ROW_NAME, KEY_NAME, AMOUNT FROM SOURCE",
        };

    private static ComposableTableCompiler PivotCompiler(
        ReportDefinition definition,
        List<PivotGroup> groups)
    {
        var state = new ReportState
        {
            ActiveTable = "pivot",
            Tables = new Dictionary<string, ReportTable>
            {
                ["pivot"] = new()
                {
                    From = "definition",
                    Composables =
                    [
                        new TableComposable
                        {
                            Kind = "pivot",
                            Rows = ["ROW_NAME"],
                            Cols = ["KEY_NAME"],
                            Values =
                            [
                                new MetricRule
                                {
                                    Id = "m1",
                                    Col = "AMOUNT",
                                    Fn = AggregateFn.Sum,
                                },
                            ],
                        },
                    ],
                },
            },
        };
        var schema = ReportSchema.Create(
            "pivot-safety",
            [
                TestFixtures.Col("ROW_NAME", typeof(string)),
                TestFixtures.Col("KEY_NAME", typeof(string)),
                TestFixtures.Col("AMOUNT", typeof(decimal)),
            ]);
        return new ComposableTableCompiler(
            definition,
            state,
            schema,
            DateTime.UtcNow,
            (_, _, _, _, _) => Task.FromResult(groups));
    }

    private static async Task<IReadOnlyList<object?>> PivotBindings(List<PivotGroup> groups)
    {
        var compiler = PivotCompiler(PivotDefinition(), groups);
        var plan = compiler.CompleteForTarget(await compiler.Compile("pivot", default));
        return new SqliteCompiler().Compile(plan.Relation.Query).Bindings;
    }

    private static ComposableTableCompiler Compiler(
        ReportDefinition definition,
        ReportState state,
        ReportSchema schema)
        => new(
            definition,
            state,
            schema,
            DateTime.UtcNow,
            (_, _, _, _, _) => Task.FromResult(new List<PivotGroup>()));
}
