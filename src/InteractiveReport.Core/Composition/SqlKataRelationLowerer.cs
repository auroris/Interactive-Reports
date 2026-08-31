using System.Collections.Immutable;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Planning;
using InteractiveReport.Core.Schema;
using InteractiveReport.Core.Validation;
using SqlKata;

namespace InteractiveReport.Core.Composition;

/// <summary>
/// Implements the only SQLKata backend for immutable bound relation nodes. The current physical
/// alias allocator is deliberately retained; this visitor changes the logical
/// boundary, not the alias scheme.
/// </summary>
internal sealed class SqlKataRelationLowerer
{
    private readonly ReportDialect _dialect;
    private readonly DateTime _evaluationUtcNow;

    /// <summary>
    /// Initializes a lowering visitor for one dialect and one request-stable evaluation time.
    /// </summary>
    /// <param name="dialect">The database dialect whose SQL rules apply.</param>
    /// <param name="evaluationUtcNow">The fixed UTC timestamp used to evaluate time-sensitive expressions consistently throughout the request.</param>
    public SqlKataRelationLowerer(
        ReportDialect dialect,
        DateTime evaluationUtcNow)
    {
        _dialect = dialect;
        _evaluationUtcNow = evaluationUtcNow;
    }

    /// <summary>
    /// Lowers the bound plan into the query form used by provider-neutral SQL composition.
    /// </summary>
    /// <param name="node">The immutable logical relation root to visit recursively.</param>
    /// <returns>A fresh SQLKata query tree, logical output contract, and physical-column mapping.</returns>
    /// <exception cref="InvalidOperationException">Thrown for an unsupported node type or inconsistent physical output contract.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="node"/> is <see langword="null"/>.</exception>
    public LoweredRelation Lower(BoundRelationNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var lowered = node switch
        {
            BoundOpaqueSqlSource source => LowerSource(source),
            BoundExportReference reference => LowerExportReference(reference),
            BoundComputeRelation compute => LowerCompute(compute),
            BoundFilterRelation filter => LowerFilter(filter),
            BoundGroupRelation group => LowerGroup(group),
            BoundChartRelation chart => LowerChart(chart),
            BoundResolvedPivotRelation pivot => LowerPivot(pivot),
            BoundMetadataRelation metadata => LowerMetadata(metadata),
            BoundSearchRelation search => LowerSearch(search),
            _ => throw new InvalidOperationException(
                $"No SQLKata lowering visitor exists for bound relation node "
                + $"'{node.GetType().Name}'."),
        };
        EnsurePhysicalContract(lowered);
        return lowered;
    }

    /// <summary>
    /// Starts lowering from an opaque configured SQL source and its bound output contract.
    /// </summary>
    /// <param name="node">The bound source carrying SQL, dialect, definition name, and output.</param>
    /// <returns>A composable source query with allocated physical column names.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the bound source dialect differs from this lowerer's dialect.</exception>
    private LoweredRelation LowerSource(BoundOpaqueSqlSource node)
    {
        if (node.Dialect != _dialect)
            throw new InvalidOperationException(
                $"Bound source dialect '{node.Dialect}' does not match lowering "
                + $"dialect '{_dialect}'.");
        var definition = new ReportDefinition
        {
            Name = node.DefinitionName,
            Sql = node.Sql,
            Dialect = node.Dialect,
        };
        return FromComposable(
            ComposableSqlRelation.Definition(definition, node.Output.ToReportSchema()),
            node.Output);
    }

    /// <summary>
    /// Re-lowers an exported parent relation while substituting the reference's inherited output contract.
    /// </summary>
    /// <param name="node">The export reference and target relation.</param>
    /// <returns>A fresh physical query tree exposing the reference output.</returns>
    private LoweredRelation LowerExportReference(BoundExportReference node)
    {
        // Re-lowering the immutable target gives each child a fresh Query tree and
        // allocator. Sibling traversal order therefore cannot affect physical names.
        var target = Lower(node.Target.Relation);
        return target with
        {
            Output = node.Output,
            SchemaName = node.Output.Name,
        };
    }

    /// <summary>
    /// Lowers a computed-column relation by emitting its bound expression over the lowered input.
    /// </summary>
    /// <param name="node">The bound compute relation containing one output column.</param>
    /// <returns>The input query with the computed projection stage applied.</returns>
    private LoweredRelation LowerCompute(BoundComputeRelation node)
    {
        var input = Lower(node.Input);
        var column = node.Column.Output.ToColumnModel();
        var rule = new CompiledRule<DefineColumnEffect>(
            node.Column.Expression,
            new DefineColumnEffect(column));
        var relation = ComposableSqlPlanner.ApplyComputed(
            input.AsComposable(),
            rule,
            _dialect,
            _evaluationUtcNow);
        return FromComposable(relation, node.Output);
    }

    /// <summary>
    /// Lowers bound predicates into SQL WHERE stages over the lowered input.
    /// </summary>
    /// <param name="node">The bound filter relation containing zero or more predicates.</param>
    /// <returns>The filtered query with the node's output contract.</returns>
    private LoweredRelation LowerFilter(BoundFilterRelation node)
    {
        var input = Lower(node.Input);
        var rules = node.Predicates
            .Select(predicate => new CompiledRule<IncludeRowEffect>(
                predicate.Expression,
                new IncludeRowEffect()))
            .ToList();
        var relation = ComposableSqlPlanner.ApplyFilters(
            input.AsComposable(),
            rules,
            _dialect,
            _evaluationUtcNow);
        return FromComposable(relation, node.Output);
    }

    /// <summary>
    /// Lowers grouping dimensions and metrics into an aggregate relation.
    /// </summary>
    /// <param name="node">The bound group relation, including its stable count column id.</param>
    /// <returns>The grouped query with synthetic metric columns mapped physically.</returns>
    private LoweredRelation LowerGroup(BoundGroupRelation node)
    {
        var input = Lower(node.Input);
        var dimensions = node.Dimensions
            .Select(column => column.ToColumnModel())
            .ToList();
        var metrics = node.Metrics
            .Select(metric => new ValidMetric(
                metric.Id,
                metric.Input.ToColumnModel(),
                metric.Function))
            .ToList();
        var relation = ComposableSqlPlanner.Group(
            input.AsComposable(),
            node.Output.Name,
            dimensions,
            metrics,
            _dialect,
            node.CountColumn.LogicalId);
        return FromComposable(relation, node.Output);
    }

    /// <summary>
    /// Lowers a chart relation to its category-and-value tabular SQL shape.
    /// </summary>
    /// <param name="node">The bound chart relation and normalized chart specification.</param>
    /// <returns>The chart-shaped query with the node's output contract.</returns>
    private LoweredRelation LowerChart(BoundChartRelation node)
    {
        var input = Lower(node.Input);
        var relation = ComposableSqlPlanner.Chart(
            input.AsComposable(),
            node.Output.Name,
            node.Chart,
            _dialect);
        return FromComposable(relation, node.Output);
    }

    /// <summary>
    /// Lowers a resolved dynamic pivot using the discovery keys already bound into the plan.
    /// </summary>
    /// <param name="node">The resolved pivot containing row dimensions, column dimensions, metrics, and dynamic cells.</param>
    /// <returns>The wide pivot query with one physical projection per resolved cell.</returns>
    private LoweredRelation LowerPivot(BoundResolvedPivotRelation node)
    {
        var grouped = Lower(node.Discovery);
        var rows = node.RowDimensions
            .Select(column => column.ToColumnModel())
            .ToList();
        var columns = node.ColumnDimensions
            .Select(column => column.ToColumnModel())
            .ToList();
        var metrics = node.Metrics
            .Select(metric => new ValidMetric(
                metric.Id,
                metric.Input.ToColumnModel(),
                metric.Function))
            .ToList();
        var keys = node.Keys.Select(key => new PivotColumnKey(
            key.Key.SqlValues(),
            key.Cells.Select(cell => new PivotCellColumn(
                    cell.SourceLogicalId,
                    cell.Output.ToColumnModel()))
                .ToList()))
            .ToList();
        var relation = ComposableSqlPlanner.PivotWide(
            grouped.AsComposable(),
            node.Output.Name,
            rows,
            columns,
            metrics,
            keys,
            _dialect);
        return FromComposable(relation, node.Output);
    }

    /// <summary>
    /// Applies a metadata-only output contract without adding a SQL stage.
    /// </summary>
    /// <param name="node">The metadata relation containing labels and formats already reflected in its output.</param>
    /// <returns>The unchanged input query with the metadata output contract.</returns>
    private LoweredRelation LowerMetadata(BoundMetadataRelation node)
    {
        var input = Lower(node.Input);
        return input with
        {
            Output = node.Output,
            SchemaName = node.Output.Name,
        };
    }

    /// <summary>
    /// Applies the bound toolbar search predicate to eligible text columns.
    /// </summary>
    /// <param name="node">The search relation and normalized search text.</param>
    /// <returns>The searched query with the node's output contract.</returns>
    private LoweredRelation LowerSearch(BoundSearchRelation node)
    {
        var input = Lower(node.Input);
        var relation = ComposableSqlPlanner.ApplySearch(
            input.AsComposable(),
            node.Search);
        return FromComposable(relation, node.Output);
    }

    /// <summary>
    /// Converts a composable SQL relation back into a lowered relation with the supplied public contract.
    /// </summary>
    /// <param name="relation">The composable query and physical-name mapping.</param>
    /// <param name="output">The logical output contract to expose.</param>
    /// <returns>A lowered relation that preserves the planner's allocator, nesting depth, and physical map.</returns>
    private static LoweredRelation FromComposable(
        ComposableSqlRelation relation,
        BoundOutputContract output)
        => new(
            relation.Query,
            output,
            relation.PhysicalColumns.ToImmutableDictionary(
                StringComparer.OrdinalIgnoreCase),
            relation.Names,
            output.Name,
            relation.NestingDepth);

    /// <summary>
    /// Verifies that every logical output column has exactly one physical mapping.
    /// </summary>
    /// <param name="relation">The completed lowering result to verify.</param>
    /// <exception cref="InvalidOperationException">Thrown when mapping count differs from output width or a logical id is unmapped.</exception>
    private static void EnsurePhysicalContract(LoweredRelation relation)
    {
        if (relation.PhysicalColumns.Count != relation.Output.Count)
            throw new InvalidOperationException(
                $"Lowered relation '{relation.SchemaName}' has "
                + $"{relation.PhysicalColumns.Count} physical columns for "
                + $"{relation.Output.Count} logical columns.");
        foreach (var column in relation.Output.Columns)
            if (!relation.PhysicalColumns.ContainsKey(column.LogicalId))
                throw new InvalidOperationException(
                    $"Lowered relation '{relation.SchemaName}' has no physical mapping "
                    + $"for logical column '{column.LogicalId}'.");
    }
}

/// <summary>
/// Contains the SQLKata result of visiting one bound relation. Public order and presentation remain
/// in Output; PhysicalColumns is only a lowering concern.
/// </summary>
internal sealed record LoweredRelation(
    Query Query,
    BoundOutputContract Output,
    ImmutableDictionary<string, string> PhysicalColumns,
    SqlPhysicalNameAllocator Names,
    string SchemaName,
    int StageCount)
{
    /// <summary>Gets the public report schema projected from the immutable logical output contract.</summary>
    public ReportSchema Schema => Output.ToReportSchema();

    /// <summary>
    /// Wraps a lowered relation as a composable SQL relation without changing its contract.
    /// </summary>
    /// <returns>A composable wrapper sharing this query, allocator, physical map, and stage count.</returns>
    internal ComposableSqlRelation AsComposable()
        => new(
            Query,
            Schema,
            PhysicalColumns,
            Names,
            SchemaName,
            StageCount);
}
