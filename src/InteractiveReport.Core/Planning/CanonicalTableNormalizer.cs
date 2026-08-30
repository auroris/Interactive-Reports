using System.Collections.Immutable;
using InteractiveReport.Core.Expressions;
using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Planning;

/// <summary>
/// Converts the mutable document syntax for one table into a deep, immutable
/// canonical specification. Array position supplies diagnostics only; semantic
/// phase and dependency edges determine execution order.
/// </summary>
internal static class CanonicalTableNormalizer
{
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

            // Keep the object complete for diagnostics. Normalize never returns it
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

    private static CanonicalSelection SnapshotSelection(
        TableComposable composable,
        string path)
    {
        var columns = SnapshotStrings(composable.Columns);
        return new CanonicalSelection(columns.Length == 0, columns, path);
    }

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
            if (!rule.Enabled) continue;

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
                path));
        }
    }

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

        // Missing precedence cannot be inferred from array position. Allocate the
        // same 10-step sequence vocabulary used by authored rules, ordered by stable
        // highlight id and skipping every explicit value.
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
            .OrderBy(value => value.Sequence ?? int.MaxValue)
            .ThenBy(value => value.Id, StableNameComparer.Instance)
            .ToImmutableArray();
    }

    private static CanonicalBreaks SnapshotBreaks(TableComposable composable, string path)
        => new(SnapshotStrings(composable.Breaks), path);

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

    private static bool SelectionEquals(CanonicalSelection left, CanonicalSelection right)
        => left.SelectAll == right.SelectAll
            && NamesEqual(left.Columns, right.Columns);

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

    private static bool BreaksEqual(CanonicalBreaks left, CanonicalBreaks right)
        => NamesEqual(left.Columns, right.Columns);

    private static bool NamesEqual(ImmutableArray<string> left, ImmutableArray<string> right)
    {
        if (left.Length != right.Length) return false;
        for (var index = 0; index < left.Length; index++)
            if (!string.Equals(left[index], right[index], StringComparison.OrdinalIgnoreCase))
                return false;
        return true;
    }

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

    private static bool ValuesEqual(ImmutableArray<string> left, ImmutableArray<string> right)
    {
        if (left.Length != right.Length) return false;
        for (var index = 0; index < left.Length; index++)
            if (!string.Equals(left[index], right[index], StringComparison.Ordinal))
                return false;
        return true;
    }

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

    private static ImmutableArray<string> SnapshotStrings(IReadOnlyList<string>? values)
        => values is null ? [] : [.. values];

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
        foreach (var highlight in local.Highlights)
            Add(ComposableKind.Highlight, highlight.SourcePath);
        if (local.Breaks is { } breaks)
            Add(ComposableKind.Break, breaks.SourcePath);
        foreach (var aggregate in local.Aggregates)
            Add(ComposableKind.Aggregate, aggregate.SourcePath);

        return result.ToImmutable();

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

        public int Compare(string? left, string? right)
        {
            var insensitive = StringComparer.OrdinalIgnoreCase.Compare(left, right);
            return insensitive != 0
                ? insensitive
                : StringComparer.Ordinal.Compare(left, right);
        }
    }
}
