using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Schema;

namespace InteractiveReport.Core.Tests;

/// <summary>
/// Public-surface acceptance coverage for named tables as recursively composed
/// relations. Each child consumes its parent's completed relation; a table name and
/// the number or kind of shapes in its ancestry do not select a different pipeline.
/// </summary>
public sealed class RecursiveTableCompositionTests : IClassFixture<SqliteE2EFixture>
{
    private const string ShippedPivotCell = "m1@[\"SHIPPED\"]";

    private static readonly IReadOnlyDictionary<string, object?> NoParams =
        new Dictionary<string, object?>();

    private readonly ReportExecutor _executor;

    public RecursiveTableCompositionTests(SqliteE2EFixture database)
    {
        _executor = new ReportExecutor(database, new SchemaCache());
    }

    private ReportDefinition Definition => new()
    {
        Name = "recursive-table-composition",
        Connection = "E2E",
        Dialect = ReportDialect.Sqlite,
        Sql = "SELECT ORDER_ID, CUSTOMER, STATUS, AMOUNT, NOTES FROM ORDERS",
    };

    [Fact]
    public async Task Thirty_table_empty_and_filter_chain_executes()
    {
        var tables = new Dictionary<string, ReportTable>();
        for (var index = 0; index < 30; index++)
        {
            var composables = index % 2 == 0
                ? new List<TableComposable>()
                :
                [
                    new TableComposable
                    {
                        Kind = "filter",
                        Filters = [new FilterRule { Expr = "AMOUNT >= 0" }],
                    },
                ];
            tables[$"t{index}"] = Table(
                index == 0 ? "definition" : $"t{index - 1}",
                composables);
        }

        var result = await _executor.Query(
            Definition,
            Document("t29", tables),
            NoParams);

        Assert.Equal(10, result.TotalRows);
        Assert.Equal(10, result.Rows.Count);
        Assert.Equal(
            Enumerable.Range(1, 10).Select(id => (long)id),
            result.Rows.Select(row => Convert.ToInt64(row["ORDER_ID"])).Order());
    }

    [Fact]
    public async Task Group_can_feed_group()
    {
        var document = Document(
            "second",
            new Dictionary<string, ReportTable>
            {
                ["first"] = Table(
                    "definition",
                    Group(["STATUS"], [Metric("m1", "AMOUNT", AggregateFn.Sum)])),
                ["second"] = Table(
                    "first",
                    Group(["STATUS"], [Metric("m2", "m1", AggregateFn.Sum)])),
            });

        var result = await _executor.Query(Definition, document, NoParams);

        Assert.Equal(4, result.TotalRows);
        Assert.Equal(["STATUS", "__count", "m2"], result.Columns.Select(column => column.Name));
        var shipped = result.Rows.Single(row => Equals(row["STATUS"], "SHIPPED"));
        Assert.Equal(1L, Convert.ToInt64(shipped["__count"]));
        Assert.Equal(26000m, Convert.ToDecimal(shipped["m2"]));
    }

    [Fact]
    public async Task Group_can_feed_chart()
    {
        var document = Document(
            "chart",
            new Dictionary<string, ReportTable>
            {
                ["group"] = Table(
                    "definition",
                    Group(["STATUS"], [Metric("m1", "AMOUNT", AggregateFn.Sum)])),
                ["chart"] = Table(
                    "group",
                    new TableComposable
                    {
                        Kind = "chart",
                        Type = "bar",
                        Label = "STATUS",
                        Value = "m1",
                    }),
            });

        var result = await _executor.Query(Definition, document, NoParams);

        Assert.Equal(4, result.TotalRows);
        Assert.Equal(["STATUS", "m1"], result.Columns.Select(column => column.Name));
        var shipped = result.Rows.Single(row => Equals(row["STATUS"], "SHIPPED"));
        Assert.Equal(26000m, Convert.ToDecimal(shipped["m1"]));
    }

    [Fact]
    public async Task Pivot_can_feed_group_using_the_stable_generated_cell_name()
    {
        var document = Document(
            "group",
            new Dictionary<string, ReportTable>
            {
                ["pivot"] = Table(
                    "definition",
                    new TableComposable
                    {
                        Kind = "pivot",
                        Rows = ["CUSTOMER"],
                        Cols = ["STATUS"],
                        Values = [Metric("m1", "AMOUNT", AggregateFn.Sum)],
                    }),
                ["group"] = Table(
                    "pivot",
                    Group(
                        ["CUSTOMER"],
                        [Metric("m2", ShippedPivotCell, AggregateFn.Sum)])),
            });

        var result = await _executor.Query(Definition, document, NoParams);

        Assert.Equal(["CUSTOMER", "__count", "m2"], result.Columns.Select(column => column.Name));
        var acme = result.Rows.Single(row => Equals(row["CUSTOMER"], "Acme Corp"));
        Assert.Equal(12000m, Convert.ToDecimal(acme["m2"]));
    }

    [Fact]
    public async Task Document_table_limit_reports_a_clean_bounded_validation_error()
    {
        var tables = new Dictionary<string, ReportTable>();
        for (var index = 0; index < 65; index++)
            tables[$"t{index}"] = Table(
                index == 0 ? "definition" : $"t{index - 1}");

        var exception = await Assert.ThrowsAsync<ReportValidationException>(() =>
            _executor.Query(Definition, Document("t64", tables), NoParams));

        var error = Assert.Single(exception.Errors);
        Assert.Equal("tables", error.Path);
        Assert.Contains("at most 64 tables", error.Message);
    }

    [Fact]
    public async Task Cycle_error_points_to_the_from_edge_that_closes_the_cycle()
    {
        var document = Document(
            "a",
            new Dictionary<string, ReportTable>
            {
                ["a"] = Table("b"),
                ["b"] = Table("a"),
            });

        var exception = await Assert.ThrowsAsync<ReportValidationException>(() =>
            _executor.Query(Definition, document, NoParams));

        Assert.Contains(
            exception.Errors,
            error => error.Path == "tables.b.from"
                && error.Message == "table delegation contains a cycle at 'a'");
    }

    [Fact]
    public async Task Parent_terminal_select_and_sort_do_not_change_the_child_relation()
    {
        var document = Document(
            "child",
            new Dictionary<string, ReportTable>
            {
                ["parent"] = Table(
                    "definition",
                    new TableComposable { Kind = "select", Columns = ["CUSTOMER"] },
                    new TableComposable
                    {
                        Kind = "sort",
                        Sorts = [new SortRule { Col = "ORDER_ID", Dir = SortDir.Desc }],
                    }),
                ["child"] = Table(
                    "parent",
                    new TableComposable
                    {
                        Kind = "filter",
                        Filters = [new FilterRule { Expr = "AMOUNT >= 0" }],
                    },
                    new TableComposable
                    {
                        Kind = "sort",
                        Sorts = [new SortRule { Col = "ORDER_ID", Dir = SortDir.Asc }],
                    }),
            });

        var result = await _executor.Query(Definition, document, NoParams);

        Assert.Equal(
            ["ORDER_ID", "CUSTOMER", "STATUS", "AMOUNT", "NOTES"],
            result.Columns.Select(column => column.Name));
        Assert.Equal(
            Enumerable.Range(1, 10).Select(id => (long)id),
            result.Rows.Select(row => Convert.ToInt64(row["ORDER_ID"])));
    }

    [Fact]
    public async Task Unshaped_child_of_a_shape_does_not_project_grid_only_edit_link_data()
    {
        var definition = Definition;
        definition.EditLink = new ReportEditLink
        {
            UrlTemplate = "/orders/{ORDER_ID}/edit",
        };
        var document = Document(
            "child",
            new Dictionary<string, ReportTable>
            {
                ["grouped"] = Table("definition", Group(["ORDER_ID"], [])),
                ["child"] = Table(
                    "grouped",
                    new TableComposable { Kind = "select", Columns = ["__count"] }),
            });

        var result = await _executor.Query(definition, document, NoParams);

        Assert.Equal(["__count"], result.Columns.Select(column => column.Name));
        Assert.All(result.Rows, row =>
        {
            Assert.True(row.ContainsKey("__count"));
            Assert.False(row.ContainsKey("ORDER_ID"));
        });
    }

    private static ReportState Document(
        string activeTable,
        Dictionary<string, ReportTable> tables)
        => new()
        {
            ActiveTable = activeTable,
            Tables = tables,
            Page = new PageRequest { Index = 1, Size = 0 },
        };

    private static ReportTable Table(
        string from,
        params TableComposable[] composables)
        => Table(from, composables.ToList());

    private static ReportTable Table(
        string from,
        List<TableComposable> composables)
        => new()
        {
            From = from,
            Composables = composables,
        };

    private static TableComposable Group(
        List<string> by,
        List<MetricRule> values)
        => new()
        {
            Kind = "group",
            By = by,
            Values = values,
        };

    private static MetricRule Metric(string id, string column, AggregateFn function)
        => new()
        {
            Id = id,
            Col = column,
            Fn = function,
        };
}
