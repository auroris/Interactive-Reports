using System.Collections.Immutable;
using InteractiveReport.Core.Expressions;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Planning;
using InteractiveReport.Core.Schema;

namespace InteractiveReport.Core.Validation;

/// <summary>
/// Binds the exported relation mutations of one canonical table. Computed columns
/// are already dependency ordered by the normalizer and therefore extend the
/// binding schema one node at a time. Filters bind only after that schema is
/// complete. No mutable report-document DTO crosses this boundary.
/// </summary>
internal static class CanonicalRelationBinder
{
    private const int MaxComputedColumns = 20;
    private const int MaxFilters = 50;

    public static CanonicalRelationBinding Bind(
        CanonicalTableSpec specification,
        string schemaName,
        ReportSchema initialSchema,
        ColumnPolicy policy,
        int inheritedComputedCount,
        int inheritedFilterCount,
        List<ValidationError> errors,
        List<IgnoredItem> ignored)
        => Bind(
            specification,
            schemaName,
            BoundOutputContract.FromSchema(schemaName, initialSchema),
            policy,
            inheritedComputedCount,
            inheritedFilterCount,
            errors,
            ignored);

    public static CanonicalRelationBinding Bind(
        CanonicalTableSpec specification,
        string schemaName,
        BoundOutputContract initialContract,
        ColumnPolicy policy,
        int inheritedComputedCount,
        int inheritedFilterCount,
        List<ValidationError> errors,
        List<IgnoredItem> ignored)
    {
        ArgumentNullException.ThrowIfNull(specification);
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaName);
        ArgumentNullException.ThrowIfNull(initialContract);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(errors);
        ArgumentNullException.ThrowIfNull(ignored);

        var output = initialContract.Rename(schemaName);
        var mutations = ImmutableArray.CreateBuilder<BoundCanonicalRelationMutation>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var computedCount = inheritedComputedCount
            + specification.ComputedPopulation.AuthoredCount;
        var computedBudgetExceeded = computedCount > MaxComputedColumns;

        if (computedBudgetExceeded)
        {
            errors.Add(new ValidationError(
                specification.ComputedPopulation.BudgetPath("computed"),
                $"at most {MaxComputedColumns} computed columns per report state"));
        }
        else
        {
            foreach (var node in specification.Computed)
            {
                var schema = output.ToReportSchema();
                var createEffect = ComputedColumnValidator.PrepareEffect(
                    node.Id,
                    node.Label,
                    schema.Lookup,
                    seenIds,
                    errors,
                    node.SourcePath);
                if (createEffect is null) continue;

                var expression = ExpressionRuleCompiler.Bind(
                    node.Expression,
                    schema.Lookup,
                    ExpressionRequirement.Value,
                    $"{node.SourcePath}.expr",
                    errors);
                if (expression is null) continue;

                var effect = createEffect(expression);
                var inputs = ExprColumns.Collect(expression.Ast)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(name => name, StringComparer.Ordinal)
                    .ToImmutableArray();
                var column = BoundColumnContract.FromColumn(
                    effect.Column,
                    new BoundComputedColumnLineage(inputs));
                output = output.Append(column);
                mutations.Add(new BoundCanonicalComputeMutation(
                    OwningComposablePath(node.SourcePath, "computed"),
                    output,
                    new BoundComputedColumn(expression, column, node.SourcePath)));
            }
        }

        var filterCount = inheritedFilterCount
            + specification.FilterPopulation.AuthoredCount;
        if (filterCount > MaxFilters
            && specification.FilterPopulation.AuthoredCount > 0)
        {
            errors.Add(new ValidationError(
                specification.FilterPopulation.BudgetPath("filters"),
                $"at most {MaxFilters} filter rules per report state"));
        }
        else if (!specification.Filters.IsEmpty && !computedBudgetExceeded)
        {
            var schema = output.ToReportSchema();
            var operationPath = BestOwningCollectionPath(
                specification.Filters.Select(filter => filter.SourcePath),
                "filters");
            var candidates = new List<(CanonicalFilter Node, CompiledRule<IncludeRowEffect> Rule)>(
                specification.Filters.Length);
            foreach (var node in specification.Filters)
            {
                var expression = ExpressionRuleCompiler.Bind(
                    node.Expression,
                    schema.Lookup,
                    ExpressionRequirement.Predicate,
                    $"{node.SourcePath}.expr",
                    errors);
                if (expression is not null)
                    candidates.Add((
                        node,
                        new CompiledRule<IncludeRowEffect>(
                            expression,
                            new IncludeRowEffect())));
            }

            var rules = ColumnBindingRules.StripRestrictedFilters(
                candidates.Select(candidate => candidate.Rule).ToList(),
                policy,
                schema,
                ignored);
            if (rules.Count > 0)
            {
                var predicates = candidates
                    .Where(candidate => rules.Any(rule => ReferenceEquals(rule, candidate.Rule)))
                    .Select(candidate => new BoundRowPredicate(
                        candidate.Rule.Expression,
                        candidate.Node.SourcePath))
                    .ToImmutableArray();
                mutations.Add(new BoundCanonicalFilterMutation(
                    OwningComposablePath(operationPath, "filters"),
                    output,
                    predicates));
            }
        }

        return new CanonicalRelationBinding(
            output,
            mutations.ToImmutable(),
            computedCount,
            filterCount);
    }

    private static string BestOwningCollectionPath(
        IEnumerable<string> rulePaths,
        string property)
        => rulePaths
            .Select(path => OwningCollectionPath(path, property))
            .OrderBy(path => path, StringComparer.Ordinal)
            .First();

    private static string OwningCollectionPath(string rulePath, string property)
    {
        var marker = $".{property}[";
        var markerIndex = rulePath.LastIndexOf(marker, StringComparison.Ordinal);
        return markerIndex < 0
            ? rulePath
            : rulePath[..markerIndex] + $".{property}";
    }

    private static string OwningComposablePath(string path, string property)
    {
        var collectionSuffix = $".{property}";
        var collectionPath = OwningCollectionPath(path, property);
        return collectionPath.EndsWith(collectionSuffix, StringComparison.Ordinal)
            ? collectionPath[..^collectionSuffix.Length]
            : collectionPath;
    }
}

internal sealed record CanonicalRelationBinding(
    BoundOutputContract Output,
    ImmutableArray<BoundCanonicalRelationMutation> Mutations,
    int ComputedRuleCount,
    int FilterRuleCount)
{
    public ReportSchema Schema => Output.ToReportSchema();

    public BoundRelationNode ApplyTo(BoundRelationNode input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var relation = input;
        foreach (var mutation in Mutations)
        {
            relation = mutation switch
            {
                BoundCanonicalComputeMutation compute => new BoundComputeRelation(
                    relation,
                    compute.Column,
                    compute.Output,
                    compute.OperationPath),
                BoundCanonicalFilterMutation filter => new BoundFilterRelation(
                    relation,
                    filter.Predicates,
                    filter.Output,
                    filter.OperationPath),
                _ => throw new InvalidOperationException(
                    $"Unknown canonical relation mutation '{mutation.GetType().Name}'."),
            };
        }
        return relation;
    }
}

internal abstract record BoundCanonicalRelationMutation(
    string OperationPath,
    BoundOutputContract Output)
{
    public abstract ComposableKind Kind { get; }
}

internal sealed record BoundCanonicalComputeMutation(
    string Path,
    BoundOutputContract Contract,
    BoundComputedColumn Column)
    : BoundCanonicalRelationMutation(Path, Contract)
{
    public override ComposableKind Kind => ComposableKind.Compute;
}

internal sealed record BoundCanonicalFilterMutation(
    string Path,
    BoundOutputContract Contract,
    ImmutableArray<BoundRowPredicate> Predicates)
    : BoundCanonicalRelationMutation(Path, Contract)
{
    public override ComposableKind Kind => ComposableKind.Filter;
}
