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

    /// <summary>
    /// Binds canonical computed columns and filters starting from a report schema.
    /// </summary>
    /// <param name="specification">The immutable canonical table specification.</param>
    /// <param name="schemaName">The logical name assigned to the resulting relation contract.</param>
    /// <param name="initialSchema">The inherited schema used to create the initial output contract.</param>
    /// <param name="policy">Determines which filter column references remain allowed.</param>
    /// <param name="inheritedComputedCount">Authored computed-rule count already consumed by ancestors.</param>
    /// <param name="inheritedFilterCount">Authored filter-rule count already consumed by ancestors.</param>
    /// <param name="errors">Receives fatal budget, expression, and computed-column errors.</param>
    /// <param name="ignored">Receives non-fatal restricted-filter diagnostics.</param>
    /// <returns>The bound output contract, ordered mutations, and accumulated authored-rule counts.</returns>
    /// <remarks>Appends diagnostics to <paramref name="errors"/> and <paramref name="ignored"/>.</remarks>
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

    /// <summary>
    /// Binds canonical computed columns and filters starting from an immutable parent output contract.
    /// </summary>
    /// <param name="specification">The immutable canonical table specification.</param>
    /// <param name="schemaName">The logical name assigned to the resulting relation contract.</param>
    /// <param name="initialContract">The inherited logical columns, presentation, and lineage.</param>
    /// <param name="policy">Determines which filter column references remain allowed.</param>
    /// <param name="inheritedComputedCount">Authored computed-rule count already consumed by ancestors.</param>
    /// <param name="inheritedFilterCount">Authored filter-rule count already consumed by ancestors.</param>
    /// <param name="errors">Receives fatal budget, expression, and computed-column errors.</param>
    /// <param name="ignored">Receives non-fatal restricted-filter diagnostics.</param>
    /// <returns>The bound output contract, ordered mutations, and accumulated authored-rule counts.</returns>
    /// <exception cref="ArgumentNullException">Thrown when a required argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="schemaName"/> is blank.</exception>
    /// <remarks>Appends diagnostics to <paramref name="errors"/> and <paramref name="ignored"/>.</remarks>
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

    /// <summary>
    /// Selects the most specific owning collection path for diagnostics.
    /// </summary>
    /// <param name="rulePaths">The document paths of the rules being validated.</param>
    /// <param name="property">The rule-collection property to locate in each source path.</param>
    /// <returns>The ordinally first normalized owning collection path.</returns>
    private static string BestOwningCollectionPath(
        IEnumerable<string> rulePaths,
        string property)
        => rulePaths
            .Select(path => OwningCollectionPath(path, property))
            .OrderBy(path => path, StringComparer.Ordinal)
            .First();

    /// <summary>
    /// Returns the source path of the collection that owns a rule.
    /// </summary>
    /// <param name="rulePath">The document path used to locate validation diagnostics.</param>
    /// <param name="property">The rule-collection property whose owning collection is required.</param>
    /// <returns>The path of the collection that owns the generated value.</returns>
    private static string OwningCollectionPath(string rulePath, string property)
    {
        var marker = $".{property}[";
        var markerIndex = rulePath.LastIndexOf(marker, StringComparison.Ordinal);
        return markerIndex < 0
            ? rulePath
            : rulePath[..markerIndex] + $".{property}";
    }

    /// <summary>
    /// Returns the source path of the composable that owns a rule.
    /// </summary>
    /// <param name="path">A rule or collection path beneath the owning composable.</param>
    /// <param name="property">The rule-collection property whose owning composable is required.</param>
    /// <returns>The path of the composable that owns the generated value.</returns>
    private static string OwningComposablePath(string path, string property)
    {
        var collectionSuffix = $".{property}";
        var collectionPath = OwningCollectionPath(path, property);
        return collectionPath.EndsWith(collectionSuffix, StringComparison.Ordinal)
            ? collectionPath[..^collectionSuffix.Length]
            : collectionPath;
    }
}

/// <summary>Contains one table's bound exported mutations, resulting output, and cumulative authored-rule counts.</summary>
internal sealed record CanonicalRelationBinding(
    BoundOutputContract Output,
    ImmutableArray<BoundCanonicalRelationMutation> Mutations,
    int ComputedRuleCount,
    int FilterRuleCount)
{
    /// <summary>Gets a mutable report-schema projection of <see cref="Output"/>.</summary>
    public ReportSchema Schema => Output.ToReportSchema();

    /// <summary>
    /// Applies the bound relation mutations in canonical order to an input relation.
    /// </summary>
    /// <param name="input">The relation to which the mutations are applied.</param>
    /// <returns>The relation root after wrapping <paramref name="input"/> with each compute and filter mutation.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the binding contains an unknown mutation type.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="input"/> is <see langword="null"/>.</exception>
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

/// <summary>Defines one ordered, already-bound relation mutation and its output contract.</summary>
internal abstract record BoundCanonicalRelationMutation(
    string OperationPath,
    BoundOutputContract Output)
{
    /// <summary>Gets the semantic composable kind represented by the mutation.</summary>
    public abstract ComposableKind Kind { get; }
}

/// <summary>Adds one bound computed column to a relation.</summary>
internal sealed record BoundCanonicalComputeMutation(
    string Path,
    BoundOutputContract Contract,
    BoundComputedColumn Column)
    : BoundCanonicalRelationMutation(Path, Contract)
{
    /// <inheritdoc />
    public override ComposableKind Kind => ComposableKind.Compute;
}

/// <summary>Adds one batch of bound row predicates to a relation.</summary>
internal sealed record BoundCanonicalFilterMutation(
    string Path,
    BoundOutputContract Contract,
    ImmutableArray<BoundRowPredicate> Predicates)
    : BoundCanonicalRelationMutation(Path, Contract)
{
    /// <inheritdoc />
    public override ComposableKind Kind => ComposableKind.Filter;
}
