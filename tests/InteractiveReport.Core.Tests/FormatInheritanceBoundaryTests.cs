using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Schema;

namespace InteractiveReport.Core.Tests;

public sealed class FormatInheritanceBoundaryTests : IClassFixture<SqliteE2EFixture>
{
    private static readonly IReadOnlyDictionary<string, object?> NoParams =
        new Dictionary<string, object?>();

    private static readonly DateTime EvaluationUtcNow =
        new(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc);

    private readonly ReportExecutor _executor;

    public FormatInheritanceBoundaryTests(SqliteE2EFixture database)
        => _executor = new ReportExecutor(database, new SchemaCache());

    private static ReportDefinition Definition => new()
    {
        Name = "format-inheritance-boundary",
        Connection = "E2E",
        Dialect = ReportDialect.Sqlite,
        Sql = "SELECT CUSTOMER, STATUS, AMOUNT, NOTES FROM ORDERS",
    };

    [Fact]
    public async Task Child_does_not_inherit_parent_renderer_or_hidden_projection()
    {
        var document = Document(
            activeTable: "child",
            child: new ReportTable
            {
                From = "parent",
                Composables =
                [
                    new TableComposable { Kind = "select", Columns = ["AMOUNT"] },
                ],
            });

        var result = await _executor.Query(Definition, document, NoParams);
        var export = await _executor.Export(Definition, document, NoParams);
        var plan = await Compile(document);

        Assert.All(result.Rows, row => Assert.Equal(["AMOUNT"], row.Keys));
        Assert.All(export.Rows, row => Assert.IsNotType<string>(row["AMOUNT"]));
        Assert.DoesNotContain(export.Rows, row =>
            Convert.ToString(row["AMOUNT"])!.Contains("<a", StringComparison.Ordinal));

        var effective = Assert.Contains("AMOUNT", plan.Formats);
        Assert.Equal("currency:USD", effective.Mask);
        Assert.Null(effective.DisplayAs);
        Assert.Null(effective.UrlColumn);
        Assert.Null(effective.Bold);
        Assert.Null(effective.Classes);
    }

    [Fact]
    public async Task Mask_and_format_source_lineage_cross_a_shape_boundary()
    {
        var document = Document(
            activeTable: "child",
            child: new ReportTable
            {
                From = "parent",
                Composables =
                [
                    new TableComposable
                    {
                        Kind = "group",
                        By = ["STATUS"],
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
                    new TableComposable { Kind = "select", Columns = ["ir1"] },
                ],
            });

        var result = await _executor.Query(Definition, document, NoParams);
        var plan = await Compile(document);

        Assert.Equal("AMOUNT", Assert.Single(result.Columns).FormatSource);
        Assert.Equal("currency:USD", plan.Formats["ir1"].Mask);
        Assert.Null(plan.Formats["ir1"].DisplayAs);
        Assert.Equal("currency:USD", plan.Export.Formats["ir1"].Mask);
        Assert.Null(plan.Export.Formats["ir1"].DisplayAs);
        Assert.DoesNotContain("AMOUNT", plan.Formats);
        Assert.DoesNotContain("AMOUNT", plan.Export.Formats);
    }

    [Fact]
    public async Task Mask_lineage_survives_chart_and_pivot_shapes()
    {
        var chartDocument = Document(
            activeTable: "child",
            child: new ReportTable
            {
                From = "parent",
                Composables =
                [
                    new TableComposable
                    {
                        Kind = "chart",
                        Type = "bar",
                        Label = "STATUS",
                        Value = "AMOUNT",
                        Fn = AggregateFn.Sum,
                    },
                ],
            });
        var pivotDocument = Document(
            activeTable: "child",
            child: new ReportTable
            {
                From = "parent",
                Composables =
                [
                    new TableComposable
                    {
                        Kind = "pivot",
                        Rows = ["CUSTOMER"],
                        Cols = ["STATUS"],
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
            });

        var chart = await _executor.Query(Definition, chartDocument, NoParams);
        var pivot = await _executor.Query(Definition, pivotDocument, NoParams);

        Assert.Equal("AMOUNT", chart.Columns[1].FormatSource);
        Assert.All(pivot.Columns.Skip(1), column => Assert.Equal("AMOUNT", column.FormatSource));
    }

    [Fact]
    public async Task Explicit_child_format_clear_removes_mask_and_lineage()
    {
        var document = Document(
            activeTable: "child",
            child: new ReportTable
            {
                From = "parent",
                Composables =
                [
                    new TableComposable
                    {
                        Kind = "group",
                        By = ["STATUS"],
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
                    new TableComposable
                    {
                        Kind = "formats",
                        Formats = new Dictionary<string, ColumnFormat>(),
                    },
                ],
            });

        var result = await _executor.Query(Definition, document, NoParams);
        var plan = await Compile(document);

        Assert.Null(result.Columns.Single(column => column.Name == "ir1").FormatSource);
        Assert.Empty(plan.Formats);
        Assert.Empty(plan.Export.Formats);
    }

    [Fact]
    public async Task Parent_active_retains_its_full_renderer_style_and_export_behavior()
    {
        var document = Document(activeTable: "parent");

        var result = await _executor.Query(Definition, document, NoParams);
        var export = await _executor.Export(Definition, document, NoParams);
        var plan = await Compile(document);

        Assert.All(result.Rows, row => Assert.Equal(["AMOUNT", "NOTES"], row.Keys));
        Assert.Contains(export.Rows, row =>
            Assert.IsType<string>(row["AMOUNT"]).Contains("<a class=", StringComparison.Ordinal));
        Assert.All(export.Rows, row =>
        {
            var text = Assert.IsType<string>(row["AMOUNT"]);
            Assert.True(
                text.StartsWith("<a", StringComparison.Ordinal)
                || text.StartsWith("$", StringComparison.Ordinal));
        });

        var effective = plan.Formats["AMOUNT"];
        Assert.Equal("currency:USD", effective.Mask);
        Assert.Equal("link", effective.DisplayAs);
        Assert.Equal("NOTES", effective.UrlColumn);
        Assert.True(effective.Bold);
        Assert.Equal(["money"], effective.Classes);

        var inherited = plan.Export.Formats["AMOUNT"];
        Assert.Equal("currency:USD", inherited.Mask);
        Assert.Null(inherited.DisplayAs);
        Assert.Null(inherited.Bold);
        Assert.Null(inherited.Classes);
    }

    [Fact]
    public async Task Derived_renderer_does_not_replace_the_format_owned_by_its_source_dimension()
    {
        var document = Document(
            activeTable: "child",
            child: new ReportTable
            {
                From = "parent",
                Composables =
                [
                    new TableComposable
                    {
                        Kind = "group",
                        By = ["AMOUNT"],
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
                    new TableComposable
                    {
                        Kind = "formats",
                        Formats = new Dictionary<string, ColumnFormat>
                        {
                            ["ir1"] = new()
                            {
                                DisplayAs = "link",
                                UrlColumn = "AMOUNT",
                                TextColumn = "ir1",
                            },
                        },
                    },
                ],
            });

        var plan = await Compile(document);

        var dimension = plan.Formats["AMOUNT"];
        Assert.Equal("currency:USD", dimension.Mask);
        Assert.Null(dimension.DisplayAs);
        Assert.Null(dimension.UrlColumn);

        var metric = plan.Formats["ir1"];
        Assert.Null(metric.Mask);
        Assert.Equal("link", metric.DisplayAs);
        Assert.Equal("AMOUNT", metric.UrlColumn);
        Assert.Null(plan.FormatSources["ir1"]);
    }

    [Fact]
    public async Task Same_table_dimension_renderer_does_not_render_its_sibling_metric()
    {
        var document = new ReportState
        {
            ActiveTable = "grouped",
            Page = new PageRequest { Index = 1, Size = 0 },
            Tables = new Dictionary<string, ReportTable>
            {
                ["grouped"] = new()
                {
                    From = "definition",
                    Composables =
                    [
                        new TableComposable
                        {
                            Kind = "group",
                            By = ["AMOUNT"],
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
                        new TableComposable
                        {
                            Kind = "formats",
                            Formats = new Dictionary<string, ColumnFormat>
                            {
                                ["AMOUNT"] = new()
                                {
                                    DisplayAs = "link",
                                    UrlColumn = "AMOUNT",
                                    TextColumn = "AMOUNT",
                                },
                            },
                        },
                        new TableComposable { Kind = "select", Columns = ["AMOUNT", "ir1"] },
                    ],
                },
            },
        };

        var export = await _executor.Export(Definition, document, NoParams);

        Assert.All(export.Rows, row =>
        {
            Assert.Contains("<a class=", Assert.IsType<string>(row["AMOUNT"]));
            Assert.IsNotType<string>(row["ir1"]);
        });
    }

    [Fact]
    public async Task Shape_format_lineage_advances_through_the_immediate_input_column()
    {
        var document = new ReportState
        {
            ActiveTable = "second",
            Page = new PageRequest { Index = 1, Size = 0 },
            Tables = new Dictionary<string, ReportTable>
            {
                ["base"] = new()
                {
                    From = "definition",
                    Composables =
                    [
                        new TableComposable
                        {
                            Kind = "formats",
                            Formats = new Dictionary<string, ColumnFormat>
                            {
                                ["AMOUNT"] = new() { Mask = "decimal-2" },
                            },
                        },
                    ],
                },
                ["first"] = new()
                {
                    From = "base",
                    Composables =
                    [
                        new TableComposable
                        {
                            Kind = "group",
                            By = ["AMOUNT"],
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
                        new TableComposable
                        {
                            Kind = "formats",
                            Formats = new Dictionary<string, ColumnFormat>
                            {
                                ["AMOUNT"] = new() { Mask = "integer" },
                            },
                        },
                    ],
                },
                ["second"] = new()
                {
                    From = "first",
                    Composables =
                    [
                        new TableComposable
                        {
                            Kind = "group",
                            By = ["ir1"],
                            Values =
                            [
                                new MetricRule
                                {
                                    Id = "ir2",
                                    Col = "ir1",
                                    Fn = AggregateFn.Sum,
                                },
                            ],
                        },
                    ],
                },
            },
        };

        var result = await _executor.Query(Definition, document, NoParams);
        var schema = result.Document!.Tables!["second"].Schema!;

        Assert.Equal("ir1", schema.Single(column => column.Name == "ir2").FormatSource);
    }

    private async Task<CompiledComposableTable> Compile(ReportState document)
    {
        var schema = await _executor.GetSchema(Definition, NoParams);
        var compiler = new ComposableTableCompiler(
            Definition,
            document,
            schema,
            EvaluationUtcNow,
            (_, _, _, _, _) => Task.FromException<List<PivotGroup>>(
                new InvalidOperationException("Pivot discovery is not expected.")));
        return compiler.CompleteForTarget(
            await compiler.Compile(document.ActiveTable!, default));
    }

    private static ReportState Document(string activeTable, ReportTable? child = null)
    {
        var tables = new Dictionary<string, ReportTable>(StringComparer.OrdinalIgnoreCase)
        {
            ["parent"] = new()
            {
                From = "definition",
                Composables =
                [
                    new TableComposable
                    {
                        Kind = "formats",
                        Formats = new Dictionary<string, ColumnFormat>
                        {
                            ["AMOUNT"] = new()
                            {
                                Mask = "currency:USD",
                                Align = "right",
                                Bold = true,
                                Italic = true,
                                Fg = "#111111",
                                Bg = "#eeeeee",
                                Classes = ["money"],
                                DisplayAs = "link",
                                UrlColumn = "NOTES",
                                TextColumn = "AMOUNT",
                                Command = "ignored-for-link",
                                KeyColumn = "CUSTOMER",
                            },
                        },
                    },
                    new TableComposable { Kind = "select", Columns = ["AMOUNT"] },
                ],
            },
        };
        if (child is not null) tables["child"] = child;

        return new ReportState
        {
            ActiveTable = activeTable,
            Page = new PageRequest { Index = 1, Size = 0 },
            Tables = tables,
        };
    }
}
