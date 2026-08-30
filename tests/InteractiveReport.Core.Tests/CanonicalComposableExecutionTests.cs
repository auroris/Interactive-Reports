using System.Globalization;
using InteractiveReport.Core.Composition;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Planning;
using InteractiveReport.Core.Schema;

namespace InteractiveReport.Core.Tests;

/// <summary>
/// End-to-end characterization of the canonical composable schedule. Array position
/// is serialization detail; operation semantics and expression dependencies determine
/// the SQL plan.
/// </summary>
public sealed class CanonicalComposableExecutionTests : IClassFixture<SqliteE2EFixture>
{
    private static readonly IReadOnlyDictionary<string, object?> NoParams =
        new Dictionary<string, object?>();

    private static readonly DateTime EvaluationUtcNow =
        new(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc);

    private readonly ReportExecutor _executor;

    public CanonicalComposableExecutionTests(SqliteE2EFixture database)
        => _executor = new ReportExecutor(database, new SchemaCache());

    private static ReportDefinition Definition => new()
    {
        Name = "canonical-composable-execution",
        Connection = "E2E",
        Dialect = ReportDialect.Sqlite,
        Sql = "SELECT ORDER_ID, CUSTOMER, STATUS, AMOUNT, NOTES FROM ORDERS",
    };

    [Fact]
    public async Task Composable_array_permutations_produce_equivalent_sql_and_results()
    {
        var naturalSql = await CompilePageSql(GridDocument(permuted: false));
        var permutedSql = await CompilePageSql(GridDocument(permuted: true));

        Assert.Equal(naturalSql.Sql, permutedSql.Sql);
        Assert.Equal(naturalSql.Bindings, permutedSql.Bindings);

        var natural = await _executor.Query(Definition, GridDocument(permuted: false), NoParams);
        var permuted = await _executor.Query(Definition, GridDocument(permuted: true), NoParams);

        Assert.Equal(natural.TotalRows, permuted.TotalRows);
        Assert.Equal(
            natural.Columns.Select(column => column.Name),
            permuted.Columns.Select(column => column.Name));
        Assert.Equal(RowValues(natural), RowValues(permuted));
        Assert.Equal(
            ["Stark Ind:24000", "Acme Corp:18000", "Globex:15000", "Tyrell Corp:12000", "Initech:10000"],
            RowValues(natural));
    }

    [Fact]
    public async Task Pivot_runs_before_same_table_compute_and_filter_for_every_permutation()
    {
        var natural = await _executor.Query(Definition, PivotDocument(permuted: false), NoParams);
        var permuted = await _executor.Query(Definition, PivotDocument(permuted: true), NoParams);

        Assert.Equal(natural.TotalRows, permuted.TotalRows);
        Assert.Equal(
            natural.Columns.Select(column => column.Name),
            permuted.Columns.Select(column => column.Name));
        Assert.Equal(RowValues(natural), RowValues(permuted));
        Assert.Equal(["Acme Corp:12000:12000"], RowValues(natural));
    }

    [Fact]
    public async Task Unknown_composable_kind_fails_through_the_canonical_executor_path()
    {
        var document = Document(
        [
            new TableComposable { Kind = "teleport" },
        ]);

        var exception = await Assert.ThrowsAsync<ReportValidationException>(
            () => _executor.Query(Definition, document, NoParams));

        Assert.Contains(exception.Errors, error =>
            error.Path == "tables.result.composables[0].kind"
            && error.Message.Contains("teleport", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reordered_rule_validation_retains_its_original_document_path()
    {
        var document = Document(
        [
            new TableComposable
            {
                Kind = "compute",
                Computed =
                [
                    new ComputedColumn { Id = "ir1", Expr = "AMOUNT * 2" },
                    new ComputedColumn { Id = "ir2", Expr = "MISSING + 1" },
                ],
            },
        ]);

        var error = await Assert.ThrowsAsync<ReportValidationException>(
            () => _executor.Query(Definition, document, NoParams));

        Assert.Contains(error.Errors, item =>
            item.Path == "tables.result.composables[0].computed[1].expr"
            && item.Message.Contains("MISSING", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reordered_filter_errors_are_each_remapped_to_their_original_rule()
    {
        var document = Document(
        [
            new TableComposable
            {
                Kind = "filter",
                // Canonical expression ordering reverses these source indexes.
                Filters =
                [
                    new FilterRule { Expr = "Z_MISSING > 0" },
                    new FilterRule { Expr = "A_MISSING > 0" },
                ],
            },
        ]);

        var exception = await Assert.ThrowsAsync<ReportValidationException>(
            () => _executor.Query(Definition, document, NoParams));

        var aError = Assert.Single(exception.Errors.Where(item =>
            item.Message.Contains("A_MISSING", StringComparison.Ordinal)));
        var zError = Assert.Single(exception.Errors.Where(item =>
            item.Message.Contains("Z_MISSING", StringComparison.Ordinal)));
        Assert.Equal("tables.result.composables[0].filters[1].expr", aError.Path);
        Assert.Equal("tables.result.composables[0].filters[0].expr", zError.Path);
    }

    [Fact]
    public async Task Reordered_highlight_errors_are_each_remapped_to_their_original_rule()
    {
        var document = Document(
        [
            new TableComposable
            {
                Kind = "highlight",
                Highlights =
                [
                    new HighlightRule
                    {
                        Id = "later",
                        Sequence = 20,
                        Expr = "Z_MISSING > 0",
                        Style = new HighlightStyle { Bg = "red" },
                    },
                    new HighlightRule
                    {
                        Id = "earlier",
                        Sequence = 10,
                        Expr = "A_MISSING > 0",
                        Style = new HighlightStyle { Bg = "blue" },
                    },
                ],
            },
        ]);

        var exception = await Assert.ThrowsAsync<ReportValidationException>(
            () => _executor.Query(Definition, document, NoParams));

        var aError = Assert.Single(exception.Errors.Where(item =>
            item.Message.Contains("A_MISSING", StringComparison.Ordinal)));
        var zError = Assert.Single(exception.Errors.Where(item =>
            item.Message.Contains("Z_MISSING", StringComparison.Ordinal)));
        Assert.Equal("tables.result.composables[0].highlights[1].expr", aError.Path);
        Assert.Equal("tables.result.composables[0].highlights[0].expr", zError.Path);
    }

    [Fact]
    public async Task Canonically_collapsed_filters_report_one_budget_error()
    {
        var document = Document(
        [
            new TableComposable
            {
                Kind = "filter",
                Filters = Enumerable.Range(0, 26)
                    .Select(_ => new FilterRule { Expr = "AMOUNT > 0" })
                    .ToList(),
            },
            new TableComposable
            {
                Kind = "filter",
                Filters = Enumerable.Range(0, 25)
                    .Select(_ => new FilterRule { Expr = "AMOUNT >= 0" })
                    .ToList(),
            },
        ]);

        var exception = await Assert.ThrowsAsync<ReportValidationException>(
            () => _executor.Query(Definition, document, NoParams));

        var budget = Assert.Single(exception.Errors.Where(item =>
            item.Message == "at most 50 filter rules per report state"));
        Assert.Equal("tables.result.composables[0].filters", budget.Path);
    }

    [Fact]
    public async Task Metric_and_computed_outputs_share_the_ir_identity_namespace()
    {
        var document = Document(
        [
            new TableComposable
            {
                Kind = "compute",
                Computed = [new ComputedColumn { Id = "ir1", Expr = "__count + 1" }],
            },
            new TableComposable
            {
                Kind = "group",
                By = ["STATUS"],
                Values = [new MetricRule { Id = "ir1", Col = "AMOUNT", Fn = AggregateFn.Sum }],
            },
        ]);

        var error = await Assert.ThrowsAsync<ReportValidationException>(
            () => _executor.Query(Definition, document, NoParams));

        Assert.Contains(error.Errors, item =>
            item.Path == "tables.result.composables[0].computed[0].id"
            && item.Message.Contains("document-wide namespace", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Request_search_filters_the_completed_active_relation_only()
    {
        var document = Document(
        [
            new TableComposable
            {
                Kind = "group",
                By = ["STATUS"],
                Values = [new MetricRule { Id = "ir1", Col = "AMOUNT", Fn = AggregateFn.Sum }],
            },
        ]);
        document.Search = "Acme";

        var result = await _executor.Query(Definition, document, NoParams);

        Assert.Equal(0, result.TotalRows);
        Assert.Empty(result.Rows);
    }

    [Fact]
    public async Task Request_search_changes_the_local_execution_relation_not_the_table_export()
    {
        var document = Document([]);
        document.Search = "Acme";
        var definition = Definition;
        var schema = await _executor.GetSchema(definition, NoParams);
        var compiler = new ComposableTableCompiler(
            definition,
            document,
            schema,
            EvaluationUtcNow,
            (_, _, _, _, _) => Task.FromException<List<PivotGroup>>(
                new InvalidOperationException("Pivot discovery is not expected in this test.")));

        var compiled = await compiler.Compile("result", default);
        var completed = compiler.CompleteForTarget(compiled);

        Assert.Same(compiled.Export.Bound, completed.Export.Bound);
        Assert.IsType<BoundSearchRelation>(completed.Local.ExecutionNode);
        Assert.NotNull(completed.Relation);
        Assert.True(completed.SearchApplied);
    }

    private async Task<(string Sql, object?[] Bindings)> CompilePageSql(ReportState document)
    {
        var definition = Definition;
        var schema = await _executor.GetSchema(definition, NoParams);
        var compiler = new ComposableTableCompiler(
            definition,
            document,
            schema,
            EvaluationUtcNow,
            (_, _, _, _, _) => Task.FromException<List<PivotGroup>>(
                new InvalidOperationException("Pivot discovery is not expected in this test.")));
        var tableId = document.ActiveTable!;
        var plan = compiler.CompleteForTarget(await compiler.Compile(tableId, default));
        var compiled = DialectSupport.GetCompiler(ReportDialect.Sqlite)
            .Compile(plan.ExecutionBundle.MainRows.Query);
        return (compiled.Sql, compiled.Bindings.ToArray());
    }

    private static ReportState GridDocument(bool permuted)
    {
        var compute = new TableComposable
        {
            Kind = "compute",
            Computed = [new ComputedColumn { Id = "ir1", Expr = "AMOUNT * 2" }],
        };
        var filter = new TableComposable
        {
            Kind = "filter",
            Filters = [new FilterRule { Expr = "ir1 >= 10000" }],
        };
        var sort = new TableComposable
        {
            Kind = "sort",
            Sorts = [new SortRule { Col = "ir1", Dir = SortDir.Desc }],
        };
        var select = new TableComposable { Kind = "select", Columns = ["CUSTOMER", "ir1"] };

        return Document(permuted
            ? [select, filter, sort, compute]
            : [compute, filter, sort, select]);
    }

    private static ReportState PivotDocument(bool permuted)
    {
        var identities = new DynamicPivotColumnIdentityRegistry(["ir1", "ir2"]);
        var shipped = identities.Register("result", "ir1", ["SHIPPED"]);
        var pending = identities.Register("result", "ir1", ["PENDING"]);
        var pivot = new TableComposable
        {
            Kind = "pivot",
            Rows = ["CUSTOMER"],
            Cols = ["STATUS"],
            Values = [new MetricRule { Id = "ir1", Col = "AMOUNT", Fn = AggregateFn.Sum }],
        };
        var compute = new TableComposable
        {
            Kind = "compute",
            Computed =
            [
                new ComputedColumn
                {
                    Id = "ir2",
                    Expr = $"COALESCE(`{shipped}`, 0) - COALESCE(`{pending}`, 0)",
                },
            ],
        };
        var filter = new TableComposable
        {
            Kind = "filter",
            Filters = [new FilterRule { Expr = "ir2 >= 10000" }],
        };
        var sort = new TableComposable
        {
            Kind = "sort",
            Sorts = [new SortRule { Col = "ir2", Dir = SortDir.Desc }],
        };
        var select = new TableComposable
        {
            Kind = "select",
            Columns = ["CUSTOMER", shipped, "ir2"],
        };

        return Document(permuted
            ? [filter, select, compute, sort, pivot]
            : [pivot, compute, filter, sort, select]);
    }

    private static ReportState Document(List<TableComposable> composables)
        => new()
        {
            ActiveTable = "result",
            Page = new PageRequest { Index = 1, Size = 0 },
            Tables = new Dictionary<string, ReportTable>
            {
                ["result"] = new() { From = "definition", Composables = composables },
            },
        };

    private static string[] RowValues(ReportResult result)
    {
        var columns = result.Columns.Select(column => column.Name).ToArray();
        return result.Rows.Select(row => string.Join(
            ":",
            columns.Select(column => Convert.ToString(row[column], CultureInfo.InvariantCulture))))
            .ToArray();
    }
}
