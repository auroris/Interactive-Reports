// Canonical planning entrypoint: converts mutable table DTOs into an order-independent
// canonical specification. The normalized graph is the trust boundary consumed by binders and
// compilers, so document array position never becomes executable semantics.

using System.Collections.Immutable;
using InteractiveReport.Core.Expressions;
using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Planning;

/// <summary>
/// Converts the mutable composable declarations for one table into the immutable plan consumed by
/// binders and compilers. It merges singleton and metadata operations, topologically orders computed
/// columns, assigns stable highlight precedence, and rejects conflicting declarations. Source array
/// positions are retained for diagnostics but do not determine execution order.
/// </summary>
internal static class CanonicalTableNormalizer
{
    /// <summary>
    /// Takes a report table and its diagnostic path, then snapshots and validates every composable into
    /// a deterministic canonical specification. The input table is not modified.
    /// </summary>
    /// <param name="table">The mutable table definition whose composables will be canonicalized.</param>
    /// <param name="tablePath">The root document path to include in validation errors; defaults to <c>table</c>.</param>
    /// <returns>A deep immutable specification ordered by composable phase and dependency.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="table"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="tablePath"/> is empty or whitespace.</exception>
    /// <exception cref="ReportValidationException">Thrown with all collected declaration and expression errors.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the composable catalog contains a kind the normalizer cannot handle.</exception>
    public static CanonicalTableSpec Normalize(
        ReportTable table,
        string tablePath = "table")
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(tablePath);

        var errors = new List<ValidationError>();
        var shapes = new List<ShapeDeclaration>();
        var computed = new List<PendingComputed>();
        var filters = new List<CanonicalFilter>();
        var selections = new List<CanonicalSelection>();
        var orderings = new List<CanonicalOrdering>();
        var highlights = new List<CanonicalHighlight>();
        var breakSets = new List<CanonicalBreaks>();
        var aggregates = new List<CanonicalAggregate>();
        var labels = new Dictionary<string, LabelAssignment>(StringComparer.OrdinalIgnoreCase);
        var formats = new Dictionary<string, FormatAssignment>(StringComparer.OrdinalIgnoreCase);
        var labelPaths = new List<string>();
        var formatPaths = new List<string>();
        var computedCollectionPaths = new List<string>();
        var filterCollectionPaths = new List<string>();
        var highlightCollectionPaths = new List<string>();
        var authoredComputedCount = 0;
        var authoredFilterCount = 0;
        var authoredHighlightCount = 0;
        var clearsLabels = false;
        var clearsFormats = false;

        var composables = table.Composables ?? [];
        for (var composableIndex = 0; composableIndex < composables.Count; composableIndex++)
        {
            var composablePath = $"{tablePath}.composables[{composableIndex}]";
            var composable = composables[composableIndex];
            if (composable is null)
            {
                errors.Add(new ValidationError(composablePath, "composable cannot be null"));
                continue;
            }

            if (!ComposableSemanticsCatalog.TryResolve(composable.Kind, out var semantics))
            {
                errors.Add(new ValidationError(
                    $"{composablePath}.kind",
                    string.IsNullOrWhiteSpace(composable.Kind)
                        ? "composable kind is required"
                        : $"unknown composable kind '{composable.Kind}'"));
                continue;
            }

            switch (semantics.Kind)
            {
                case ComposableKind.Group:
                case ComposableKind.Pivot:
                case ComposableKind.Chart:
                    shapes.Add(new ShapeDeclaration(semantics.Kind, composable, composablePath));
                    break;

                case ComposableKind.Compute:
                    computedCollectionPaths.Add($"{composablePath}.computed");
                    authoredComputedCount += composable.Computed?.Count ?? 0;
                    SnapshotComputed(composable, composablePath, computed, errors);
                    break;

                case ComposableKind.Filter:
                    filterCollectionPaths.Add($"{composablePath}.filters");
                    authoredFilterCount += composable.Filters?.Count ?? 0;
                    SnapshotFilters(composable, composablePath, filters, errors);
                    break;

                case ComposableKind.Labels:
                    labelPaths.Add(composablePath);
                    if (composable.Labels is { Count: 0 }) clearsLabels = true;
                    SnapshotLabels(composable, composablePath, labels, errors);
                    break;

                case ComposableKind.Formats:
                    formatPaths.Add(composablePath);
                    if (composable.Formats is { Count: 0 }) clearsFormats = true;
                    SnapshotFormats(composable, composablePath, formats, errors);
                    break;

                case ComposableKind.Select:
                    selections.Add(SnapshotSelection(composable, composablePath));
                    break;

                case ComposableKind.Sort:
                    orderings.Add(SnapshotOrdering(composable, composablePath, errors));
                    break;

                case ComposableKind.Highlight:
                    highlightCollectionPaths.Add($"{composablePath}.highlights");
                    authoredHighlightCount += composable.Highlights?.Count ?? 0;
                    SnapshotHighlights(composable, composablePath, highlights, errors);
                    break;

                case ComposableKind.Break:
                    breakSets.Add(SnapshotBreaks(composable, composablePath));
                    break;

                case ComposableKind.Aggregate:
                    SnapshotAggregates(composable, composablePath, aggregates, errors);
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Composable semantics for '{semantics.Kind}' have no normalizer.");
            }
        }

        var shape = NormalizeShape(shapes, errors);
        var orderedComputed = OrderComputed(computed, errors);
        var orderedFilters = filters
            .OrderBy(filter => filter.Expression, StringComparer.Ordinal)
            .ThenBy(filter => filter.SourcePath, StringComparer.Ordinal)
            .ToImmutableArray();

        var selection = ResolveSingleton(
            selections,
            ComposableKind.Select,
            static value => value.SourcePath,
            SelectionEquals,
            errors);
        var ordering = ResolveSingleton(
            orderings,
            ComposableKind.Sort,
            static value => value.SourcePath,
            OrderingEquals,
            errors);
        var breaks = ResolveSingleton(
            breakSets,
            ComposableKind.Break,
            static value => value.SourcePath,
            BreaksEqual,
            errors);

        var orderedHighlights = NormalizeHighlights(highlights, errors);
        var orderedAggregates = NormalizeAggregates(aggregates);
        var metadata = new CanonicalMetadata(
            clearsLabels,
            labels.ToImmutableDictionary(
                pair => pair.Value.Column,
                pair => pair.Value.Value,
                StringComparer.OrdinalIgnoreCase),
            clearsFormats,
            formats.ToImmutableDictionary(
                pair => pair.Key,
                pair => pair.Value.Value,
                StringComparer.OrdinalIgnoreCase));
        var local = new CanonicalLocalResult(
            selection,
            ordering,
            orderedHighlights,
            new CanonicalRulePopulation(
                authoredHighlightCount,
                highlightCollectionPaths.ToImmutableArray()),
            breaks,
            orderedAggregates);

        if (errors.Count > 0)
            throw new ReportValidationException(errors);

        return new CanonicalTableSpec(
            shape,
            orderedComputed,
            new CanonicalRulePopulation(
                authoredComputedCount,
                computedCollectionPaths.ToImmutableArray()),
            orderedFilters,
            new CanonicalRulePopulation(
                authoredFilterCount,
                filterCollectionPaths.ToImmutableArray()),
            metadata,
            local,
            BuildNaturalOrder(
                shape,
                orderedComputed,
                orderedFilters,
                labelPaths,
                formatPaths,
                local));
    }

    /// <summary>
    /// Resolves the table's optional group, pivot, or chart shape and reports multiple shape declarations.
    /// </summary>
    /// <param name="declarations">The shape declarations collected from the table.</param>
    /// <param name="errors">The validation list to append to when more than one shape is present.</param>
    /// <returns>The sole canonical shape, or <see langword="null"/> when none exists or the declarations conflict.</returns>
    /// <exception cref="InvalidOperationException">Thrown when a declaration is not a recognized shape kind.</exception>
    private static CanonicalShape? NormalizeShape(
        IReadOnlyList<ShapeDeclaration> declarations,
        List<ValidationError> errors)
    {
        if (declarations.Count == 0) return null;

        if (declarations.Count > 1)
        {
            var kinds = string.Join(
                ", ",
                declarations.Select(declaration =>
                    $"'{ComposableSemanticsCatalog.Get(declaration.Kind).DocumentKind}'"));
            errors.Add(new ValidationError(
                declarations[1].Path,
                $"a table may contain at most one shape composable; found {kinds}"));
            return null;
        }

        var declaration = declarations[0];
        var value = declaration.Value;
        return declaration.Kind switch
        {
            ComposableKind.Group => new CanonicalGroupShape(
                SnapshotStrings(value.By),
                SnapshotMetrics(value.Values, $"{declaration.Path}.values", errors),
                declaration.Path),
            ComposableKind.Pivot => new CanonicalPivotShape(
                SnapshotStrings(value.Rows),
                SnapshotStrings(value.Cols),
                SnapshotMetrics(value.Values, $"{declaration.Path}.values", errors),
                value.Totals ?? false,
                declaration.Path),
            ComposableKind.Chart => new CanonicalChartShape(
                value.Type,
                value.Label,
                value.Value,
                value.Fn,
                value.Orientation,
                value.Sort is null
                    ? null
                    : new CanonicalChartSort(value.Sort.By, value.Sort.Dir),
                value.LabelAxisTitle,
                value.ValueAxisTitle,
                declaration.Path),
            _ => throw new InvalidOperationException(
                $"'{declaration.Kind}' is not a shape composable."),
        };
    }

    /// <summary>
    /// Copies enabled computed-column rules into pending immutable records and validates required identifiers.
    /// </summary>
    /// <param name="composable">The compute composable whose rules will be copied.</param>
    /// <param name="composablePath">The composable's source path, used to identify each rule in diagnostics.</param>
    /// <param name="into">The list that receives each valid, enabled declaration.</param>
    /// <param name="errors">The validation list that receives null-rule and missing-identifier errors.</param>
    /// <remarks>This method appends to <paramref name="into"/> and <paramref name="errors"/>; it does not modify the composable.</remarks>
    private static void SnapshotComputed(
        TableComposable composable,
        string composablePath,
        List<PendingComputed> into,
        List<ValidationError> errors)
    {
        if (composable.Computed is null) return;

        for (var index = 0; index < composable.Computed.Count; index++)
        {
            var path = $"{composablePath}.computed[{index}]";
            var rule = composable.Computed[index];
            if (rule is null)
            {
                errors.Add(new ValidationError(path, "computed column cannot be null"));
                continue;
            }
            if (!rule.Enabled) continue;
            if (string.IsNullOrWhiteSpace(rule.Id))
            {
                errors.Add(new ValidationError($"{path}.id", "computed column id is required"));
                continue;
            }

            into.Add(new PendingComputed(
                rule.Id,
                rule.Label,
                rule.Expr ?? "",
                path));
        }
    }

    /// <summary>
    /// Parses computed-column expressions, reports duplicate identifiers or dependency cycles, and returns
    /// the declarations in stable topological order. References to non-computed columns remain source-column dependencies.
    /// </summary>
    /// <param name="declarations">The pending computed columns to validate and order.</param>
    /// <param name="errors">The validation list that receives duplicate, parse, and cycle errors.</param>
    /// <returns>Canonical computed columns ordered so each computed dependency precedes its consumers.</returns>
    /// <remarks>The returned array is completed even after a cycle is found so callers can continue collecting diagnostics.</remarks>
    private static ImmutableArray<CanonicalComputedColumn> OrderComputed(
        IReadOnlyList<PendingComputed> declarations,
        List<ValidationError> errors)
    {
        if (declarations.Count == 0) return [];

        var byId = new Dictionary<string, PendingComputed>(StringComparer.OrdinalIgnoreCase);
        foreach (var declaration in declarations)
        {
            if (!byId.TryAdd(declaration.Id, declaration))
            {
                errors.Add(new ValidationError(
                    $"{declaration.Path}.id",
                    $"duplicate computed column id '{declaration.Id}'"));
            }
        }

        var dependencies = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var declaration in byId.Values)
        {
            var references = CollectExpressionColumns(
                declaration.Expression,
                $"{declaration.Path}.expr",
                errors);
            dependencies[declaration.Id] = references
                .Where(byId.ContainsKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        var originalDependencies = dependencies.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToHashSet(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

        var dependents = byId.Keys.ToDictionary(
            id => id,
            _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        foreach (var (id, required) in dependencies)
            foreach (var dependency in required)
                dependents[dependency].Add(id);

        var comparer = StableNameComparer.Instance;
        var ready = new SortedSet<string>(comparer);
        foreach (var id in byId.Keys)
            if (dependencies[id].Count == 0)
                ready.Add(id);

        var result = ImmutableArray.CreateBuilder<CanonicalComputedColumn>(byId.Count);
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (ready.Count > 0)
        {
            var id = ready.Min!;
            ready.Remove(id);
            emitted.Add(id);

            var declaration = byId[id];
            result.Add(new CanonicalComputedColumn(
                declaration.Id,
                declaration.Label,
                declaration.Expression,
                originalDependencies[id].OrderBy(value => value, comparer).ToImmutableArray(),
                declaration.Path));

            foreach (var dependent in dependents[id].OrderBy(value => value, comparer))
            {
                dependencies[dependent].Remove(id);
                if (dependencies[dependent].Count == 0)
                    ready.Add(dependent);
            }
        }

        if (result.Count != byId.Count)
        {
            var unresolved = byId.Keys
                .Where(id => !emitted.Contains(id))
                .OrderBy(id => id, comparer)
                .ToArray();
            var first = byId[unresolved[0]];
            errors.Add(new ValidationError(
                $"{first.Path}.expr",
                $"computed column dependency cycle involves {string.Join(", ", unresolved.Select(id => $"'{id}'"))}"));

            // Invariant: keep the object complete for diagnostics. Normalize never returns it
            // while errors are present.
            foreach (var id in unresolved)
            {
                var declaration = byId[id];
                result.Add(new CanonicalComputedColumn(
                    declaration.Id,
                    declaration.Label,
                    declaration.Expression,
                    originalDependencies[id].OrderBy(value => value, comparer).ToImmutableArray(),
                    declaration.Path));
            }
        }

        return result.ToImmutable();
    }

    /// <summary>
    /// Parses one portable expression and collects its distinct column references.
    /// </summary>
    /// <param name="expression">The expression text to inspect.</param>
    /// <param name="path">The expression's source path for validation errors.</param>
    /// <param name="errors">The validation list that receives empty, length, or syntax errors.</param>
    /// <returns>A case-insensitive set of referenced column names, or an empty set when parsing fails.</returns>
    private static HashSet<string> CollectExpressionColumns(
        string expression,
        string path,
        List<ValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            errors.Add(new ValidationError(path, "expression is empty"));
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        if (expression.Length > 2000)
        {
            errors.Add(new ValidationError(path, "expression exceeds 2000 characters"));
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var syntax = ExprSyntaxParser.Parse(expression);
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectSyntaxColumns(syntax, result);
            return result;
        }
        catch (ExprError ex)
        {
            errors.Add(new ValidationError(path, ex.Message));
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Recursively walks an expression tree and adds every name node to a case-insensitive destination set.
    /// </summary>
    /// <param name="syntax">The parsed expression node to traverse.</param>
    /// <param name="into">The set that receives referenced names; existing entries are preserved.</param>
    private static void CollectSyntaxColumns(SyntaxNode syntax, HashSet<string> into)
    {
        switch (syntax)
        {
            case NameSyntax name:
                into.Add(name.Name);
                break;
            case CallSyntax call:
                foreach (var argument in call.Args) CollectSyntaxColumns(argument, into);
                break;
            case UnarySyntax unary:
                CollectSyntaxColumns(unary.Operand, into);
                break;
            case BinarySyntax binary:
                CollectSyntaxColumns(binary.Left, into);
                CollectSyntaxColumns(binary.Right, into);
                break;
            case NullTestSyntax test:
                CollectSyntaxColumns(test.Operand, into);
                break;
            case BetweenSyntax between:
                CollectSyntaxColumns(between.Operand, into);
                CollectSyntaxColumns(between.Lower, into);
                CollectSyntaxColumns(between.Upper, into);
                break;
            case CaseSyntax @case:
                if (@case.Operand is { } operand) CollectSyntaxColumns(operand, into);
                foreach (var branch in @case.Whens)
                {
                    CollectSyntaxColumns(branch.When, into);
                    CollectSyntaxColumns(branch.Then, into);
                }
                if (@case.Else is { } otherwise) CollectSyntaxColumns(otherwise, into);
                break;
        }
    }

    /// <summary>
    /// Copies enabled filter rules into canonical records while retaining source paths for diagnostics.
    /// </summary>
    /// <param name="composable">The filter composable whose rules will be copied.</param>
    /// <param name="composablePath">The composable's source path, used to identify each rule.</param>
    /// <param name="into">The list that receives valid, enabled filters.</param>
    /// <param name="errors">The validation list that receives null-rule errors.</param>
    /// <remarks>This method appends to <paramref name="into"/> and <paramref name="errors"/>.</remarks>
    private static void SnapshotFilters(
        TableComposable composable,
        string composablePath,
        List<CanonicalFilter> into,
        List<ValidationError> errors)
    {
        if (composable.Filters is null) return;

        for (var index = 0; index < composable.Filters.Count; index++)
        {
            var path = $"{composablePath}.filters[{index}]";
            var rule = composable.Filters[index];
            if (rule is null)
            {
                errors.Add(new ValidationError(path, "filter cannot be null"));
                continue;
            }
            if (rule.Enabled)
                into.Add(new CanonicalFilter(rule.Expr ?? "", path));
        }
    }

    /// <summary>
    /// Merges non-empty label assignments by case-insensitive column name and reports differing values for the same column.
    /// </summary>
    /// <param name="composable">The labels composable whose map will be merged.</param>
    /// <param name="composablePath">The composable's source path for conflict diagnostics.</param>
    /// <param name="into">The canonical label map to update.</param>
    /// <param name="errors">The validation list that receives conflicting-label errors.</param>
    /// <remarks>Whitespace-only keys and values are ignored. Accepted label values are trimmed.</remarks>
    private static void SnapshotLabels(
        TableComposable composable,
        string composablePath,
        Dictionary<string, LabelAssignment> into,
        List<ValidationError> errors)
    {
        if (composable.Labels is null) return;

        foreach (var (column, value) in composable.Labels)
        {
            var path = $"{composablePath}.labels.{column}";
            if (string.IsNullOrWhiteSpace(column) || string.IsNullOrWhiteSpace(value))
                continue;

            var normalized = value.Trim();
            if (into.TryGetValue(column, out var existing))
            {
                if (!string.Equals(existing.Value, normalized, StringComparison.Ordinal))
                    errors.Add(new ValidationError(
                        path,
                        $"conflicting label declarations for column '{column}'"));
                else if (StableNameComparer.Instance.Compare(column, existing.Column) < 0)
                    into[column] = existing with { Column = column };
                continue;
            }
            into.Add(column, new LabelAssignment(column, normalized, path));
        }
    }

    /// <summary>
    /// Snapshots and merges column formats by case-insensitive column name, reporting null or conflicting assignments.
    /// </summary>
    /// <param name="composable">The formats composable whose map will be merged.</param>
    /// <param name="composablePath">The composable's source path for validation errors.</param>
    /// <param name="into">The canonical format map to update.</param>
    /// <param name="errors">The validation list that receives null-format and conflict errors.</param>
    private static void SnapshotFormats(
        TableComposable composable,
        string composablePath,
        Dictionary<string, FormatAssignment> into,
        List<ValidationError> errors)
    {
        if (composable.Formats is null) return;

        foreach (var (column, value) in composable.Formats)
        {
            var path = $"{composablePath}.formats.{column}";
            if (value is null)
            {
                errors.Add(new ValidationError(path, "column format cannot be null"));
                continue;
            }

            var snapshot = SnapshotFormat(value);
            if (into.TryGetValue(column, out var existing))
            {
                if (!FormatsEqual(existing.Value, snapshot))
                    errors.Add(new ValidationError(
                        path,
                        $"conflicting format declarations for column '{column}'"));
                continue;
            }
            into.Add(column, new FormatAssignment(snapshot, path));
        }
    }

    /// <summary>
    /// Copies a mutable column format into an immutable canonical value.
    /// </summary>
    /// <param name="value">The column format to copy.</param>
    /// <returns>The canonical column format.</returns>
    private static CanonicalColumnFormat SnapshotFormat(ColumnFormat value) => new(
        value.Mask,
        value.Align,
        value.Bold,
        value.Italic,
        value.Fg,
        value.Bg,
        SnapshotStrings(value.Classes),
        value.DisplayAs,
        value.UrlColumn,
        value.TextColumn,
        value.Command,
        value.KeyColumn);

    /// <summary>
    /// Copies a selection and interprets an absent or empty column list as “select all.”
    /// </summary>
    /// <param name="composable">The select composable to snapshot.</param>
    /// <param name="path">The composable's source path.</param>
    /// <returns>An immutable selection containing the copied columns and select-all flag.</returns>
    private static CanonicalSelection SnapshotSelection(
        TableComposable composable,
        string path)
    {
        var columns = SnapshotStrings(composable.Columns);
        return new CanonicalSelection(columns.Length == 0, columns, path);
    }

    /// <summary>
    /// Copies sort terms in authored order and reports null entries.
    /// </summary>
    /// <param name="composable">The sort composable to snapshot.</param>
    /// <param name="path">The composable's source path, used to identify sort terms.</param>
    /// <param name="errors">The validation list that receives null-sort errors.</param>
    /// <returns>An immutable ordering containing every non-null sort term.</returns>
    private static CanonicalOrdering SnapshotOrdering(
        TableComposable composable,
        string path,
        List<ValidationError> errors)
    {
        var result = ImmutableArray.CreateBuilder<CanonicalSort>();
        if (composable.Sorts is not null)
        {
            for (var index = 0; index < composable.Sorts.Count; index++)
            {
                var rule = composable.Sorts[index];
                if (rule is null)
                {
                    errors.Add(new ValidationError(
                        $"{path}.sorts[{index}]",
                        "sort cannot be null"));
                    continue;
                }
                result.Add(new CanonicalSort(
                    rule.Col,
                    rule.Dir,
                    rule.Nulls,
                    $"{path}.sorts[{index}]"));
            }
        }
        return new CanonicalOrdering(result.ToImmutable(), path);
    }

    /// <summary>
    /// Copies highlight rules, including disabled rules, into canonical records with their diagnostic paths.
    /// </summary>
    /// <param name="composable">The highlight composable whose rules will be copied.</param>
    /// <param name="composablePath">The composable's source path, used to identify each rule.</param>
    /// <param name="into">The list that receives non-null highlight rules.</param>
    /// <param name="errors">The validation list that receives null-rule errors.</param>
    private static void SnapshotHighlights(
        TableComposable composable,
        string composablePath,
        List<CanonicalHighlight> into,
        List<ValidationError> errors)
    {
        if (composable.Highlights is null) return;

        for (var index = 0; index < composable.Highlights.Count; index++)
        {
            var rule = composable.Highlights[index];
            var path = $"{composablePath}.highlights[{index}]";
            if (rule is null)
            {
                errors.Add(new ValidationError(path, "highlight cannot be null"));
                continue;
            }
            into.Add(new CanonicalHighlight(
                rule.Id,
                rule.Name,
                rule.Sequence,
                rule.Scope,
                rule.Col,
                rule.Expr ?? "",
                rule.Style is null
                    ? null
                    : new CanonicalHighlightStyle(rule.Style.Bg, rule.Style.Fg),
                path,
                rule.Enabled));
        }
    }

    /// <summary>
    /// Validates highlight identifiers and sequences, assigns stable sequence values where omitted, and orders
    /// all rules by scope, sequence, and identifier. It preserves duplicate rules so validation remains complete.
    /// </summary>
    /// <param name="values">The captured highlight declarations to validate and order.</param>
    /// <param name="errors">The validation list that receives duplicate identifier and sequence errors.</param>
    /// <returns>A deterministically ordered immutable array with every missing sequence filled in.</returns>
    private static ImmutableArray<CanonicalHighlight> NormalizeHighlights(
        IReadOnlyList<CanonicalHighlight> values,
        List<ValidationError> errors)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sequences = new HashSet<int>();
        foreach (var value in values)
        {
            if (!ids.Add(value.Id))
                errors.Add(new ValidationError(
                    $"{value.SourcePath}.id",
                    $"duplicate highlight id '{value.Id}'"));
            if (value.Sequence is { } sequence && !sequences.Add(sequence))
                errors.Add(new ValidationError(
                    $"{value.SourcePath}.sequence",
                    $"duplicate highlight sequence '{sequence}'"));
        }

        // Invariant: missing precedence cannot be inferred from array position. Allocate the
        // same 10-step sequence vocabulary used by authored rules, ordered by stable highlight
        // id and skipping every explicit value.
        var normalized = values.ToArray();
        var missing = normalized
            .Select((value, index) => (Value: value, Index: index))
            .Where(item => item.Value.Sequence is null)
            .OrderBy(item => item.Value.Id, StableNameComparer.Instance)
            .ThenBy(item => item.Value.SourcePath, StringComparer.Ordinal)
            .ToArray();
        var nextSequence = 10;
        foreach (var item in missing)
        {
            while (sequences.Contains(nextSequence)) nextSequence += 10;
            normalized[item.Index] = item.Value with { Sequence = nextSequence };
            sequences.Add(nextSequence);
            nextSequence += 10;
        }

        return normalized
            .OrderBy(value => HighlightScopeOrder(value.Scope))
            .ThenBy(value => value.Sequence ?? int.MaxValue)
            .ThenBy(value => value.Id, StableNameComparer.Instance)
            .ToImmutableArray();
    }

    /// <summary>
    /// Maps a highlight scope to its deterministic evaluation order.
    /// </summary>
    /// <param name="scope">The highlight scope name.</param>
    /// <returns><c>0</c> for row scope, <c>1</c> for cell scope, or <c>2</c> for any other value.</returns>
    private static int HighlightScopeOrder(string scope)
        => string.Equals(scope, "row", StringComparison.OrdinalIgnoreCase)
            ? 0
            : string.Equals(scope, "cell", StringComparison.OrdinalIgnoreCase)
                ? 1
                : 2;

    /// <summary>
    /// Copies the authored control-break column sequence into an immutable value.
    /// </summary>
    /// <param name="composable">The break composable to snapshot.</param>
    /// <param name="path">The composable's source path.</param>
    /// <returns>The copied break columns and their source path.</returns>
    private static CanonicalBreaks SnapshotBreaks(TableComposable composable, string path)
        => new(SnapshotStrings(composable.Breaks), path);

    /// <summary>
    /// Copies aggregate rules into canonical records while retaining source paths and reporting null entries.
    /// </summary>
    /// <param name="composable">The aggregate composable whose rules will be copied.</param>
    /// <param name="composablePath">The composable's source path, used to identify each rule.</param>
    /// <param name="into">The list that receives non-null aggregate rules.</param>
    /// <param name="errors">The validation list that receives null-rule errors.</param>
    private static void SnapshotAggregates(
        TableComposable composable,
        string composablePath,
        List<CanonicalAggregate> into,
        List<ValidationError> errors)
    {
        if (composable.Aggregates is null) return;

        for (var index = 0; index < composable.Aggregates.Count; index++)
        {
            var rule = composable.Aggregates[index];
            var path = $"{composablePath}.aggregates[{index}]";
            if (rule is null)
            {
                errors.Add(new ValidationError(path, "aggregate cannot be null"));
                continue;
            }
            into.Add(new CanonicalAggregate(rule.Col, rule.Fn, path));
        }
    }

    /// <summary>
    /// Deduplicates equivalent aggregate declarations and orders them deterministically.
    /// </summary>
    /// <param name="values">The captured aggregate declarations to deduplicate.</param>
    /// <returns>Unique column/function pairs ordered by stable column name and aggregate function.</returns>
    private static ImmutableArray<CanonicalAggregate> NormalizeAggregates(
        IReadOnlyList<CanonicalAggregate> values)
    {
        var unique = new Dictionary<string, CanonicalAggregate>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values.OrderBy(value => value.SourcePath, StringComparer.Ordinal))
            unique.TryAdd($"{value.Column}\0{value.Function}", value);

        return unique.Values
            .OrderBy(value => value.Column, StableNameComparer.Instance)
            .ThenBy(value => value.Function)
            .ToImmutableArray();
    }

    /// <summary>
    /// Selects one canonical declaration from a singleton operation family and reports conflicts.
    /// </summary>
    /// <typeparam name="T">The canonical declaration type.</typeparam>
    /// <param name="declarations">The declarations in one singleton composable family.</param>
    /// <param name="kind">The singleton composable family used in conflict diagnostics.</param>
    /// <param name="path">A selector that returns a declaration's source path.</param>
    /// <param name="equivalent">A predicate that determines whether two declarations are semantically equivalent.</param>
    /// <param name="errors">The validation list that receives one error for each non-equivalent declaration.</param>
    /// <returns>The declaration with the lexically first source path, or <see langword="null"/> when none was supplied.</returns>
    private static T? ResolveSingleton<T>(
        IReadOnlyList<T> declarations,
        ComposableKind kind,
        Func<T, string> path,
        Func<T, T, bool> equivalent,
        List<ValidationError> errors)
        where T : class
    {
        if (declarations.Count == 0) return null;

        var canonical = declarations
            .OrderBy(path, StringComparer.Ordinal)
            .First();
        foreach (var declaration in declarations)
        {
            if (equivalent(canonical, declaration)) continue;
            var name = ComposableSemanticsCatalog.Get(kind).DocumentKind;
            errors.Add(new ValidationError(
                path(declaration),
                $"conflicting '{name}' composables; '{name}' is a singleton table-local declaration"));
        }
        return canonical;
    }

    /// <summary>
    /// Determines whether two selection declarations are equivalent.
    /// </summary>
    /// <param name="left">The left value in the comparison.</param>
    /// <param name="right">The right value in the comparison.</param>
    /// <returns><see langword="true"/> when both declarations select the same columns; otherwise, <see langword="false"/>.</returns>
    private static bool SelectionEquals(CanonicalSelection left, CanonicalSelection right)
        => left.SelectAll == right.SelectAll
            && NamesEqual(left.Columns, right.Columns);

    /// <summary>
    /// Determines whether two ordering declarations are equivalent.
    /// </summary>
    /// <param name="left">The left value in the comparison.</param>
    /// <param name="right">The right value in the comparison.</param>
    /// <returns><see langword="true"/> when both declarations contain the same sort terms; otherwise, <see langword="false"/>.</returns>
    private static bool OrderingEquals(CanonicalOrdering left, CanonicalOrdering right)
    {
        if (left.Sorts.Length != right.Sorts.Length) return false;
        for (var index = 0; index < left.Sorts.Length; index++)
        {
            var a = left.Sorts[index];
            var b = right.Sorts[index];
            if (!string.Equals(a.Column, b.Column, StringComparison.OrdinalIgnoreCase)
                || a.Direction != b.Direction
                || a.Nulls != b.Nulls)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Determines whether two control-break declarations are equivalent.
    /// </summary>
    /// <param name="left">The left value in the comparison.</param>
    /// <param name="right">The right value in the comparison.</param>
    /// <returns><see langword="true"/> when both declarations have identical break settings; otherwise, <see langword="false"/>.</returns>
    private static bool BreaksEqual(CanonicalBreaks left, CanonicalBreaks right)
        => NamesEqual(left.Columns, right.Columns);

    /// <summary>
    /// Compares two immutable name sequences using ordinal, case-insensitive identity rules.
    /// </summary>
    /// <param name="left">The left value in the comparison.</param>
    /// <param name="right">The right value in the comparison.</param>
    /// <returns><see langword="true"/> when both sequences contain the same names in the same order; otherwise, <see langword="false"/>.</returns>
    private static bool NamesEqual(ImmutableArray<string> left, ImmutableArray<string> right)
    {
        if (left.Length != right.Length) return false;
        for (var index = 0; index < left.Length; index++)
            if (!string.Equals(left[index], right[index], StringComparison.OrdinalIgnoreCase))
                return false;
        return true;
    }

    /// <summary>
    /// Determines whether two canonical column formats contain the same explicit settings.
    /// </summary>
    /// <param name="left">The left value in the comparison.</param>
    /// <param name="right">The right value in the comparison.</param>
    /// <returns><see langword="true"/> when all format settings are equal; otherwise, <see langword="false"/>.</returns>
    private static bool FormatsEqual(CanonicalColumnFormat left, CanonicalColumnFormat right)
        => left.Mask == right.Mask
            && left.Align == right.Align
            && left.Bold == right.Bold
            && left.Italic == right.Italic
            && left.Foreground == right.Foreground
            && left.Background == right.Background
            && ValuesEqual(left.Classes, right.Classes)
            && left.DisplayAs == right.DisplayAs
            && left.UrlColumn == right.UrlColumn
            && left.TextColumn == right.TextColumn
            && left.Command == right.Command
            && left.KeyColumn == right.KeyColumn;

    /// <summary>
    /// Compares two immutable value sequences using ordinal equality.
    /// </summary>
    /// <param name="left">The left value in the comparison.</param>
    /// <param name="right">The right value in the comparison.</param>
    /// <returns><see langword="true"/> when both sequences contain the same values in the same order; otherwise, <see langword="false"/>.</returns>
    private static bool ValuesEqual(ImmutableArray<string> left, ImmutableArray<string> right)
    {
        if (left.Length != right.Length) return false;
        for (var index = 0; index < left.Length; index++)
            if (!string.Equals(left[index], right[index], StringComparison.Ordinal))
                return false;
        return true;
    }

    /// <summary>
    /// Copies non-null metric declarations into immutable records and reports null entries.
    /// </summary>
    /// <param name="values">The metric rules to copy; <see langword="null"/> means no metrics.</param>
    /// <param name="path">The metric collection's source path.</param>
    /// <param name="errors">The validation list that receives null-metric errors.</param>
    /// <returns>The non-null metrics in authored order, including duplicate declarations.</returns>
    private static ImmutableArray<CanonicalMetric> SnapshotMetrics(
        IReadOnlyList<MetricRule>? values,
        string path,
        List<ValidationError> errors)
    {
        if (values is null) return [];

        var result = ImmutableArray.CreateBuilder<CanonicalMetric>(values.Count);
        for (var index = 0; index < values.Count; index++)
        {
            var value = values[index];
            if (value is null)
            {
                errors.Add(new ValidationError($"{path}[{index}]", "metric cannot be null"));
                continue;
            }
            result.Add(new CanonicalMetric(
                value.Id,
                value.Col,
                value.Fn,
                $"{path}[{index}]"));
        }
        return result.ToImmutable();
    }

    /// <summary>
    /// Copies an optional string list into an immutable array without trimming, sorting, or deduplicating it.
    /// </summary>
    /// <param name="values">The optional string sequence to canonicalize.</param>
    /// <returns>The values in authored order, or an empty array when <paramref name="values"/> is <see langword="null"/>.</returns>
    private static ImmutableArray<string> SnapshotStrings(IReadOnlyList<string>? values)
        => values is null ? [] : [.. values];

    /// <summary>
    /// Builds the canonical operation order from phase rules and computed-column dependencies.
    /// </summary>
    /// <param name="shape">The optional terminal shape operation.</param>
    /// <param name="computed">Computed columns already ordered by dependency.</param>
    /// <param name="filters">Canonical filters already ordered for stable output.</param>
    /// <param name="labelPaths">Source paths for all label composables.</param>
    /// <param name="formatPaths">Source paths for all format composables.</param>
    /// <param name="local">The table-local selection, ordering, highlight, break, and aggregate operations.</param>
    /// <returns>Phase-annotated operation references in the order downstream compilers must apply them.</returns>
    private static ImmutableArray<CanonicalOperationRef> BuildNaturalOrder(
        CanonicalShape? shape,
        ImmutableArray<CanonicalComputedColumn> computed,
        ImmutableArray<CanonicalFilter> filters,
        IReadOnlyList<string> labelPaths,
        IReadOnlyList<string> formatPaths,
        CanonicalLocalResult local)
    {
        var result = ImmutableArray.CreateBuilder<CanonicalOperationRef>();

        if (shape is not null)
            Add(shape.Kind, shape.SourcePath);
        foreach (var value in computed)
            Add(ComposableKind.Compute, value.SourcePath);
        foreach (var value in filters)
            Add(ComposableKind.Filter, value.SourcePath);
        if (labelPaths.Count > 0)
            Add(ComposableKind.Labels, labelPaths.Min(StringComparer.Ordinal)!);
        if (formatPaths.Count > 0)
            Add(ComposableKind.Formats, formatPaths.Min(StringComparer.Ordinal)!);

        if (local.Selection is { } selection)
            Add(ComposableKind.Select, selection.SourcePath);
        if (local.Ordering is { } ordering)
            Add(ComposableKind.Sort, ordering.SourcePath);
        foreach (var highlight in local.Highlights.Where(value => value.Enabled))
            Add(ComposableKind.Highlight, highlight.SourcePath);
        if (local.Breaks is { } breaks)
            Add(ComposableKind.Break, breaks.SourcePath);
        foreach (var aggregate in local.Aggregates)
            Add(ComposableKind.Aggregate, aggregate.SourcePath);

        return result.ToImmutable();

        // Converts a composable kind and source path into the phase-annotated reference expected
        // by downstream planners, then appends it to the enclosing result builder.
        void Add(ComposableKind kind, string path)
        {
            var semantics = ComposableSemanticsCatalog.Get(kind);
            result.Add(new CanonicalOperationRef(kind, semantics.Phase, path));
        }
    }

    private sealed record ShapeDeclaration(
        ComposableKind Kind,
        TableComposable Value,
        string Path);

    private sealed record PendingComputed(
        string Id,
        string? Label,
        string Expression,
        string Path);

    private sealed record LabelAssignment(string Column, string Value, string Path);

    private sealed record FormatAssignment(CanonicalColumnFormat Value, string Path);

    private sealed class StableNameComparer : IComparer<string>
    {
        public static readonly StableNameComparer Instance = new();

        /// <summary>
        /// Compares logical names using the planner's stable, case-insensitive ordering.
        /// </summary>
        /// <param name="left">The left value in the comparison.</param>
        /// <param name="right">The right value in the comparison.</param>
        /// <returns>A signed value indicating the relative sort order.</returns>
        public int Compare(string? left, string? right)
        {
            var insensitive = StringComparer.OrdinalIgnoreCase.Compare(left, right);
            return insensitive != 0
                ? insensitive
                : StringComparer.Ordinal.Compare(left, right);
        }
    }
}
