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

        Assert.Equal("__irc1", relation.PhysicalColumns["__irc0"]);
        Assert.Equal("__irc2", relation.Names.Column());
    }

    [Theory]
    [InlineData(ReportDialect.SqlServer)]
    [InlineData(ReportDialect.Sqlite)]
    [InlineData(ReportDialect.Postgres)]
    [InlineData(ReportDialect.Oracle)]
    public void Identifier_torture_corpus_compiles_as_literal_names(ReportDialect dialect)
    {
        var compiler = DialectSupport.GetCompiler(dialect);

        foreach (var name in IdentifierTortureCorpus.NamesForCompiler(dialect))
        {
            var compiled = compiler.Compile(new Query("source")
                .SelectRaw(SqlKataSyntax.Identifier(dialect, name)));
            var expected = IdentifierTortureCorpus.QuoteSqlIdentifier(dialect, name);

            Assert.True(
                compiled.Sql.Contains(expected, StringComparison.Ordinal),
                $"Expected {dialect} to preserve identifier '{name}' as {expected}, but compiled: {compiled.Sql}");
            Assert.Empty(compiled.NamedBindings);
        }
    }

    [Theory]
    [InlineData(ReportDialect.SqlServer, "[A]]B\"Q]")]
    [InlineData(ReportDialect.Sqlite, "\"A]B\"\"Q\"")]
    [InlineData(ReportDialect.Postgres, "\"A]B\"\"Q\"")]
    [InlineData(ReportDialect.Oracle, "\"A]B\"\"Q\"")]
    public void Raw_identifiers_escape_the_dialect_closing_delimiter(
        ReportDialect dialect,
        string expected)
    {
        var query = new Query("source").SelectRaw(
            $"{SqlKataSyntax.Identifier(dialect, "A]B\"Q")} AS {SqlKataSyntax.Identifier(dialect, "safe")}");

        var sql = DialectSupport.GetCompiler(dialect).Compile(query).Sql;

        Assert.Contains(expected, sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ReportDialect.SqlServer, "[A?B]")]
    [InlineData(ReportDialect.Sqlite, "\"A?B\"")]
    [InlineData(ReportDialect.Postgres, "\"A?B\"")]
    [InlineData(ReportDialect.Oracle, "\"A?B\"")]
    public void Raw_question_marks_are_not_mistaken_for_bindings(
        ReportDialect dialect,
        string expectedIdentifier)
    {
        const string configuredSql = "SELECT '?' AS P, '\uE000?' AS S";
        const string sentinelIdentifier = "A\uE000\uE001B";
        const string sentinelBinding = "bound\uE000\uE001?";
        var query = new Query()
            .FromRaw($"({SqlKataSyntax.PreserveRaw(configuredSql)}) source")
            .SelectRaw($"{SqlKataSyntax.Identifier(dialect, "A?B")}, {SqlKataSyntax.Identifier(dialect, sentinelIdentifier)}")
            .Where("P", sentinelBinding);

        var compiled = DialectSupport.GetCompiler(dialect).Compile(query);

        Assert.Contains("'?'", compiled.Sql, StringComparison.Ordinal);
        Assert.Contains("'\uE000?'", compiled.Sql, StringComparison.Ordinal);
        Assert.Contains(expectedIdentifier, compiled.Sql, StringComparison.Ordinal);
        Assert.Contains(sentinelIdentifier, compiled.Sql, StringComparison.Ordinal);
        Assert.Single(compiled.NamedBindings);
        Assert.Equal(sentinelBinding, compiled.NamedBindings.Values.Single());
        Assert.Contains("'?'", compiled.ToString(), StringComparison.Ordinal);
        Assert.Contains(sentinelBinding, compiled.ToString(), StringComparison.Ordinal);
        Assert.Equal(2, compiled.Sql.Count(character => character == '\uE000'));
        Assert.Equal(2, compiled.RawSql.Count(character => character == '\uE000'));
        Assert.Equal(1, compiled.Sql.Count(character => character == '\uE001'));
        Assert.Equal(1, compiled.RawSql.Count(character => character == '\uE001'));

        var combined = DialectSupport.GetCompiler(dialect).Compile([query.Clone(), query.Clone()]);
        Assert.Equal(2, combined.NamedBindings.Count);
        Assert.Equal(2, combined.NamedBindings.Values.Count(value => Equals(value, sentinelBinding)));
        Assert.Equal(2, combined.Sql.Split(sentinelIdentifier, StringSplitOptions.None).Length - 1);
        Assert.Equal(2, combined.ToString().Split(sentinelBinding, StringSplitOptions.None).Length - 1);
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
    public void Pivot_raw_key_equality_normalizes_numeric_provider_widths_only()
    {
        Assert.True(ComposableTableCompiler.PivotKeysEqual([1], [1L]));
        Assert.True(ComposableTableCompiler.PivotKeysEqual([1m], [1d]));
        Assert.False(ComposableTableCompiler.PivotKeysEqual([1], ["1"]));
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

        var searched = ComposableSqlPlanner.ApplySearch(source, "open", ReportDialect.Sqlite);
        var grouped = ComposableSqlPlanner.Group(
            searched,
            "median",
            [schema.Lookup["STATUS"]],
            [new ValidMetric("ir1", schema.Lookup["AMOUNT"], AggregateFn.Median)],
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
    public async Task Export_rejects_definition_as_an_active_table_target()
    {
        var definition = new ReportDefinition
        {
            Name = "reserved-export-target",
            Connection = "E2E",
            Dialect = ReportDialect.Sqlite,
            Sql = "SELECT ORDER_ID, STATUS FROM ORDERS",
        };
        var state = TestFixtures.Doc();
        state.ActiveTable = "  DeFiNiTiOn  ";

        var exception = await Assert.ThrowsAsync<ReportValidationException>(() =>
            _executor.Download(definition, state, NoParams));

        Assert.Contains(exception.Errors, error =>
            error.Path == "activeTable"
            && error.Message == "unknown table 'DeFiNiTiOn'");
    }

    [Fact]
    public async Task Database_identifiers_containing_raw_markers_remain_addressable()
    {
        var definition = new ReportDefinition
        {
            Name = "raw-marker-identifier",
            Connection = "E2E",
            Dialect = ReportDialect.Sqlite,
            Sql = """SELECT STATUS AS "A]B", AMOUNT FROM ORDERS""",
        };
        var state = TestFixtures.Doc(tail:
        [
            TestFixtures.Group(
                by: ["A]B"],
                values: [TestFixtures.Metric("ir1", "AMOUNT", AggregateFn.Sum)]),
        ]);

        var query = await _executor.Query(definition, state, NoParams);
        var export = await _executor.Download(definition, state, NoParams);

        Assert.Equal(["A]B", "__count", "ir1"], query.Columns.Select(column => column.Name));
        Assert.Equal(4, query.TotalRows);
        Assert.All(query.Rows, row => Assert.True(row.ContainsKey("A]B")));
        Assert.Equal(query.Columns.Select(column => column.Name), export.Columns.Select(column => column.Name));
        Assert.Equal(4, export.Rows.Count);
    }

    [Fact]
    public async Task Definition_sql_and_identifiers_may_contain_literal_question_marks()
    {
        var definition = new ReportDefinition
        {
            Name = "question-mark-sql",
            Connection = "E2E",
            Dialect = ReportDialect.Sqlite,
            Sql = """SELECT STATUS AS "A?B", '?' AS "literal?", AMOUNT FROM ORDERS""",
        };
        var state = TestFixtures.Doc(tail:
        [
            TestFixtures.Group(
                by: ["A?B"],
                values: [TestFixtures.Metric("ir1", "AMOUNT", AggregateFn.Sum)]),
        ]);

        var query = await _executor.Query(definition, state, NoParams);
        var export = await _executor.Download(definition, state, NoParams);

        Assert.Equal(["A?B", "__count", "ir1"], query.Columns.Select(column => column.Name));
        Assert.Equal(4, query.TotalRows);
        Assert.All(query.Rows, row => Assert.True(row.ContainsKey("A?B")));
        Assert.Equal(query.Columns.Select(column => column.Name), export.Columns.Select(column => column.Name));
        Assert.Equal(4, export.Rows.Count);
    }

    [Fact]
    public async Task Intermediate_metric_mask_overrides_root_provenance_without_inheriting_renderer()
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
                    ["AMOUNT"] = new() { Mask = "$#,##0.00" },
                },
            },
            tail:
            [
                TestFixtures.Group(
                    ["STATUS"],
                    [TestFixtures.Metric("ir1", "AMOUNT", AggregateFn.Sum)],
                    new TestFixtures.StageLayer
                    {
                        Formats = new Dictionary<string, ColumnFormat>
                        {
                            ["ir1"] = new()
                            {
                                DisplayAs = "link",
                                UrlColumn = "STATUS",
                                Mask = "#,##0.00",
                            },
                        },
                    }),
                TestFixtures.Group(
                    ["STATUS"],
                    [TestFixtures.Metric("ir2", "ir1", AggregateFn.Sum)],
                    new TestFixtures.StageLayer { Columns = ["ir2"] }),
            ]);

        var query = await _executor.Query(definition, state, NoParams);
        var export = await _executor.Download(definition, state, NoParams);

        Assert.Equal(
            "ir1",
            query.Document!.Tables![state.ActiveTable!].Schema!
                .Single(column => column.Name == "ir2").FormatSource);
        Assert.Equal(
            6000m,
            decimal.Parse(
                Convert.ToString(export.Rows[0]["ir2"])!,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture));
        Assert.DoesNotContain("<a", Convert.ToString(export.Rows[0]["ir2"]));
    }

    [Fact]
    public async Task Null_cache_parent_terminal_validation_does_not_leak_into_a_child_target()
    {
        var definition = new ReportDefinition
        {
            Name = "terminal-isolation",
            Connection = "E2E",
            Dialect = ReportDialect.Sqlite,
            Sql = "SELECT ORDER_ID, CUSTOMER, STATUS, AMOUNT, NOTES FROM ORDERS",
        };

        static ReportState State(string activeTable) => new()
        {
            ActiveTable = activeTable,
            Page = new PageRequest { Index = 1, Size = 0 },
            Tables = new Dictionary<string, ReportTable>
            {
                ["parent"] = new()
                {
                    From = "definition",
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
                        new TableComposable
                        {
                            Kind = "break",
                            Breaks = ["UNKNOWN_BREAK"],
                        },
                        new TableComposable
                        {
                            Kind = "aggregate",
                            Aggregates =
                            [
                                new AggregateRule
                                {
                                    Col = "NOTES",
                                    Fn = AggregateFn.Sum,
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

        var result = await _executor.Query(definition, State("child"), NoParams);

        Assert.Equal(10, result.TotalRows);
        Assert.DoesNotContain(result.Ignored, item =>
            item.Kind is "highlight" or "break" or "aggregate");
        Assert.NotNull(result.Document!.Tables!["parent"].Schema);

        var exception = await Assert.ThrowsAsync<ReportValidationException>(() =>
            _executor.Query(definition, State("parent"), NoParams));
        Assert.Contains(exception.Errors, error =>
            error.Path == "tables.parent.composables[0].highlights[0].expr"
            && error.Message.Contains("UNKNOWN_COLUMN", StringComparison.Ordinal));
        Assert.Contains(exception.Errors, error =>
            error.Path == "tables.parent.composables[2].aggregates[0]"
            && error.Message.Contains("not valid", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Request_search_is_not_applied_to_a_non_active_schema_refresh()
    {
        var definition = new ReportDefinition
        {
            Name = "non-active-search-isolation",
            Connection = "E2E",
            Dialect = ReportDialect.Sqlite,
            Sql = "SELECT ORDER_ID, CUSTOMER, STATUS, AMOUNT, NOTES FROM ORDERS",
        };
        var state = new ReportState
        {
            ActiveTable = "active",
            Search = "Acme",
            Page = new PageRequest { Index = 1, Size = 0 },
            Tables = new Dictionary<string, ReportTable>
            {
                ["inactive"] = new()
                {
                    From = "definition",
                    Composables =
                    [
                        new TableComposable
                        {
                            Kind = "pivot",
                            Rows = ["CUSTOMER"],
                            Cols = ["STATUS"],
                            Totals = true,
                        },
                    ],
                },
                ["active"] = new()
                {
                    From = "definition",
                    Composables = [],
                },
            },
        };

        var result = await _executor.Query(definition, state, NoParams);

        Assert.Equal(3, result.TotalRows);
        Assert.All(result.Rows, row => Assert.Contains(
            "acme",
            Convert.ToString(row["CUSTOMER"])!,
            StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(result.Document!.Tables!["inactive"].Schema);
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
                                    Id = "ir1",
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
