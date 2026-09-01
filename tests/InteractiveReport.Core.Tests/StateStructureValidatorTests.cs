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
    public void Collect_bounds_the_search_overlay_length()
    {
        // The search text is bound once per text column, so its length multiplies; the cap is
        // the same one the list-of-values search already applies.
        var tooLong = StateStructureValidator.Collect(new ReportState
        {
            Search = new string('a', StateStructureValidator.MaxSearchLength + 1),
        });
        var atLimit = StateStructureValidator.Collect(new ReportState
        {
            Search = new string('a', StateStructureValidator.MaxSearchLength),
        });

        var error = Assert.Single(tooLong);
        Assert.Equal("search", error.Path);
        Assert.Contains("200", error.Message);
        Assert.Empty(atLimit);
    }

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
    public void Collect_rejects_a_very_oversized_rule_collection_without_visiting_its_elements()
    {
        var state = StateWith(
            new TableComposable
            {
                Kind = "compute",
                Computed = Enumerable
                    .Repeat<ComputedColumn>(null!, 100_000)
                    .ToList(),
            });

        var error = Assert.Single(StateStructureValidator.Collect(state));

        Assert.Equal("tables.source.composables[0].computed", error.Path);
        Assert.Equal("at most 20 computed columns per report state", error.Message);
    }

    [Fact]
    public void Collect_allows_tight_rule_boundaries_and_rejects_the_next_entry()
    {
        var computed = Enumerable.Range(0, StateStructureValidator.MaxComputedRules)
            .Select(index => new ComputedColumn { Id = $"ir{index}", Expr = "AMOUNT" })
            .ToList();
        var filters = Enumerable.Range(0, StateStructureValidator.MaxFilterRules)
            .Select(_ => new FilterRule { Expr = "AMOUNT > 0" })
            .ToList();
        var highlights = Enumerable.Range(0, StateStructureValidator.MaxHighlightRules)
            .Select(index => new HighlightRule
            {
                Id = $"highlight-{index}",
                Scope = "row",
                Expr = "AMOUNT > 0",
            })
            .ToList();
        var metrics = Enumerable.Range(0, StateStructureValidator.MaxShapeMetrics)
            .Select(index => new MetricRule { Id = $"metric-{index}", Col = "AMOUNT" })
            .ToList();
        var state = StateWith(
            new TableComposable { Kind = "compute", Computed = computed },
            new TableComposable { Kind = "filter", Filters = filters },
            new TableComposable { Kind = "highlight", Highlights = highlights },
            new TableComposable { Kind = "group", Values = metrics });

        Assert.Empty(StateStructureValidator.Collect(state));

        computed.Add(new ComputedColumn { Id = "computed-over-limit", Expr = "AMOUNT" });
        filters.Add(new FilterRule { Expr = "AMOUNT > 1" });
        highlights.Add(new HighlightRule
        {
            Id = "highlight-over-limit",
            Scope = "row",
            Expr = "AMOUNT > 1",
        });
        metrics.Add(new MetricRule { Id = "metric-over-limit", Col = "AMOUNT" });

        var errors = StateStructureValidator.Collect(state);

        Assert.Equal(4, errors.Count);
        Assert.Contains(errors, error =>
            error.Path == "tables.source.composables[0].computed"
            && error.Message == "at most 20 computed columns per report state");
        Assert.Contains(errors, error =>
            error.Path == "tables.source.composables[1].filters"
            && error.Message == "at most 50 filter rules per report state");
        Assert.Contains(errors, error =>
            error.Path == "tables.source.composables[2].highlights"
            && error.Message == "at most 50 highlight rules per report state");
        Assert.Contains(errors, error =>
            error.Path == "tables.source.composables[3].values"
            && error.Message == "a shape may contain at most 256 metrics");
    }

    [Fact]
    public void Collect_bounds_generic_lists_maps_and_nested_format_classes()
    {
        var columns = Enumerable.Range(0, StateStructureValidator.MaxNestedCollectionEntries)
            .Select(index => $"column-{index}")
            .ToList();
        var labels = Enumerable.Range(0, StateStructureValidator.MaxNestedCollectionEntries)
            .ToDictionary(index => $"label-{index}", index => $"Label {index}");
        var formats = Enumerable.Range(0, StateStructureValidator.MaxNestedCollectionEntries)
            .ToDictionary(index => $"format-{index}", _ => new ColumnFormat());
        var classes = Enumerable.Range(0, StateStructureValidator.MaxNestedCollectionEntries)
            .Select(index => $"class-{index}")
            .ToList();
        var state = StateWith(
            new TableComposable { Kind = "select", Columns = columns },
            new TableComposable { Kind = "labels", Labels = labels },
            new TableComposable { Kind = "formats", Formats = formats },
            new TableComposable
            {
                Kind = "formats",
                Formats = new Dictionary<string, ColumnFormat>
                {
                    ["AMOUNT"] = new() { Classes = classes },
                },
            });

        Assert.Empty(StateStructureValidator.Collect(state));

        columns.Add("column-over-limit");
        labels.Add("label-over-limit", "Over limit");
        formats.Add("format-over-limit", new ColumnFormat());
        classes.Add("class-over-limit");

        var errors = StateStructureValidator.Collect(state);

        Assert.Equal(4, errors.Count);
        Assert.All(errors, error => Assert.Equal(
            "a collection may contain at most 900 entries",
            error.Message));
        Assert.Equal(
            [
                "tables.source.composables[0].columns",
                "tables.source.composables[1].labels",
                "tables.source.composables[2].formats",
                "tables.source.composables[3].formats.AMOUNT.classes",
            ],
            errors.Select(error => error.Path));
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

    private static ReportState StateWith(params TableComposable[] composables) => new()
    {
        Tables = new Dictionary<string, ReportTable>
        {
            ["source"] = new()
            {
                From = "definition",
                Composables = composables.ToList(),
            },
        },
    };
}
