using System.Collections.Immutable;
using InteractiveReport.Core.Expressions;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Planning;
using InteractiveReport.Core.Schema;
using InteractiveReport.Core.Validation;

namespace InteractiveReport.Core.Tests;

public class CanonicalRelationBinderTests
{
    private static readonly ReportSchema OrdersSchema = ReportSchema.Create(
        "orders",
        TestFixtures.OrdersSchema);

    [Fact]
    public void Bind_extends_the_schema_in_canonical_dependency_order_before_filters()
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
                new TableComposable
                {
                    Kind = "compute",
                    Computed =
                    [
                        new ComputedColumn { Id = "ir2", Expr = "ir1 * 2" },
                        new ComputedColumn { Id = "ir1", Expr = "AMOUNT + 1" },
                    ],
                },
            ],
        };
        var specification = CanonicalTableNormalizer.Normalize(table, "tables.summary");

        var result = Bind(specification);

        Assert.Empty(result.Errors);
        Assert.Empty(result.Ignored);
        Assert.Equal(
            [ComposableKind.Compute, ComposableKind.Compute, ComposableKind.Filter],
            result.Binding.Mutations.Select(mutation => mutation.Kind));
        Assert.Equal(["ir1", "ir2"],
            result.Binding.Mutations
                .OfType<BoundCanonicalComputeMutation>()
                .Select(mutation => mutation.Column.Output.LogicalId));
        Assert.Equal(
            ["ir1", "ir2"],
            result.Binding.Schema.Columns
                .Skip(result.Binding.Schema.Count - 2)
                .Select(column => column.Name));
        Assert.Equal(2, result.Binding.ComputedRuleCount);
        Assert.Equal(1, result.Binding.FilterRuleCount);

        var secondDefinition = Assert.IsType<BoundCanonicalComputeMutation>(
            result.Binding.Mutations[1]).Column;
        var computedExpression = Assert.IsType<BinaryOp>(secondDefinition.Expression.Ast);
        Assert.Equal("ir1", Assert.IsType<ColumnRef>(computedExpression.Left).Column.Name);

        var filter = Assert.Single(Assert.IsType<BoundCanonicalFilterMutation>(
            result.Binding.Mutations[2]).Predicates);
        var predicate = Assert.IsType<Comparison>(filter.Expression.Ast);
        Assert.Equal("ir2", Assert.IsType<ColumnRef>(predicate.Left).Column.Name);

        var source = new BoundOpaqueSqlSource(
            "orders",
            "SELECT * FROM ORDERS",
            ReportDialect.Sqlite,
            BoundOutputContract.FromSchema("orders", OrdersSchema));
        var relation = Assert.IsType<BoundFilterRelation>(
            result.Binding.ApplyTo(source));
        var secondCompute = Assert.IsType<BoundComputeRelation>(relation.Input);
        var firstCompute = Assert.IsType<BoundComputeRelation>(secondCompute.Input);
        Assert.Same(source, firstCompute.Input);
        Assert.Equal("ir1", firstCompute.Column.Output.LogicalId);
        Assert.Equal("ir2", secondCompute.Column.Output.LogicalId);
        Assert.Equal(
            ["ir1"],
            Assert.IsType<BoundComputedColumnLineage>(
                secondCompute.Column.Output.Lineage).InputLogicalIds.ToArray());
    }

    [Fact]
    public void Bind_reports_computed_and_filter_expression_errors_at_their_original_paths()
    {
        var specification = Specification(
            computed:
            [
                new CanonicalComputedColumn(
                    "ir1",
                    null,
                    "AMOUNT +",
                    [],
                    "tables.summary.composables[7].computed[4]"),
            ],
            filters:
            [
                new CanonicalFilter(
                    "AMOUNT >",
                    "tables.summary.composables[2].filters[9]"),
            ]);

        var result = Bind(specification);

        Assert.Equal(
            [
                "tables.summary.composables[7].computed[4].expr",
                "tables.summary.composables[2].filters[9].expr",
            ],
            result.Errors.Select(error => error.Path));
        Assert.Empty(result.Binding.Mutations);
    }

    [Fact]
    public void Bind_reports_one_collection_error_when_computed_budget_crosses_inherited_limit()
    {
        var specification = Specification(
            computed:
            [
                Computed("ir1", "AMOUNT + 1", "tables.child.composables[7].computed[0]"),
                Computed("ir2", "AMOUNT + 2", "tables.child.composables[7].computed[1]"),
                Computed("ir3", "AMOUNT + 3", "tables.child.composables[7].computed[2]"),
            ]);

        var result = Bind(specification, inheritedComputedCount: 19);

        var error = Assert.Single(result.Errors);
        Assert.Equal("tables.child.composables[7].computed", error.Path);
        Assert.Equal("at most 20 computed columns per report state", error.Message);
        Assert.Empty(result.Binding.Mutations);
        Assert.False(result.Binding.Schema.TryGetValue("ir1", out _));
        Assert.False(result.Binding.Schema.TryGetValue("ir2", out _));
        Assert.Equal(22, result.Binding.ComputedRuleCount);
    }

    [Fact]
    public void Bind_counts_disabled_computed_rules_from_normalized_authored_population()
    {
        var composables = Enumerable.Range(0, 8)
            .Select(_ => new TableComposable { Kind = "labels" })
            .ToList();
        composables[7] = new TableComposable
        {
            Kind = "compute",
            Computed =
            [
                new ComputedColumn
                {
                    Enabled = false,
                    Id = "not-a-synthetic-id",
                    Expr = "not valid expression syntax +",
                },
            ],
        };
        composables[2] = new TableComposable
        {
            Kind = "compute",
            Computed =
            [
                new ComputedColumn
                {
                    Enabled = false,
                    Id = "also-invalid",
                    Expr = "also invalid +",
                },
            ],
        };
        var specification = CanonicalTableNormalizer.Normalize(
            new ReportTable { Composables = composables },
            "tables.child");

        Assert.Empty(specification.Computed);
        Assert.Equal(2, specification.ComputedPopulation.AuthoredCount);
        Assert.Equal(
            [
                "tables.child.composables[2].computed",
                "tables.child.composables[7].computed",
            ],
            specification.ComputedPopulation.CollectionPaths.ToArray());

        var result = Bind(specification, inheritedComputedCount: 19);

        var error = Assert.Single(result.Errors);
        Assert.Equal("tables.child.composables[2].computed", error.Path);
        Assert.Equal("at most 20 computed columns per report state", error.Message);
        Assert.Empty(result.Binding.Mutations);
        Assert.Equal(21, result.Binding.ComputedRuleCount);
    }

    [Fact]
    public void Bind_collapses_an_inherited_filter_budget_failure_to_one_deterministic_collection_error()
    {
        var specification = Specification(
            filters:
            [
                new CanonicalFilter(
                    "AMOUNT > 1",
                    "tables.child.composables[9].filters[5]"),
                new CanonicalFilter(
                    "AMOUNT > 2",
                    "tables.child.composables[2].filters[8]"),
            ]);

        var result = Bind(specification, inheritedFilterCount: 49);

        var error = Assert.Single(result.Errors);
        Assert.Equal("tables.child.composables[2].filters", error.Path);
        Assert.Equal("at most 50 filter rules per report state", error.Message);
        Assert.Empty(result.Binding.Mutations);
        Assert.Equal(51, result.Binding.FilterRuleCount);
    }

    [Fact]
    public void Bind_counts_disabled_filters_from_normalized_authored_population()
    {
        var composables = Enumerable.Range(0, 9)
            .Select(_ => new TableComposable { Kind = "labels" })
            .ToList();
        composables[8] = new TableComposable
        {
            Kind = "filter",
            Filters =
            [
                new FilterRule
                {
                    Enabled = false,
                    Expr = "not valid expression syntax +",
                },
            ],
        };
        composables[3] = new TableComposable
        {
            Kind = "filter",
            Filters =
            [
                new FilterRule
                {
                    Enabled = false,
                    Expr = "also invalid +",
                },
            ],
        };
        var specification = CanonicalTableNormalizer.Normalize(
            new ReportTable { Composables = composables },
            "tables.child");

        Assert.Empty(specification.Filters);
        Assert.Equal(2, specification.FilterPopulation.AuthoredCount);
        Assert.Equal(
            [
                "tables.child.composables[3].filters",
                "tables.child.composables[8].filters",
            ],
            specification.FilterPopulation.CollectionPaths.ToArray());

        var result = Bind(specification, inheritedFilterCount: 49);

        var error = Assert.Single(result.Errors);
        Assert.Equal("tables.child.composables[3].filters", error.Path);
        Assert.Equal("at most 50 filter rules per report state", error.Message);
        Assert.Empty(result.Binding.Mutations);
        Assert.Equal(51, result.Binding.FilterRuleCount);
    }

    [Fact]
    public void Bind_strips_filters_on_restricted_base_columns_but_keeps_computed_column_filters()
    {
        var specification = Specification(
            computed:
            [
                Computed("ir1", "AMOUNT + 1", "tables.summary.composables[0].computed[0]"),
            ],
            filters:
            [
                new CanonicalFilter(
                    "AMOUNT > 10",
                    "tables.summary.composables[1].filters[0]"),
                new CanonicalFilter(
                    "ir1 > 10",
                    "tables.summary.composables[1].filters[1]"),
            ]);
        var policy = ColumnPolicy.From(new ReportDefinition
        {
            Columns = new Dictionary<string, ReportColumnOverride>(StringComparer.OrdinalIgnoreCase)
            {
                ["AMOUNT"] = new() { Filterable = false },
            },
        });

        var result = Bind(specification, policy: policy);

        Assert.Empty(result.Errors);
        Assert.Equal(
            [new IgnoredItem("filter", "filter references non-filterable column 'AMOUNT'")],
            result.Ignored);
        Assert.Equal(
            [ComposableKind.Compute, ComposableKind.Filter],
            result.Binding.Mutations.Select(mutation => mutation.Kind));
        var filter = Assert.Single(Assert.IsType<BoundCanonicalFilterMutation>(
            result.Binding.Mutations[1]).Predicates);
        var predicate = Assert.IsType<Comparison>(filter.Expression.Ast);
        Assert.Equal("ir1", Assert.IsType<ColumnRef>(predicate.Left).Column.Name);
    }

    private static CanonicalComputedColumn Computed(
        string id,
        string expression,
        string sourcePath)
        => new(id, null, expression, [], sourcePath);

    private static CanonicalTableSpec Specification(
        ImmutableArray<CanonicalComputedColumn> computed = default,
        ImmutableArray<CanonicalFilter> filters = default)
        => new(
            Shape: null,
            Computed: computed.IsDefault ? [] : computed,
            ComputedPopulation: Population(
                computed.IsDefault ? [] : computed.Select(value => value.SourcePath),
                "computed"),
            Filters: filters.IsDefault ? [] : filters,
            FilterPopulation: Population(
                filters.IsDefault ? [] : filters.Select(value => value.SourcePath),
                "filters"),
            Metadata: new CanonicalMetadata(
                ClearsInheritedLabels: false,
                Labels: ImmutableDictionary.Create<string, string>(StringComparer.OrdinalIgnoreCase),
                ClearsInheritedFormats: false,
                Formats: ImmutableDictionary.Create<string, CanonicalColumnFormat>(StringComparer.OrdinalIgnoreCase)),
            Local: new CanonicalLocalResult(
                null,
                null,
                [],
                CanonicalRulePopulation.Empty,
                null,
                []),
            NaturalOrder: []);

    private static CanonicalRulePopulation Population(
        IEnumerable<string> rulePaths,
        string property)
    {
        var paths = rulePaths.ToArray();
        var marker = $".{property}[";
        return new CanonicalRulePopulation(
            paths.Length,
            paths.Select(path =>
                {
                    var index = path.LastIndexOf(marker, StringComparison.Ordinal);
                    return index < 0 ? path : path[..index] + $".{property}";
                })
                .Distinct(StringComparer.Ordinal)
                .ToImmutableArray());
    }

    private static BindResult Bind(
        CanonicalTableSpec specification,
        ColumnPolicy? policy = null,
        int inheritedComputedCount = 0,
        int inheritedFilterCount = 0)
    {
        var errors = new List<ValidationError>();
        var ignored = new List<IgnoredItem>();
        var binding = CanonicalRelationBinder.Bind(
            specification,
            "orders",
            OrdersSchema,
            policy ?? ColumnPolicy.Unrestricted,
            inheritedComputedCount,
            inheritedFilterCount,
            errors,
            ignored);
        return new BindResult(binding, errors, ignored);
    }

    private sealed record BindResult(
        CanonicalRelationBinding Binding,
        List<ValidationError> Errors,
        List<IgnoredItem> Ignored);
}
