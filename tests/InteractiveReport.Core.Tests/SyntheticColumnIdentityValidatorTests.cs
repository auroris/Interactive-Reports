using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Planning;
using InteractiveReport.Core.Schema;

namespace InteractiveReport.Core.Tests;

public sealed class SyntheticColumnIdentityValidatorTests : IClassFixture<SqliteE2EFixture>
{
    private static readonly IReadOnlyDictionary<string, object?> NoParams =
        new Dictionary<string, object?>();

    private readonly ReportExecutor _executor;

    public SyntheticColumnIdentityValidatorTests(SqliteE2EFixture database)
        => _executor = new ReportExecutor(database, new SchemaCache());

    private static ReportDefinition Definition => new()
    {
        Name = "synthetic-column-identity",
        Connection = "E2E",
        Dialect = ReportDialect.Sqlite,
        Sql = "SELECT ORDER_ID, CUSTOMER, STATUS, AMOUNT, NOTES FROM ORDERS",
    };

    [Fact]
    public void Collect_rejects_retired_and_nonpositive_ids_even_in_dormant_tables()
    {
        var document = new ReportState
        {
            Tables = new Dictionary<string, ReportTable>
            {
                ["computed"] = new()
                {
                    From = "definition",
                    Composables =
                    [
                        new TableComposable
                        {
                            Kind = "compute",
                            Computed =
                            [
                                new ComputedColumn
                                {
                                    Id = "c1",
                                    Expr = "AMOUNT + 1",
                                    Enabled = false,
                                },
                            ],
                        },
                    ],
                },
                ["group"] = new()
                {
                    From = "definition",
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
                                    Id = "m1",
                                    Col = "AMOUNT",
                                    Fn = AggregateFn.Sum,
                                },
                            ],
                        },
                    ],
                },
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
                            Values =
                            [
                                new MetricRule
                                {
                                    Id = "ir0",
                                    Col = "AMOUNT",
                                    Fn = AggregateFn.Sum,
                                },
                                new MetricRule
                                {
                                    Id = "IR1",
                                    Col = "AMOUNT",
                                    Fn = AggregateFn.Avg,
                                },
                            ],
                        },
                    ],
                },
            },
        };

        var errors = SyntheticColumnIdentityValidator.Collect(document);

        Assert.Equal(4, errors.Count);
        Assert.Equal(
            [
                "tables.computed.composables[0].computed[0].id",
                "tables.group.composables[0].values[0].id",
                "tables.pivot.composables[0].values[0].id",
                "tables.pivot.composables[0].values[1].id",
            ],
            errors.Select(error => error.Path).Order());
        Assert.All(errors, error => Assert.Contains("canonical irN namespace", error.Message));
    }

    [Fact]
    public async Task Query_rejects_blank_ids_in_a_dormant_table_with_a_populated_cache()
    {
        var document = new ReportState
        {
            ActiveTable = "active",
            Tables = new Dictionary<string, ReportTable>
            {
                ["active"] = new()
                {
                    From = "definition",
                    Composables = [],
                },
                ["dormant"] = new()
                {
                    From = "definition",
                    Schema = [new ColumnInfo("AMOUNT", "Amount", "number", false)],
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
                                    Id = " ",
                                    Col = "AMOUNT",
                                    Fn = AggregateFn.Sum,
                                },
                            ],
                        },
                        new TableComposable
                        {
                            Kind = "compute",
                            Computed = [new ComputedColumn { Id = "\t", Expr = "AMOUNT + 1" }],
                        },
                    ],
                },
            },
        };

        var exception = await Assert.ThrowsAsync<ReportValidationException>(() =>
            _executor.Query(Definition, document, NoParams));

        Assert.Equal(
            [
                "tables.dormant.composables[0].values[0].id",
                "tables.dormant.composables[1].computed[0].id",
            ],
            exception.Errors.Select(error => error.Path).Order());
        Assert.All(exception.Errors, error =>
            Assert.Contains("canonical irN namespace", error.Message, StringComparison.Ordinal));
    }

    [Fact]
    public void Collect_reserves_ids_across_kinds_siblings_and_disabled_rules()
    {
        var document = new ReportState
        {
            Tables = new Dictionary<string, ReportTable>
            {
                // Deliberately insert in reverse lexical order. Table map order has
                // no semantic or diagnostic precedence.
                ["zComputed"] = new()
                {
                    From = "definition",
                    Composables =
                    [
                        new TableComposable
                        {
                            Kind = "compute",
                            Computed =
                            [
                                new ComputedColumn
                                {
                                    Id = "ir1",
                                    Expr = "AMOUNT + 1",
                                    Enabled = false,
                                },
                            ],
                        },
                    ],
                },
                ["aGroup"] = new()
                {
                    From = "definition",
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
                    ],
                },
                ["mPivot"] = new()
                {
                    From = "definition",
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
                                    Fn = AggregateFn.Avg,
                                },
                            ],
                        },
                    ],
                },
            },
        };

        var errors = SyntheticColumnIdentityValidator.Collect(document);

        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, error =>
            error.Path == "tables.mPivot.composables[0].values[0].id"
            && error.Message.Contains(
                "tables.aGroup.composables[0].values[0].id",
                StringComparison.Ordinal));
        Assert.Contains(errors, error =>
            error.Path == "tables.zComputed.composables[0].computed[0].id"
            && error.Message.Contains("document-wide namespace", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Query_rejects_a_post_pivot_computed_id_that_reuses_the_metric_id(
        bool computedStoredFirst)
    {
        var pivot = new TableComposable
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
        };
        var compute = new TableComposable
        {
            Kind = "compute",
            Computed = [new ComputedColumn { Id = "ir1", Expr = "1" }],
        };
        var document = new ReportState
        {
            ActiveTable = "result",
            Tables = new Dictionary<string, ReportTable>
            {
                ["result"] = new()
                {
                    From = "definition",
                    Composables = computedStoredFirst ? [compute, pivot] : [pivot, compute],
                },
            },
        };

        var exception = await Assert.ThrowsAsync<ReportValidationException>(() =>
            _executor.Query(Definition, document, NoParams));

        var error = Assert.Single(exception.Errors);
        var computedIndex = computedStoredFirst ? 0 : 1;
        var pivotIndex = computedStoredFirst ? 1 : 0;
        Assert.Equal($"tables.result.composables[{computedIndex}].computed[0].id", error.Path);
        Assert.Contains(
            $"tables.result.composables[{pivotIndex}].values[0].id",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_rejects_a_collision_in_a_dormant_sibling()
    {
        var document = new ReportState
        {
            ActiveTable = "active",
            Tables = new Dictionary<string, ReportTable>
            {
                ["active"] = new()
                {
                    From = "definition",
                    Composables =
                    [
                        new TableComposable
                        {
                            Kind = "compute",
                            Computed = [new ComputedColumn { Id = "ir2", Expr = "AMOUNT + 1" }],
                        },
                    ],
                },
                ["dormant"] = new()
                {
                    From = "definition",
                    Schema = [new ColumnInfo("FORGED", "Forged", "text", false)],
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
                                    Id = "ir2",
                                    Col = "AMOUNT",
                                    Fn = AggregateFn.Sum,
                                },
                            ],
                        },
                    ],
                },
            },
        };

        var exception = await Assert.ThrowsAsync<ReportValidationException>(() =>
            _executor.Export(Definition, document, NoParams));

        var error = Assert.Single(exception.Errors);
        Assert.Equal("tables.dormant.composables[0].values[0].id", error.Path);
        Assert.Contains(
            "tables.active.composables[0].computed[0].id",
            error.Message,
            StringComparison.Ordinal);
    }
}
