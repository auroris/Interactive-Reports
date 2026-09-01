using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Schema;
using InteractiveReport.Core.Validation;
using static InteractiveReport.Core.Tests.TestFixtures;

namespace InteractiveReport.Core.Tests;

public sealed class ReportDocumentSchemaCacheTests : IClassFixture<SqliteE2EFixture>
{
    private static readonly IReadOnlyDictionary<string, object?> NoParams =
        new Dictionary<string, object?>();

    private readonly ReportExecutor _executor;

    public ReportDocumentSchemaCacheTests(SqliteE2EFixture database)
        => _executor = new ReportExecutor(database, new SchemaCache());

    private static ReportDefinition Definition()
        => new()
        {
            Name = "schema-cache-e2e",
            Connection = "E2E",
            Dialect = ReportDialect.Sqlite,
            Sql = "SELECT ORDER_ID, CUSTOMER, STATUS, AMOUNT, NOTES FROM ORDERS",
        };

    [Fact]
    public async Task Query_returns_a_detached_document_with_every_null_table_cache_filled()
    {
        var document = Doc(
            tail:
            [
                Group(
                    by: ["STATUS"],
                    values: [Metric("ir1", "AMOUNT", AggregateFn.Sum)]),
            ],
            alternatives: new()
            {
                ["customerPivot"] =
                [
                    Pivot(
                        rows: ["CUSTOMER"],
                        cols: ["STATUS"],
                        values: [Metric("ir2", "AMOUNT", AggregateFn.Sum)]),
                ],
            });

        Assert.All(document.Tables!, table => Assert.Null(table.Value.Schema));

        var result = await _executor.Query(Definition(), document, NoParams);

        var returned = Assert.IsType<ReportState>(result.Document);
        var returnedTables = Assert.IsType<Dictionary<string, ReportTable>>(returned.Tables);
        Assert.NotSame(document, returned);
        Assert.NotSame(document.Tables, returned.Tables);
        Assert.Equal(document.ActiveTable, returned.ActiveTable);
        Assert.All(document.Tables!, table => Assert.Null(table.Value.Schema));
        Assert.All(returned.Tables!, table => Assert.NotNull(table.Value.Schema));

        Assert.Equal(
            ["ORDER_ID", "CUSTOMER", "STATUS", "AMOUNT", "NOTES"],
            returnedTables["source"].Schema!.Select(column => column.Name));
        Assert.Equal(
            ["STATUS", "__count", "ir1"],
            returnedTables[returned.ActiveTable!].Schema!.Select(column => column.Name));
        Assert.Equal(
            result.AvailableColumns.Select(column => column.Name),
            returnedTables[returned.ActiveTable!].Schema!.Select(column => column.Name));
        Assert.NotEmpty(returnedTables["customerPivot"].Schema!);
    }

    [Fact]
    public async Task Active_grid_does_not_execute_an_inactive_high_cardinality_chart_to_fill_its_cache()
    {
        var definition = Definition();
        definition.MaxChartPoints = 3;
        var document = Doc(alternatives: new()
        {
            ["customerChart"] =
            [
                ChartStage(shape =>
                {
                    shape.Type = "bar";
                    shape.Label = "CUSTOMER";
                    shape.Fn = AggregateFn.Count;
                }),
            ],
        });

        Assert.Equal("source", document.ActiveTable);
        Assert.Null(document.Tables!["customerChart"].Schema);

        // Nine customer groups exceed the chart's three-point runtime limit. The
        // active grid still succeeds because chart cache refresh is schema-only.
        var result = await _executor.Query(definition, document, NoParams);

        Assert.NotEmpty(result.Rows);
        Assert.Equal("source", result.Document!.ActiveTable);
        Assert.Equal(
            ["CUSTOMER", "__count"],
            result.Document.Tables!["customerChart"].Schema!.Select(column => column.Name));
    }

    [Fact]
    public async Task Compiled_non_null_cache_is_replaced_and_cannot_override_live_schema_or_column_policy()
    {
        var definition = Definition();
        definition.Columns = new()
        {
            ["AMOUNT"] = new ReportColumnOverride { Filterable = false },
        };
        var document = Doc(source: new StageLayer
        {
            Filters = [Filter("AMOUNT > 10000")],
        });
        document.Tables!["source"].Schema =
        [
            new ColumnInfo("GHOST", "Forged", "text", false),
        ];

        var result = await _executor.Query(definition, document, NoParams);

        Assert.Equal(10, result.TotalRows);
        Assert.Contains(result.Ignored, item =>
            item.Kind == "filter" && item.Detail.Contains("AMOUNT", StringComparison.Ordinal));
        Assert.DoesNotContain(result.AvailableColumns, column => column.Name == "GHOST");
        Assert.Equal(
            ["ORDER_ID", "CUSTOMER", "STATUS", "AMOUNT", "NOTES"],
            result.Document!.Tables!["source"].Schema!.Select(column => column.Name));
    }

    [Fact]
    public async Task Forged_cached_column_cannot_bind_an_expression()
    {
        var document = Doc(source: new StageLayer
        {
            Filters = [Filter("GHOST = 'trusted'")],
        });
        document.Tables!["source"].Schema =
        [
            new ColumnInfo("GHOST", "Forged", "text", false),
        ];

        var error = await Assert.ThrowsAsync<ReportValidationException>(
            () => _executor.Query(Definition(), document, NoParams));

        Assert.Contains(error.Errors, item =>
            item.Path == "tables.source.composables[0].filters[0].expr"
            && item.Message.Contains("GHOST", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Executor_leaves_the_caller_document_unchanged_and_returns_a_canonical_copy()
    {
        var document = Doc();
        document.ActiveTable = "  SoUrCe  ";
        document.Tables!["source"].From = "  DeFiNiTiOn  ";
        document.Tables["source"].Composables =
        [
            new TableComposable
            {
                Kind = "  FiLtEr  ",
                Filters = [Filter("AMOUNT >= 0")],
            },
        ];
        var callerTables = document.Tables!;
        var callerTable = callerTables["source"];
        var callerComposables = callerTable.Composables;

        var result = await _executor.Query(Definition(), document, NoParams);
        var export = await _executor.Download(Definition(), document, NoParams);

        Assert.Equal("  SoUrCe  ", document.ActiveTable);
        Assert.Same(callerTables, document.Tables);
        Assert.Same(callerTable, document.Tables!["source"]);
        Assert.Same(callerComposables, document.Tables["source"].Composables);
        Assert.Equal("  DeFiNiTiOn  ", document.Tables["source"].From);
        Assert.Equal("  FiLtEr  ", Assert.Single(document.Tables["source"].Composables!).Kind);
        Assert.Null(document.Tables["source"].Schema);

        Assert.NotSame(document, result.Document);
        Assert.NotSame(callerTables, result.Document!.Tables);
        Assert.NotSame(callerTable, result.Document.Tables!["source"]);
        Assert.NotSame(callerComposables, result.Document.Tables["source"].Composables);
        Assert.Equal("source", result.Document!.ActiveTable);
        Assert.Equal("definition", result.Document.Tables!["source"].From);
        Assert.Equal("filter", Assert.Single(result.Document.Tables["source"].Composables!).Kind);
        Assert.NotNull(result.Document.Tables["source"].Schema);
        Assert.Equal(10, result.TotalRows);
        Assert.Equal(10, export.Rows.Count);
    }

    [Fact]
    public async Task Fully_drifted_selection_falls_back_to_the_live_schema()
    {
        var document = Doc(source: new StageLayer { Columns = ["GHOST"] });

        var result = await _executor.Query(Definition(), document, NoParams);
        var export = await _executor.Download(Definition(), document, NoParams);

        Assert.Equal(
            ["ORDER_ID", "CUSTOMER", "STATUS", "AMOUNT", "NOTES"],
            result.Columns.Select(column => column.Name));
        Assert.Equal(10, result.Rows.Count);
        Assert.Contains(result.Ignored, item =>
            item.Kind == "column" && item.Detail.Contains("GHOST", StringComparison.Ordinal));
        Assert.Equal(result.Columns.Select(column => column.Name), export.Columns.Select(column => column.Name));
        Assert.Equal(10, export.Rows.Count);
    }

    [Fact]
    public async Task Partial_request_returns_the_effective_default_document_with_filled_caches()
    {
        var definition = Definition();
        definition.DefaultState = Doc(tail:
        [
            Group(
                by: ["STATUS"],
                values: [Metric("ir1", "AMOUNT", AggregateFn.Sum)]),
        ]);
        var partial = new ReportState
        {
            Page = new PageRequest { Index = 1, Size = 2 },
        };

        var result = await _executor.Query(definition, partial, NoParams);

        Assert.Equal(definition.DefaultState.ActiveTable, result.Document!.ActiveTable);
        Assert.Equal(definition.DefaultState.Tables!.Keys, result.Document.Tables!.Keys);
        Assert.All(result.Document.Tables, entry => Assert.NotNull(entry.Value.Schema));
        Assert.Equal(2, result.Rows.Count);
        Assert.Null(partial.Tables);
    }

    [Fact]
    public void Resolver_deep_copies_per_table_schema_caches()
    {
        var document = Doc();
        document.Tables!["source"].Schema =
        [
            new ColumnInfo("AMOUNT", "Total", "number", false) { FormatSource = "AMOUNT" },
        ];

        var resolved = ReportStateResolver.Resolve(null, document);

        var original = Assert.Single(document.Tables["source"].Schema!);
        var copy = Assert.Single(resolved.Tables!["source"].Schema!);
        Assert.NotSame(document.Tables, resolved.Tables);
        Assert.NotSame(document.Tables["source"].Schema, resolved.Tables["source"].Schema);
        Assert.NotSame(original, copy);
        Assert.Equal(original, copy);
    }
}
