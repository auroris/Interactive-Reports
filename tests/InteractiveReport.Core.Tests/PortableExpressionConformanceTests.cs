using System.Globalization;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Schema;
using static InteractiveReport.Core.Tests.TestFixtures;

namespace InteractiveReport.Core.Tests;

/// <summary>
/// Runs the portable expression contract through both execution strategies. Grid and
/// Group ordinary layers stay in SQL; Pivot and Chart ordinary layers are evaluated
/// over materialized rows.
/// </summary>
public sealed class PortableExpressionConformanceTests : IClassFixture<SqliteE2EFixture>
{
    private static readonly IReadOnlyDictionary<string, object?> NoParams =
        new Dictionary<string, object?>();

    private readonly ReportExecutor _executor;

    public PortableExpressionConformanceTests(SqliteE2EFixture database)
        => _executor = new ReportExecutor(database, new SchemaCache());

    private static ReportDefinition Definition => new()
    {
        Name = "portable-expression-conformance",
        Connection = "E2E",
        Dialect = ReportDialect.Sqlite,
        Sql = "SELECT ORDER_ID, CUSTOMER, STATUS, AMOUNT, NOTES FROM ORDERS",
    };

    [Fact]
    public async Task Division_is_decimal_in_sql_and_materialized_table_layers()
    {
        var grid = await _executor.Query(Definition, Doc(source: new StageLayer
        {
            Computed = [new ComputedColumn { Id = "c1", Expr = "ORDER_ID / 2" }],
            Sorts = [new SortRule { Col = "ORDER_ID" }],
            Columns = ["ORDER_ID", "c1"],
        }), NoParams);

        Assert.Equal(0.5m, Number(grid.Rows[0]["c1"]));

        var group = await _executor.Query(Definition, Doc(tail:
        [
            Group(
                by: ["STATUS"],
                layer: new StageLayer
                {
                    Computed = [new ComputedColumn { Id = "c2", Expr = "__count / 2" }],
                    Sorts = [new SortRule { Col = "STATUS" }],
                    Columns = ["STATUS", "c2"],
                }),
        ]), NoParams);

        var chart = ChartStage(shape =>
        {
            shape.Type = "bar";
            shape.Label = "STATUS";
            shape.Fn = AggregateFn.Count;
        });
        chart.Composables!.AddRange(
        [
            new TableComposable
            {
                Kind = "compute",
                Computed = [new ComputedColumn { Id = "c2", Expr = "__count / 2" }],
            },
            new TableComposable { Kind = "sort", Sorts = [new SortRule { Col = "STATUS" }] },
            new TableComposable { Kind = "select", Columns = ["STATUS", "c2"] },
        ]);
        var chartResult = await _executor.Query(Definition, Doc(tail: [chart]), NoParams);

        Assert.Equal(
            NumericByKey(group.Rows, "STATUS", "c2"),
            NumericByKey(chartResult.Rows, "STATUS", "c2"));
        Assert.Equal(2.5m, NumericByKey(group.Rows, "STATUS", "c2")["SHIPPED"]);

        const string shippedCount = "__count@[\"SHIPPED\"]";
        var pivot = await _executor.Query(Definition, Doc(tail:
        [
            Pivot(
                rows: ["CUSTOMER"],
                cols: ["STATUS"],
                layer: new StageLayer
                {
                    Computed =
                    [
                        new ComputedColumn
                        {
                            Id = "c2",
                            Expr = $"COALESCE(`{shippedCount}`, 0) / 2",
                        },
                    ],
                    Columns = ["CUSTOMER", "c2"],
                }),
        ]), NoParams);

        Assert.Equal(0.5m, Number(pivot.Rows.Single(row => Equals(row["CUSTOMER"], "Globex"))["c2"]));
    }

    [Fact]
    public async Task Now_is_one_utc_instant_across_sql_and_materialized_layers()
    {
        const string format = "YYYY-MM-DDTHH24:MI:SS";
        var result = await _executor.Query(Definition, Doc(
            source: new StageLayer
            {
                Computed =
                [
                    new ComputedColumn
                    {
                        Id = "c1",
                        Expr = $"TO_STRING(NOW(), '{format}')",
                    },
                ],
            },
            tail:
            [
                Pivot(
                    rows: ["CUSTOMER", "c1"],
                    cols: ["STATUS"],
                    layer: new StageLayer
                    {
                        Computed =
                        [
                            new ComputedColumn
                            {
                                Id = "c2",
                                Expr = $"TO_STRING(NOW(), '{format}')",
                            },
                        ],
                        Columns = ["CUSTOMER", "c1", "c2"],
                    }),
            ]), NoParams);

        Assert.Equal(9, result.Rows.Count);
        Assert.Single(result.Rows.Select(row => row["c2"]).Distinct());
        Assert.All(result.Rows, row => Assert.Equal(row["c1"], row["c2"]));
    }

    [Fact]
    public async Task Ordinal_text_filter_and_sort_match_on_binary_collated_sql_and_materialized_paths()
    {
        // SQL has no collation name or syntax shared by every supported provider.
        // SQL-backed layers therefore inherit the report database collation, while
        // materialized layers are explicitly ordinal. SQLite BINARY is the matching
        // SQL contract exercised here; other databases need an ordinal/binary report
        // collation when exact cross-path text ordering is required.
        var grid = await _executor.Query(Definition, Doc(source: new StageLayer
        {
            Filters = [Filter("CUSTOMER >= 'Globex'")],
            Sorts = [new SortRule { Col = "CUSTOMER" }],
            Columns = ["CUSTOMER"],
        }), NoParams);

        var chart = ChartStage(shape =>
        {
            shape.Type = "bar";
            shape.Label = "CUSTOMER";
            shape.Fn = AggregateFn.Count;
        });
        chart.Composables!.AddRange(
        [
            new TableComposable { Kind = "filter", Filters = [Filter("CUSTOMER >= 'Globex'")] },
            new TableComposable { Kind = "sort", Sorts = [new SortRule { Col = "CUSTOMER" }] },
            new TableComposable { Kind = "select", Columns = ["CUSTOMER"] },
        ]);
        var materialized = await _executor.Query(Definition, Doc(tail: [chart]), NoParams);

        Assert.Equal(
            grid.Rows.Select(row => row["CUSTOMER"]),
            materialized.Rows.Select(row => row["CUSTOMER"]));
        Assert.Equal("acme llc", materialized.Rows[^1]["CUSTOMER"]);
    }

    private static decimal Number(object? value)
        => Convert.ToDecimal(value, CultureInfo.InvariantCulture);

    private static Dictionary<string, decimal> NumericByKey(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        string key,
        string value)
        => rows.ToDictionary(
            row => Convert.ToString(row[key], CultureInfo.InvariantCulture)!,
            row => Number(row[value]),
            StringComparer.Ordinal);
}
