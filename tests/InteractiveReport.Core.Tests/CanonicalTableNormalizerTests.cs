using InteractiveReport.Core.Model;
using InteractiveReport.Core.Planning;
using InteractiveReport.Core.Schema;
using InteractiveReport.Core.Validation;

namespace InteractiveReport.Core.Tests;

public class CanonicalTableNormalizerTests
{
    [Fact]
    public void Semantics_catalog_is_exhaustive_and_separates_inherited_from_local_effects()
    {
        Assert.Equal(
            Enum.GetValues<ComposableKind>().Order(),
            ComposableSemanticsCatalog.All.Select(value => value.Kind).Order());

        var compute = ComposableSemanticsCatalog.Get(ComposableKind.Compute);
        Assert.Equal(ComposablePhase.DerivedColumns, compute.Phase);
        Assert.True(compute.IsInherited);
        Assert.False(compute.IsTableLocal);
        Assert.True(compute.Effect.HasFlag(ComposableEffect.ExportedSchema));

        var select = ComposableSemanticsCatalog.Get(ComposableKind.Select);
        Assert.Equal(ComposablePhase.TableLocal, select.Phase);
        Assert.False(select.IsInherited);
        Assert.True(select.IsTableLocal);

        var pivot = ComposableSemanticsCatalog.Get(ComposableKind.Pivot);
        Assert.True(pivot.IsInherited);
        Assert.True(pivot.IsTableLocal);

        var group = ComposableSemanticsCatalog.Get(ComposableKind.Group);
        Assert.True(group.IsInherited);
        Assert.True(group.IsTableLocal);

        var labels = ComposableSemanticsCatalog.Get(ComposableKind.Labels);
        Assert.True(labels.IsInherited);
        Assert.True(labels.IsTableLocal);

        var highlight = ComposableSemanticsCatalog.Get(ComposableKind.Highlight);
        Assert.Equal(ComposableMerge.PrioritySet, highlight.Merge);
        Assert.Equal(
            ComposableOrderingHint.ScopeThenSequenceAscending,
            highlight.OrderingHint);
    }

    [Fact]
    public void Natural_order_is_derived_from_semantics_and_computed_dependencies()
    {
        var table = new ReportTable
        {
            Composables =
            [
                new TableComposable
                {
                    Kind = "filter",
                    Filters = [new FilterRule { Expr = "ir2 > 10" }],
                },
                new TableComposable { Kind = "select", Columns = ["REGION", "ir2"] },
                new TableComposable
                {
                    Kind = "compute",
                    Computed =
                    [
                        new ComputedColumn { Id = "ir2", Expr = "ir1 * 2" },
                        new ComputedColumn { Id = "ir1", Expr = "AMOUNT + 1" },
                    ],
                },
                new TableComposable
                {
                    Kind = "aggregate",
                    Aggregates = [new AggregateRule { Col = "ir2", Fn = AggregateFn.Sum }],
                },
                new TableComposable
                {
                    Kind = "formats",
                    Formats = new Dictionary<string, ColumnFormat>
                    {
                        ["ir2"] = new() { Mask = "decimal-2" },
                    },
                },
                new TableComposable
                {
                    Kind = "highlight",
                    Highlights =
                    [
                        new HighlightRule
                        {
                            Id = "high",
                            Sequence = 10,
                            Expr = "ir2 > 100",
                            Style = new HighlightStyle { Bg = "red" },
                        },
                    ],
                },
                new TableComposable { Kind = "break", Breaks = ["REGION"] },
                new TableComposable
                {
                    Kind = "sort",
                    Sorts = [new SortRule { Col = "ir2", Dir = SortDir.Desc }],
                },
                new TableComposable
                {
                    Kind = "labels",
                    Labels = new Dictionary<string, string> { ["ir2"] = "Double amount" },
                },
                new TableComposable
                {
                    Kind = "pivot",
                    Rows = ["REGION"],
                    Cols = ["STATUS"],
                },
            ],
        };

        var result = CanonicalTableNormalizer.Normalize(table, "tables.summary");

        Assert.IsType<CanonicalPivotShape>(result.Shape);
        Assert.Equal(["ir1", "ir2"], result.Computed.Select(value => value.Id));
        Assert.Empty(result.Computed[0].Dependencies);
        Assert.Equal(["ir1"], result.Computed[1].Dependencies.ToArray());
        Assert.Single(result.Filters);
        Assert.Equal("Double amount", result.Metadata.Labels["IR2"]);
        Assert.Equal("decimal-2", result.Metadata.Formats["IR2"].Mask);
        Assert.NotNull(result.Local.Selection);
        Assert.NotNull(result.Local.Ordering);
        Assert.NotNull(result.Local.Breaks);

        Assert.Equal(
            [
                ComposableKind.Pivot,
                ComposableKind.Compute,
                ComposableKind.Compute,
                ComposableKind.Filter,
                ComposableKind.Labels,
                ComposableKind.Formats,
                ComposableKind.Select,
                ComposableKind.Sort,
                ComposableKind.Highlight,
                ComposableKind.Break,
                ComposableKind.Aggregate,
            ],
            result.NaturalOrder.Select(value => value.Kind));
        Assert.Equal(
            result.NaturalOrder.Select(value => value.Phase).Order(),
            result.NaturalOrder.Select(value => value.Phase));
    }

    [Fact]
    public void Independent_computed_columns_use_stable_ids_not_document_position()
    {
        var table = new ReportTable
        {
            Composables =
            [
                new TableComposable
                {
                    Kind = "compute",
                    Computed =
                    [
                        new ComputedColumn { Id = "ir20", Expr = "AMOUNT + 20" },
                        new ComputedColumn { Id = "ir10", Expr = "AMOUNT + 10" },
                    ],
                },
            ],
        };

        var result = CanonicalTableNormalizer.Normalize(table);

        Assert.Equal(["ir10", "ir20"], result.Computed.Select(value => value.Id));
    }

    [Fact]
    public void Multiple_shape_composables_are_ambiguous_even_when_identical()
    {
        var table = new ReportTable
        {
            Composables =
            [
                new TableComposable { Kind = "group", By = ["REGION"] },
                new TableComposable { Kind = "group", By = ["REGION"] },
            ],
        };

        var exception = Assert.Throws<ReportValidationException>(
            () => CanonicalTableNormalizer.Normalize(table, "tables.summary"));

        var error = Assert.Single(exception.Errors);
        Assert.Equal("tables.summary.composables[1]", error.Path);
        Assert.Contains("at most one shape", error.Message);
    }

    [Fact]
    public void Computed_dependency_cycles_are_rejected_without_using_document_order()
    {
        var table = new ReportTable
        {
            Composables =
            [
                new TableComposable
                {
                    Kind = "compute",
                    Computed =
                    [
                        new ComputedColumn { Id = "ir1", Expr = "ir2 + 1" },
                        new ComputedColumn { Id = "ir2", Expr = "ir1 + 1" },
                    ],
                },
            ],
        };

        var exception = Assert.Throws<ReportValidationException>(
            () => CanonicalTableNormalizer.Normalize(table));

        Assert.Contains(exception.Errors, error =>
            error.Message.Contains("dependency cycle", StringComparison.Ordinal));
    }

    [Fact]
    public void Conflicting_singleton_terminal_declarations_are_rejected()
    {
        var table = new ReportTable
        {
            Composables =
            [
                new TableComposable { Kind = "select", Columns = ["REGION"] },
                new TableComposable { Kind = "select", Columns = ["AMOUNT"] },
                new TableComposable
                {
                    Kind = "sort",
                    Sorts = [new SortRule { Col = "REGION" }],
                },
                new TableComposable
                {
                    Kind = "sort",
                    Sorts = [new SortRule { Col = "AMOUNT" }],
                },
            ],
        };

        var exception = Assert.Throws<ReportValidationException>(
            () => CanonicalTableNormalizer.Normalize(table));

        Assert.Contains(exception.Errors, error => error.Message.Contains("'select'", StringComparison.Ordinal));
        Assert.Contains(exception.Errors, error => error.Message.Contains("'sort'", StringComparison.Ordinal));
    }

    [Fact]
    public void Conflicting_break_singletons_are_rejected()
    {
        var table = new ReportTable
        {
            Composables =
            [
                new TableComposable { Kind = "break", Breaks = ["REGION"] },
                new TableComposable { Kind = "break", Breaks = ["AMOUNT"] },
            ],
        };

        var exception = Assert.Throws<ReportValidationException>(
            () => CanonicalTableNormalizer.Normalize(table));

        Assert.Contains(exception.Errors, error =>
            error.Path == "table.composables[1]"
            && error.Message.Contains("'break'", StringComparison.Ordinal)
            && error.Message.Contains("singleton", StringComparison.Ordinal));
    }

    [Fact]
    public void Metadata_overlays_merge_disjoint_values_and_reject_conflicts()
    {
        var valid = new ReportTable
        {
            Composables =
            [
                new TableComposable { Kind = "labels", Labels = [] },
                new TableComposable
                {
                    Kind = "labels",
                    Labels = new Dictionary<string, string> { ["REGION"] = "Area" },
                },
                new TableComposable
                {
                    Kind = "labels",
                    Labels = new Dictionary<string, string> { ["AMOUNT"] = "Value" },
                },
            ],
        };

        var normalized = CanonicalTableNormalizer.Normalize(valid);

        Assert.True(normalized.Metadata.ClearsInheritedLabels);
        Assert.Equal(2, normalized.Metadata.Labels.Count);

        valid.Composables!.Add(new TableComposable
        {
            Kind = "labels",
            Labels = new Dictionary<string, string> { ["region"] = "Territory" },
        });

        var exception = Assert.Throws<ReportValidationException>(
            () => CanonicalTableNormalizer.Normalize(valid));
        Assert.Contains(exception.Errors, error => error.Message.Contains("conflicting label", StringComparison.Ordinal));
    }

    [Fact]
    public void Conflicting_format_assignments_are_rejected_at_the_field_path()
    {
        var table = new ReportTable
        {
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
                new TableComposable
                {
                    Kind = "formats",
                    Formats = new Dictionary<string, ColumnFormat>
                    {
                        ["amount"] = new() { Mask = "integer" },
                    },
                },
            ],
        };

        var exception = Assert.Throws<ReportValidationException>(
            () => CanonicalTableNormalizer.Normalize(table));

        Assert.Contains(exception.Errors, error =>
            error.Path == "table.composables[1].formats.amount"
            && error.Message.Contains("conflicting format", StringComparison.Ordinal));
    }

    [Fact]
    public void Label_normalization_is_permutation_invariant_and_matches_validation_semantics()
    {
        static ReportTable Table(bool reverse)
        {
            var padded = new TableComposable
            {
                Kind = "labels",
                Labels = new Dictionary<string, string>
                {
                    ["AMOUNT"] = "  Revenue  ",
                    ["IGNORED"] = "   ",
                    ["   "] = "Ignored key",
                },
            };
            var normalized = new TableComposable
            {
                Kind = "labels",
                Labels = new Dictionary<string, string> { ["amount"] = "Revenue" },
            };
            return new ReportTable
            {
                Composables = reverse ? [normalized, padded] : [padded, normalized],
            };
        }

        var forward = CanonicalTableNormalizer.Normalize(Table(reverse: false));
        var reverse = CanonicalTableNormalizer.Normalize(Table(reverse: true));

        Assert.Single(forward.Metadata.Labels);
        Assert.Equal("Revenue", forward.Metadata.Labels["amount"]);
        Assert.Equal(
            forward.Metadata.Labels.OrderBy(pair => pair.Key),
            reverse.Metadata.Labels.OrderBy(pair => pair.Key));
    }

    [Fact]
    public void Missing_highlight_sequences_are_stable_and_cannot_collide_with_explicit_values()
    {
        static ReportTable Table(bool reverse)
        {
            var values = new List<HighlightRule>
            {
                new()
                {
                    Id = "h-b",
                    Expr = "AMOUNT > 2",
                    Style = new HighlightStyle { Bg = "blue" },
                },
                new()
                {
                    Id = "h-explicit",
                    Sequence = 20,
                    Expr = "AMOUNT > 3",
                    Style = new HighlightStyle { Bg = "red" },
                },
                new()
                {
                    Id = "h-a",
                    Expr = "AMOUNT > 1",
                    Style = new HighlightStyle { Bg = "green" },
                },
            };
            if (reverse) values.Reverse();
            return new ReportTable
            {
                Composables =
                [
                    new TableComposable { Kind = "highlight", Highlights = values },
                ],
            };
        }

        var forward = CanonicalTableNormalizer.Normalize(Table(reverse: false));
        var reverse = CanonicalTableNormalizer.Normalize(Table(reverse: true));

        Assert.Equal(
            [("h-a", 10), ("h-explicit", 20), ("h-b", 30)],
            forward.Local.Highlights.Select(value => (value.Id, value.Sequence)));
        Assert.Equal(
            forward.Local.Highlights.Select(value => (value.Id, value.Sequence)),
            reverse.Local.Highlights.Select(value => (value.Id, value.Sequence)));
        Assert.All(forward.Local.Highlights, value => Assert.NotNull(value.Sequence));

        var errors = new List<ValidationError>();
        var layer = CanonicalLocalResultBinder.Bind(
            forward.Local,
            ReportSchema.Create("highlight-sequence", TestFixtures.OrdersSchema),
            ColumnPolicy.Unrestricted,
            errors,
            []);
        Assert.Empty(errors);
        Assert.Equal([10, 20, 30], layer.Decorations.Select(rule => rule.Effect.Sequence));
    }

    [Fact]
    public void Disabled_highlights_reserve_sequence_precedence_without_executing()
    {
        var normalized = CanonicalTableNormalizer.Normalize(new ReportTable
        {
            Composables =
            [
                new TableComposable
                {
                    Kind = "highlight",
                    Highlights =
                    [
                        new HighlightRule
                        {
                            Id = "h-disabled",
                            Sequence = 10,
                            Enabled = false,
                            Expr = "invalid +",
                        },
                        new HighlightRule
                        {
                            Id = "h-late",
                            Expr = "AMOUNT > 20",
                            Style = new HighlightStyle { Bg = "red" },
                        },
                        new HighlightRule
                        {
                            Id = "h-middle",
                            Sequence = 15,
                            Expr = "AMOUNT > 10",
                            Style = new HighlightStyle { Bg = "blue" },
                        },
                    ],
                },
            ],
        });

        Assert.Equal(
            [("h-disabled", 10, false), ("h-middle", 15, true), ("h-late", 20, true)],
            normalized.Local.Highlights.Select(value =>
                (value.Id, value.Sequence, value.Enabled)));

        var errors = new List<ValidationError>();
        var layer = CanonicalLocalResultBinder.Bind(
            normalized.Local,
            ReportSchema.Create("highlight-order", TestFixtures.OrdersSchema),
            ColumnPolicy.Unrestricted,
            errors,
            []);

        Assert.Empty(errors);
        Assert.Equal(
            [("h-middle", 15, "__ir_highlight_1"), ("h-late", 20, "__ir_highlight_2")],
            layer.Decorations.Select(rule =>
                (rule.Effect.Id, rule.Effect.Sequence, rule.Effect.ProjectionName)));
    }

    [Fact]
    public void Highlight_natural_order_excludes_disabled_rules_and_matches_scope_precedence()
    {
        var normalized = CanonicalTableNormalizer.Normalize(new ReportTable
        {
            Composables =
            [
                new TableComposable
                {
                    Kind = "highlight",
                    Highlights =
                    [
                        new HighlightRule
                        {
                            Id = "cell-low",
                            Sequence = 10,
                            Scope = "cell",
                            Col = "AMOUNT",
                            Expr = "AMOUNT > 10",
                            Style = new HighlightStyle { Bg = "red" },
                        },
                        new HighlightRule
                        {
                            Id = "row-high",
                            Sequence = 30,
                            Expr = "AMOUNT > 30",
                            Style = new HighlightStyle { Bg = "blue" },
                        },
                        new HighlightRule
                        {
                            Id = "row-disabled",
                            Sequence = 20,
                            Enabled = false,
                        },
                    ],
                },
            ],
        });

        Assert.Equal(
            ["row-disabled", "row-high", "cell-low"],
            normalized.Local.Highlights.Select(value => value.Id));
        Assert.Equal(
            [
                "table.composables[0].highlights[1]",
                "table.composables[0].highlights[0]",
            ],
            normalized.NaturalOrder.Select(value => value.SourcePath));
    }

    [Fact]
    public void Canonical_spec_does_not_retain_mutable_document_collections()
    {
        var columns = new List<string> { "REGION" };
        var classes = new List<string> { "money" };
        var format = new ColumnFormat { Mask = "decimal-2", Classes = classes };
        var table = new ReportTable
        {
            Composables =
            [
                new TableComposable { Kind = "select", Columns = columns },
                new TableComposable
                {
                    Kind = "formats",
                    Formats = new Dictionary<string, ColumnFormat> { ["AMOUNT"] = format },
                },
            ],
        };
        var normalized = CanonicalTableNormalizer.Normalize(table);

        columns[0] = "AMOUNT";
        classes[0] = "changed";
        format.Mask = "integer";

        Assert.Equal(["REGION"], normalized.Local.Selection!.Columns.ToArray());
        Assert.Equal("decimal-2", normalized.Metadata.Formats["AMOUNT"].Mask);
        Assert.Equal(["money"], normalized.Metadata.Formats["AMOUNT"].Classes.ToArray());
    }
}
