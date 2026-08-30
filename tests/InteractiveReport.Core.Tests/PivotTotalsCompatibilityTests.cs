using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Schema;

namespace InteractiveReport.Core.Tests;

public sealed class PivotTotalsCompatibilityTests : IClassFixture<SqliteE2EFixture>
{
    private static readonly IReadOnlyDictionary<string, object?> NoParams =
        new Dictionary<string, object?>();

    private readonly ReportExecutor _executor;

    public PivotTotalsCompatibilityTests(SqliteE2EFixture database)
        => _executor = new ReportExecutor(database, new SchemaCache());

    private static ReportDefinition Definition => new()
    {
        Name = "pivot-totals-compatibility",
        Connection = "E2E",
        Dialect = ReportDialect.Sqlite,
        Sql = "SELECT CUSTOMER, STATUS, AMOUNT FROM ORDERS",
    };

    [Fact]
    public async Task Totals_reject_same_table_computed_columns_at_the_pivot_source_path()
    {
        var document = Document(
        [
            new TableComposable
            {
                Kind = "compute",
                Computed = [new ComputedColumn { Id = "ir1", Expr = "CUSTOMER || '!'" }],
            },
            Pivot(totals: true),
        ]);

        var exception = await Assert.ThrowsAsync<ReportValidationException>(
            () => _executor.Query(Definition, document, NoParams));

        Assert.Contains(exception.Errors, error =>
            error.Path == "tables.result.composables[1].totals"
            && error.Message.Contains("computed columns", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Totals_reject_same_table_filters_at_the_pivot_source_path()
    {
        var document = Document(
        [
            new TableComposable
            {
                Kind = "filter",
                Filters = [new FilterRule { Expr = "CUSTOMER = 'Acme Corp'" }],
            },
            Pivot(totals: true),
        ]);

        var exception = await Assert.ThrowsAsync<ReportValidationException>(
            () => _executor.Query(Definition, document, NoParams));

        Assert.Contains(exception.Errors, error =>
            error.Path == "tables.result.composables[1].totals"
            && error.Message.Contains("filters", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Totals_allow_same_table_filters_when_column_policy_strips_them_all()
    {
        var definition = Definition;
        definition.Columns = new Dictionary<string, ReportColumnOverride>(StringComparer.OrdinalIgnoreCase)
        {
            ["CUSTOMER"] = new() { Filterable = false },
        };
        var document = Document(
        [
            new TableComposable
            {
                Kind = "filter",
                Filters = [new FilterRule { Expr = "CUSTOMER = 'Acme Corp'" }],
            },
            Pivot(totals: true),
        ]);

        var result = await _executor.Query(definition, document, NoParams);

        Assert.NotEmpty(result.Rows);
        Assert.NotEmpty(result.Aggregates);
        Assert.Equal(
            new IgnoredItem("filter", "filter references non-filterable column 'CUSTOMER'"),
            Assert.Single(result.Ignored));
    }

    [Fact]
    public async Task Totals_reject_request_search_at_the_pivot_source_path()
    {
        var document = Document([Pivot(totals: true)]);
        document.Search = "Acme";

        var exception = await Assert.ThrowsAsync<ReportValidationException>(
            () => _executor.Query(Definition, document, NoParams));

        Assert.Contains(exception.Errors, error =>
            error.Path == "tables.result.composables[0].totals"
            && error.Message.Contains("request search", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Parent_filter_remains_compatible_with_child_pivot_totals()
    {
        var document = new ReportState
        {
            ActiveTable = "result",
            Tables = new Dictionary<string, ReportTable>
            {
                ["source"] = new()
                {
                    From = "definition",
                    Composables =
                    [
                        new TableComposable
                        {
                            Kind = "filter",
                            Filters = [new FilterRule { Expr = "STATUS = 'SHIPPED'" }],
                        },
                    ],
                },
                ["result"] = new()
                {
                    From = "source",
                    Composables = [Pivot(totals: true)],
                },
            },
        };

        var result = await _executor.Query(Definition, document, NoParams);

        var shipped = TestFixtures.PivotCellId("result", "ir9", "SHIPPED");
        Assert.Equal(["CUSTOMER", shipped], result.Columns.Select(column => column.Name));
        Assert.Equal(4, result.Rows.Count);
        Assert.All(result.Rows, row => Assert.True(row.ContainsKey(shipped)));
        var total = Assert.Single(result.Aggregates);
        Assert.Equal(shipped, total.Key);
        Assert.Equal(26000m, Convert.ToDecimal(total.Value["sum"]));
    }

    private static TableComposable Pivot(bool totals) => new()
    {
        Kind = "pivot",
        Rows = ["CUSTOMER"],
        Cols = ["STATUS"],
        Values = [new MetricRule { Id = "ir9", Col = "AMOUNT", Fn = AggregateFn.Sum }],
        Totals = totals,
    };

    private static ReportState Document(List<TableComposable> composables)
        => new()
        {
            ActiveTable = "result",
            Tables = new Dictionary<string, ReportTable>
            {
                ["result"] = new()
                {
                    From = "definition",
                    Composables = composables,
                },
            },
        };
}
