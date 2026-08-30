using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Schema;
using InteractiveReport.Core.Validation;

namespace InteractiveReport.Core.Tests;

public sealed class StateStructureValidatorTests : IClassFixture<SqliteE2EFixture>
{
    private readonly ReportExecutor _executor;

    public StateStructureValidatorTests(SqliteE2EFixture database)
        => _executor = new ReportExecutor(database, new SchemaCache());

    [Fact]
    public void Collect_reports_null_tables_composables_schema_and_list_members()
    {
        var state = new ReportState
        {
            Tables = new Dictionary<string, ReportTable>
            {
                ["source"] = new()
                {
                    From = "definition",
                    Schema = [null!],
                    Composables =
                    [
                        new TableComposable
                        {
                            Kind = "sort",
                            Sorts = [null!],
                            Columns = [null!],
                        },
                        null!,
                    ],
                },
                ["broken"] = null!,
            },
        };

        var errors = StateStructureValidator.Collect(state);

        Assert.Contains(errors, error =>
            error.Path == "tables.broken" && error.Message.Contains("null", StringComparison.Ordinal));
        Assert.Contains(errors, error =>
            error.Path == "tables.source.schema[0]" && error.Message.Contains("null", StringComparison.Ordinal));
        Assert.Contains(errors, error =>
            error.Path == "tables.source.composables[1]" && error.Message.Contains("null", StringComparison.Ordinal));
        Assert.Contains(errors, error =>
            error.Path == "tables.source.composables[0].sorts[0]" && error.Message.Contains("null", StringComparison.Ordinal));
        Assert.Contains(errors, error =>
            error.Path == "tables.source.composables[0].columns[0]" && error.Message.Contains("null", StringComparison.Ordinal));
    }

    [Fact]
    public void Collect_reports_null_required_identifier_and_expression_properties()
    {
        var state = new ReportState
        {
            Tables = new Dictionary<string, ReportTable>
            {
                ["source"] = new()
                {
                    From = null,
                    Schema = [new ColumnInfo(null!, null!, null!, false)],
                    Composables =
                    [
                        new TableComposable
                        {
                            Kind = null!,
                            Values = [new MetricRule { Id = null!, Col = null! }],
                            Computed = [new ComputedColumn { Id = null!, Expr = null! }],
                            Filters = [new FilterRule { Expr = null! }],
                            Sorts = [new SortRule { Col = null! }],
                            Highlights =
                            [
                                new HighlightRule
                                {
                                    Id = null!,
                                    Scope = null!,
                                    Expr = null!,
                                },
                            ],
                            Aggregates = [new AggregateRule { Col = null! }],
                        },
                    ],
                },
            },
        };

        var paths = StateStructureValidator.Collect(state)
            .Select(error => error.Path)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("tables.source.from", paths);
        Assert.Contains("tables.source.schema[0].name", paths);
        Assert.Contains("tables.source.schema[0].label", paths);
        Assert.Contains("tables.source.schema[0].type", paths);
        Assert.Contains("tables.source.composables[0].kind", paths);
        Assert.Contains("tables.source.composables[0].values[0].id", paths);
        Assert.Contains("tables.source.composables[0].values[0].col", paths);
        Assert.Contains("tables.source.composables[0].computed[0].id", paths);
        Assert.Contains("tables.source.composables[0].computed[0].expr", paths);
        Assert.Contains("tables.source.composables[0].filters[0].expr", paths);
        Assert.Contains("tables.source.composables[0].sorts[0].col", paths);
        Assert.Contains("tables.source.composables[0].highlights[0].id", paths);
        Assert.Contains("tables.source.composables[0].highlights[0].scope", paths);
        Assert.Contains("tables.source.composables[0].highlights[0].expr", paths);
        Assert.Contains("tables.source.composables[0].aggregates[0].col", paths);
    }

    [Fact]
    public void Collect_allows_512_composables_and_rejects_513()
    {
        var composables = Enumerable.Range(0, StateStructureValidator.MaxComposables)
            .Select(_ => new TableComposable { Kind = "select", Columns = [] })
            .ToList();
        var state = new ReportState
        {
            Tables = new Dictionary<string, ReportTable>
            {
                ["source"] = new() { From = "definition", Composables = composables },
            },
        };

        Assert.DoesNotContain(
            StateStructureValidator.Collect(state),
            error => error.Message.Contains("at most 512 composables", StringComparison.Ordinal));

        composables.Add(new TableComposable { Kind = "select", Columns = [] });
        Assert.Contains(
            StateStructureValidator.Collect(state),
            error => error.Path == "tables"
                     && error.Message.Contains("at most 512 composables", StringComparison.Ordinal));
    }

    [Fact]
    public void Collect_rejects_blank_reserved_and_case_colliding_table_identifiers()
    {
        var state = new ReportState
        {
            Tables = new Dictionary<string, ReportTable>
            {
                ["orders"] = new() { From = "definition" },
                ["ORDERS"] = new() { From = "definition" },
                ["definition"] = new() { From = "definition" },
                [" "] = new() { From = "definition" },
            },
        };

        var errors = StateStructureValidator.Collect(state);

        Assert.Contains(errors, error =>
            error.Path == "tables.ORDERS" && error.Message.Contains("only by case", StringComparison.Ordinal));
        Assert.Contains(errors, error =>
            error.Path == "tables.definition" && error.Message.Contains("reserved", StringComparison.Ordinal));
        Assert.Contains(errors, error =>
            error.Path == "tables" && error.Message.Contains("cannot be blank", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Structurally_broken_default_state_is_a_configuration_error()
    {
        var definition = TestFixtures.OrdersDefinition(ReportDialect.Sqlite);
        definition.DefaultState = new ReportState
        {
            ActiveTable = "broken",
            Tables = new Dictionary<string, ReportTable> { ["broken"] = null! },
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _executor.Query(
                definition,
                new ReportState(),
                new Dictionary<string, object?>()));

        Assert.Contains("default state document is structurally invalid", exception.Message, StringComparison.Ordinal);
        Assert.Contains("tables.broken", exception.Message, StringComparison.Ordinal);
    }
}
