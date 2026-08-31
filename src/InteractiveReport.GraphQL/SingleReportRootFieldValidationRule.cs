// GraphQL validation boundary: independently limit fragment expansion, then expand reachable
// selections just far enough to reject multiple executable report fields. This prevents aliases
// and fragment DAGs from multiplying database work within one transport request.

using GraphQL.Validation;
using GraphQLParser.AST;

namespace InteractiveReport.GraphQL;

/// <summary>
/// Prevents one GraphQL operation from turning a single HTTP request into many
/// independent report executions through aliases or root-level fragments, and bounds
/// fragment-DAG expansion before GraphQL.NET performs its own field collection.
/// </summary>
internal sealed class SingleReportRootFieldValidationRule : ValidationRuleBase
{
    // Invariant: GraphQL.NET pre-counts recursively expanded fragment spreads before collecting
    // fields. Keep the adapter's explicit ceiling comfortably above ordinary queries, but low
    // enough that a small fragment DAG cannot amplify into material work.
    private const int MaxFragmentExpansions = 256;
    private const int ExceededFragmentExpansions = MaxFragmentExpansions + 1;
    private const string ErrorNumber = "IR-GQL-ROOT-FIELD-LIMIT";
    private const string ErrorMessage =
        "Only one executable 'report' root field is allowed per operation.";
    private const string FragmentErrorNumber = "IR-GQL-FRAGMENT-LIMIT";
    private static readonly string FragmentErrorMessage =
        $"The operation exceeds the fragment expansion limit of {MaxFragmentExpansions}.";

    /// <summary>
    /// Validates fragment expansion and report-root response keys during the post-validation hook.
    /// </summary>
    /// <param name="context">The GraphQL validation context containing the schema, document, and error sink.</param>
    /// <returns>A completed value task containing no visitor because this method performs the check immediately.</returns>
    /// <remarks>Reports validation errors through <paramref name="context"/> when limits are exceeded.</remarks>
    public override ValueTask<INodeVisitor?> GetPostNodeVisitorAsync(ValidationContext context)
    {
        if (context.Schema is not InteractiveReportGraphQLSchema)
        {
            return default;
        }

        var fragments = new Dictionary<string, GraphQLFragmentDefinition>(StringComparer.Ordinal);
        foreach (var fragment in context.Document.Definitions.OfType<GraphQLFragmentDefinition>())
        {
            fragments.TryAdd(fragment.FragmentName.Name.StringValue, fragment);
        }

        var fragmentExpansions = new FragmentExpansionCounter(fragments)
            .Count(context.Operation.SelectionSet);
        if (fragmentExpansions > MaxFragmentExpansions)
        {
            context.ReportError(new ValidationError(
                context.Document.Source,
                FragmentErrorNumber,
                FragmentErrorMessage,
                context.Operation));
            return default;
        }

        var responseKeys = new HashSet<string>(StringComparer.Ordinal);
        var visitedFragments = new HashSet<string>(StringComparer.Ordinal);
        var offendingField = FindSecondReportField(
            context.Operation.SelectionSet,
            context,
            fragments,
            responseKeys,
            visitedFragments);

        if (offendingField is not null)
        {
            context.ReportError(new ValidationError(
                context.Document.Source,
                ErrorNumber,
                ErrorMessage,
                offendingField));
        }

        return default;
    }

    /// <summary>
    /// Traverses fields and fragments until it finds a second distinct report response key.
    /// </summary>
    /// <param name="selectionSet">The GraphQL selection set to validate.</param>
    /// <param name="context">The GraphQL validation context containing the schema, document, and error sink.</param>
    /// <param name="fragments">The GraphQL fragment definitions available to the selection traversal.</param>
    /// <param name="responseKeys">The collection that receives distinct GraphQL response keys.</param>
    /// <param name="visitedFragments">The fragment names already traversed, used to prevent cycles.</param>
    /// <returns>The second distinct executable <c>report</c> field, or <see langword="null"/> when the operation contains at most one.</returns>
    private static GraphQLField? FindSecondReportField(
        GraphQLSelectionSet selectionSet,
        ValidationContext context,
        IReadOnlyDictionary<string, GraphQLFragmentDefinition> fragments,
        HashSet<string> responseKeys,
        HashSet<string> visitedFragments)
    {
        foreach (var selection in selectionSet.Selections)
        {
            if (!context.ShouldIncludeNode(selection))
            {
                continue;
            }

            switch (selection)
            {
                case GraphQLField field when field.Name.StringValue == "report":
                    var responseKey = field.Alias?.Name.StringValue ?? field.Name.StringValue;
                    if (responseKeys.Add(responseKey) && responseKeys.Count > 1)
                    {
                        return field;
                    }
                    break;

                case GraphQLInlineFragment inlineFragment:
                {
                    var offendingField = FindSecondReportField(
                        inlineFragment.SelectionSet,
                        context,
                        fragments,
                        responseKeys,
                        visitedFragments);
                    if (offendingField is not null)
                    {
                        return offendingField;
                    }
                    break;
                }

                case GraphQLFragmentSpread spread:
                {
                    var fragmentName = spread.FragmentName.Name.StringValue;
                    if (!fragments.TryGetValue(fragmentName, out var fragment)
                        || !context.ShouldIncludeNode(fragment)
                        || !visitedFragments.Add(fragmentName))
                    {
                        break;
                    }

                    var offendingField = FindSecondReportField(
                        fragment.SelectionSet,
                        context,
                        fragments,
                        responseKeys,
                        visitedFragments);
                    if (offendingField is not null)
                    {
                        return offendingField;
                    }
                    break;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Computes the number of fragment-spread visits represented by an operation's
    /// reachable fragment DAG. Each fragment body is evaluated once, then its
    /// saturated cost is reused for every incoming edge. Directives are deliberately
    /// ignored: GraphQL.NET performs its recursive spread pre-count before applying
    /// @skip/@include, and cached documents can execute with different variables.
    /// </summary>
    /// <param name="fragments">The document's fragment definitions, keyed by fragment name.</param>
    private sealed class FragmentExpansionCounter(
        IReadOnlyDictionary<string, GraphQLFragmentDefinition> fragments)
    {
        private readonly Dictionary<string, int> _costs = new(StringComparer.Ordinal);
        private readonly HashSet<string> _active = new(StringComparer.Ordinal);

        /// <summary>
        /// Counts fragment-spread expansions reachable from the supplied selection set.
        /// </summary>
        /// <param name="selectionSet">The GraphQL selection set to validate.</param>
        /// <returns>The expansion count, capped at the over-budget sentinel.</returns>
        public int Count(GraphQLSelectionSet selectionSet)
            => CountSelectionSet(selectionSet);

        /// <summary>
        /// Recursively counts fragment spreads contributed by a selection set.
        /// </summary>
        /// <param name="selectionSet">The GraphQL selection set to validate.</param>
        /// <returns>The expansion count, capped at the over-budget sentinel.</returns>
        private int CountSelectionSet(GraphQLSelectionSet selectionSet)
        {
            var count = 0;
            foreach (var selection in selectionSet.Selections)
            {
                var selectionCount = selection switch
                {
                    GraphQLField { SelectionSet: { } child } => CountSelectionSet(child),
                    GraphQLInlineFragment inlineFragment => CountSelectionSet(inlineFragment.SelectionSet),
                    GraphQLFragmentSpread spread => CountSpread(spread),
                    _ => 0,
                };
                count = AddSaturated(count, selectionCount);
                if (count == ExceededFragmentExpansions)
                {
                    return count;
                }
            }
            return count;
        }

        /// <summary>
        /// Counts one fragment spread plus its nested spreads while preventing recursion cycles.
        /// </summary>
        /// <param name="spread">The fragment spread to expand.</param>
        /// <returns>The bounded number of spread visits contributed by this edge.</returns>
        /// <remarks>Caches completed fragment costs and temporarily tracks the active recursion path.</remarks>
        private int CountSpread(GraphQLFragmentSpread spread)
        {
            var fragmentName = spread.FragmentName.Name.StringValue;
            if (!fragments.TryGetValue(fragmentName, out var fragment))
            {
                return 1;
            }
            if (_costs.TryGetValue(fragmentName, out var cached))
            {
                return AddSaturated(1, cached);
            }

            // Core validation reports cycles separately. Treat one as over-budget so this
            // security rule terminates deterministically even on an invalid DAG.
            if (_active.Count >= MaxFragmentExpansions || !_active.Add(fragmentName))
            {
                return ExceededFragmentExpansions;
            }

            int nested;
            try
            {
                nested = CountSelectionSet(fragment.SelectionSet);
            }
            finally
            {
                _active.Remove(fragmentName);
            }
            _costs[fragmentName] = nested;
            return AddSaturated(1, nested);
        }

        /// <summary>
        /// Adds two expansion counts without exceeding the sentinel used for an over-budget operation.
        /// </summary>
        /// <param name="left">The accumulated expansion count.</param>
        /// <param name="right">The expansion count to add.</param>
        /// <returns>The sum, capped at the exceeded-expansion sentinel.</returns>
        private static int AddSaturated(int left, int right)
            => left >= ExceededFragmentExpansions
                || right >= ExceededFragmentExpansions
                || left > MaxFragmentExpansions - right
                    ? ExceededFragmentExpansions
                    : left + right;
    }
}
