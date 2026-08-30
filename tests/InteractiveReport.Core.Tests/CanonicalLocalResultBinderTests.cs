using InteractiveReport.Core.Model;
using InteractiveReport.Core.Planning;
using InteractiveReport.Core.Schema;
using InteractiveReport.Core.Validation;

namespace InteractiveReport.Core.Tests;

public class CanonicalLocalResultBinderTests
{
    private static readonly ReportSchema OrdersSchema = ReportSchema.Create(
        "orders",
        TestFixtures.OrdersSchema);

    [Fact]
    public void Bind_validates_and_projects_terminal_composables_directly()
    {
        var table = new ReportTable
        {
            Composables =
            [
                new TableComposable
                {
                    Kind = "aggregate",
                    Aggregates =
                    [
                        new AggregateRule { Col = "STATUS", Fn = AggregateFn.Count },
                        new AggregateRule { Col = "AMOUNT", Fn = AggregateFn.Sum },
                        new AggregateRule { Col = "amount", Fn = AggregateFn.Sum },
                        new AggregateRule { Col = "MISSING", Fn = AggregateFn.Sum },
                    ],
                },
                new TableComposable
                {
                    Kind = "break",
                    Breaks = ["CUSTOMER", "customer", "STATUS", "MISSING_BREAK"],
                },
                new TableComposable
                {
                    Kind = "highlight",
                    Highlights =
                    [
                        new HighlightRule
                        {
                            Id = "h-row",
                            Name = "  Large amount  ",
                            Scope = "ROW",
                            Expr = "AMOUNT > 100",
                            Style = new HighlightStyle { Bg = "red" },
                        },
                        new HighlightRule
                        {
                            Id = "h-cell",
                            Sequence = 40,
                            Scope = "cell",
                            Col = "STATUS",
                            Expr = "STATUS = 'open'",
                            Style = new HighlightStyle { Fg = "white" },
                        },
                    ],
                },
                new TableComposable
                {
                    Kind = "sort",
                    Sorts =
                    [
                        new SortRule
                        {
                            Col = "AMOUNT",
                            Dir = SortDir.Desc,
                            Nulls = NullPlacement.Last,
                        },
                        new SortRule { Col = "amount", Dir = SortDir.Asc },
                        new SortRule { Col = "STATUS", Dir = SortDir.Desc },
                        new SortRule { Col = "MISSING_SORT", Dir = SortDir.Asc },
                    ],
                },
                new TableComposable
                {
                    Kind = "select",
                    Columns = ["region", "REGION", "MISSING_COLUMN", "AMOUNT"],
                },
            ],
        };
        var specification = CanonicalTableNormalizer.Normalize(table, "tables.summary");
        var policy = ColumnPolicy.From(new ReportDefinition
        {
            Columns = new Dictionary<string, ReportColumnOverride>(StringComparer.OrdinalIgnoreCase)
            {
                ["STATUS"] = new() { Sortable = false },
            },
        });

        var direct = BindDirect(specification.Local, policy);

        Assert.Empty(direct.Errors);
        Assert.Equal(
            [
                new IgnoredItem("column", "unknown column 'MISSING_COLUMN'"),
                new IgnoredItem("sort", "column 'STATUS' is not sortable"),
                new IgnoredItem("sort", "unknown column 'MISSING_SORT'"),
                new IgnoredItem("break", "column 'STATUS' is not sortable (control breaks imply sorting)"),
                new IgnoredItem("break", "unknown column 'MISSING_BREAK'"),
                new IgnoredItem("aggregate", "unknown column 'MISSING'"),
            ],
            direct.Ignored);

        Assert.Equal(["REGION", "AMOUNT"], Names(direct.Layer.SelectColumns));
        Assert.Equal(["REGION", "AMOUNT", "CUSTOMER"], Names(direct.Layer.ProjectionColumns));
        Assert.Equal(["CUSTOMER"], Names(direct.Layer.Breaks));
        var sort = Assert.Single(direct.Layer.Sorts);
        Assert.Equal("AMOUNT", sort.Column.Name);
        Assert.Equal(SortDir.Desc, sort.Dir);
        Assert.Equal(NullPlacement.Last, sort.Nulls);
        Assert.Equal(
            [
                ("AMOUNT", AggregateFn.Sum),
                ("STATUS", AggregateFn.Count),
            ],
            direct.Layer.Aggregates.Select(aggregate => (aggregate.Column.Name, aggregate.Fn)));
        Assert.Equal(
            [
                ("h-row", "Large amount", 10, HighlightScope.Row, (string?)null, "__ir_highlight_0"),
                ("h-cell", "h-cell", 40, HighlightScope.Cell, "STATUS", "__ir_highlight_1"),
            ],
            direct.Layer.Decorations.Select(rule =>
                (
                    rule.Effect.Id,
                    rule.Effect.Name,
                    rule.Effect.Sequence,
                    rule.Effect.Scope,
                    rule.Effect.Column?.Name,
                    rule.Effect.ProjectionName)));
    }

    [Fact]
    public void Bind_preserves_each_canonical_rule_source_path_for_errors()
    {
        var local = new CanonicalLocalResult(
            Selection: null,
            Ordering: null,
            Highlights:
            [
                Highlight(
                    id: "",
                    sequence: 10,
                    expression: "AMOUNT > 1",
                    style: new CanonicalHighlightStyle("red", null),
                    path: "tables.summary.composables[8].highlights[3]"),
                Highlight(
                    id: "bad-sequence",
                    sequence: 0,
                    expression: "AMOUNT > 1",
                    style: new CanonicalHighlightStyle("red", null),
                    path: "tables.summary.composables[2].highlights[4]"),
                Highlight(
                    id: "bad-scope",
                    sequence: 20,
                    expression: "AMOUNT > 1",
                    style: new CanonicalHighlightStyle("red", null),
                    path: "tables.summary.composables[9].highlights[0]",
                    scope: "column"),
                Highlight(
                    id: "bad-style",
                    sequence: 30,
                    expression: "AMOUNT > 1",
                    style: null,
                    path: "tables.summary.composables[4].highlights[7]"),
                Highlight(
                    id: "bad-expression",
                    sequence: 40,
                    expression: "AMOUNT +",
                    style: new CanonicalHighlightStyle(null, "white"),
                    path: "tables.summary.composables[6].highlights[1]"),
                Highlight(
                    id: "missing-cell",
                    sequence: 50,
                    expression: "AMOUNT > 1",
                    style: new CanonicalHighlightStyle("red", null),
                    path: "tables.summary.composables[3].highlights[2]",
                    scope: "cell",
                    column: "MISSING_CELL"),
            ],
            HighlightPopulation: new CanonicalRulePopulation(
                6,
                ["tables.summary.composables[2].highlights"]),
            Breaks: null,
            Aggregates:
            [
                new CanonicalAggregate(
                    "STATUS",
                    AggregateFn.Sum,
                    "tables.summary.composables[11].aggregates[5]"),
            ]);

        var direct = BindDirect(local, ColumnPolicy.Unrestricted);

        Assert.Equal(
            [
                "tables.summary.composables[8].highlights[3]",
                "tables.summary.composables[2].highlights[4].sequence",
                "tables.summary.composables[9].highlights[0]",
                "tables.summary.composables[4].highlights[7].style",
                "tables.summary.composables[6].highlights[1].expr",
                "tables.summary.composables[11].aggregates[5]",
            ],
            direct.Errors.Select(error => error.Path));
        Assert.Contains(
            new IgnoredItem("highlight", "'missing-cell': unknown cell column 'MISSING_CELL'"),
            direct.Ignored);
    }

    [Fact]
    public void Bind_preserves_collection_path_and_budget_semantics()
    {
        var local = new CanonicalLocalResult(
            Selection: null,
            Ordering: null,
            Highlights:
            [
                Highlight(
                    id: "later-owner",
                    sequence: 10,
                    expression: "AMOUNT > 1",
                    style: new CanonicalHighlightStyle("red", null),
                    path: "tables.summary.composables[9].highlights[4]"),
                Highlight(
                    id: "first-owner",
                    sequence: 20,
                    expression: "AMOUNT > 2",
                    style: new CanonicalHighlightStyle("blue", null),
                    path: "tables.summary.composables[2].highlights[7]"),
            ],
            HighlightPopulation: new CanonicalRulePopulation(
                2,
                [
                    "tables.summary.composables[9].highlights",
                    "tables.summary.composables[2].highlights",
                ]),
            Breaks: null,
            Aggregates: []);
        var directContext = new LocalResultBindingContext();
        directContext.Highlights.RuleCount = 49;

        var direct = BindDirect(local, ColumnPolicy.Unrestricted, directContext);

        var error = Assert.Single(direct.Errors);
        Assert.Equal("tables.summary.composables[2].highlights", error.Path);
        Assert.Equal("at most 50 highlight rules per report state", error.Message);
        Assert.Empty(direct.Layer.Decorations);
    }

    [Fact]
    public void Bind_counts_disabled_highlights_from_normalized_authored_population()
    {
        var composables = Enumerable.Range(0, 10)
            .Select(_ => new TableComposable { Kind = "labels" })
            .ToList();
        composables[9] = new TableComposable
        {
            Kind = "highlight",
            Highlights =
            [
                new HighlightRule
                {
                    Enabled = false,
                    Id = "",
                    Scope = "not-a-scope",
                    Expr = "not valid expression syntax +",
                },
            ],
        };
        composables[2] = new TableComposable
        {
            Kind = "highlight",
            Highlights =
            [
                new HighlightRule
                {
                    Enabled = false,
                    Id = "also-invalid",
                    Scope = "also-not-a-scope",
                    Expr = "also invalid +",
                },
            ],
        };
        var specification = CanonicalTableNormalizer.Normalize(
            new ReportTable { Composables = composables },
            "tables.child");

        Assert.Equal(2, specification.Local.Highlights.Length);
        Assert.All(specification.Local.Highlights, rule => Assert.False(rule.Enabled));
        Assert.Equal(2, specification.Local.HighlightPopulation.AuthoredCount);
        Assert.Equal(
            [
                "tables.child.composables[2].highlights",
                "tables.child.composables[9].highlights",
            ],
            specification.Local.HighlightPopulation.CollectionPaths.ToArray());

        var context = new LocalResultBindingContext();
        context.Highlights.RuleCount = 49;
        var direct = BindDirect(
            specification.Local,
            ColumnPolicy.Unrestricted,
            context);

        var error = Assert.Single(direct.Errors);
        Assert.Equal("tables.child.composables[2].highlights", error.Path);
        Assert.Equal("at most 50 highlight rules per report state", error.Message);
        Assert.Empty(direct.Layer.Decorations);
        Assert.Equal(51, context.Highlights.RuleCount);
    }

    [Fact]
    public void Bind_uses_shared_identity_context_and_global_highlight_projection_ordinals()
    {
        var context = new LocalResultBindingContext();
        var first = new CanonicalLocalResult(
            null,
            null,
            [Highlight(
                id: "shared",
                sequence: 10,
                expression: "AMOUNT > 1",
                style: new CanonicalHighlightStyle("red", null),
                path: "tables.first.composables[0].highlights[0]")],
            new CanonicalRulePopulation(1, ["tables.first.composables[0].highlights"]),
            null,
            [new CanonicalAggregate(
                "AMOUNT",
                AggregateFn.Sum,
                "tables.first.composables[1].aggregates[0]")]);
        var second = new CanonicalLocalResult(
            null,
            null,
            [Highlight(
                id: "shared",
                sequence: 20,
                expression: "AMOUNT > 2",
                style: new CanonicalHighlightStyle("blue", null),
                path: "tables.second.composables[0].highlights[0]")],
            new CanonicalRulePopulation(1, ["tables.second.composables[0].highlights"]),
            null,
            [new CanonicalAggregate(
                "amount",
                AggregateFn.Sum,
                "tables.second.composables[1].aggregates[0]")]);

        var firstResult = BindDirect(first, ColumnPolicy.Unrestricted, context);
        var secondResult = BindDirect(second, ColumnPolicy.Unrestricted, context);

        Assert.Empty(firstResult.Errors);
        Assert.Equal("__ir_highlight_0", Assert.Single(firstResult.Layer.Decorations).Effect.ProjectionName);
        Assert.Single(firstResult.Layer.Aggregates);
        Assert.Equal(
            "tables.second.composables[0].highlights[0]",
            Assert.Single(secondResult.Errors).Path);
        Assert.Empty(secondResult.Layer.Decorations);
        Assert.Empty(secondResult.Layer.Aggregates);
    }

    [Fact]
    public void Disabled_highlight_reserves_its_sequence_across_table_layers()
    {
        var context = new LocalResultBindingContext();
        var disabled = new CanonicalLocalResult(
            null,
            null,
            [Highlight(
                id: "disabled",
                sequence: 10,
                expression: "invalid +",
                style: null,
                path: "tables.parent.composables[0].highlights[0]",
                enabled: false)],
            new CanonicalRulePopulation(1, ["tables.parent.composables[0].highlights"]),
            null,
            []);
        var enabled = new CanonicalLocalResult(
            null,
            null,
            [Highlight(
                id: "enabled",
                sequence: 10,
                expression: "AMOUNT > 1",
                style: new CanonicalHighlightStyle("red", null),
                path: "tables.child.composables[0].highlights[0]")],
            new CanonicalRulePopulation(1, ["tables.child.composables[0].highlights"]),
            null,
            []);

        var parentResult = BindDirect(disabled, ColumnPolicy.Unrestricted, context);
        var childResult = BindDirect(enabled, ColumnPolicy.Unrestricted, context);

        Assert.Empty(parentResult.Errors);
        Assert.Empty(parentResult.Layer.Decorations);
        var error = Assert.Single(childResult.Errors);
        Assert.Equal("tables.child.composables[0].highlights[0].sequence", error.Path);
        Assert.Equal("duplicate highlight sequence '10'", error.Message);
        Assert.Empty(childResult.Layer.Decorations);
    }

    private static CanonicalHighlight Highlight(
        string id,
        int? sequence,
        string expression,
        CanonicalHighlightStyle? style,
        string path,
        string scope = "row",
        string? column = null,
        bool enabled = true)
        => new(id, null, sequence, scope, column, expression, style, path, enabled);

    private static BindResult BindDirect(
        CanonicalLocalResult local,
        ColumnPolicy policy,
        LocalResultBindingContext? context = null)
    {
        var errors = new List<ValidationError>();
        var ignored = new List<IgnoredItem>();
        var layer = CanonicalLocalResultBinder.Bind(
            local,
            OrdersSchema,
            policy,
            errors,
            ignored,
            context);
        return new BindResult(layer, errors, ignored);
    }

    private static string[] Names(IReadOnlyList<ColumnModel> columns)
        => columns.Select(column => column.Name).ToArray();

    private sealed record BindResult(
        BoundLocalResult Layer,
        List<ValidationError> Errors,
        List<IgnoredItem> Ignored);
}
