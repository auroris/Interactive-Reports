using System.Collections.Immutable;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Planning;
using InteractiveReport.Core.Schema;
using InteractiveReport.Core.Validation;
using SqlKata;

namespace InteractiveReport.Core.Composition;

/// <summary>
/// The only SQLKata backend for immutable bound relation nodes. The current physical
/// alias allocator is deliberately retained; this visitor changes the logical
/// boundary, not the alias scheme.
/// </summary>
internal sealed class SqlKataRelationLowerer
{
    private readonly ReportDialect _dialect;
    private readonly DateTime _evaluationUtcNow;

    public SqlKataRelationLowerer(
        ReportDialect dialect,
        DateTime evaluationUtcNow)
    {
        _dialect = dialect;
        _evaluationUtcNow = evaluationUtcNow;
    }

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

    private LoweredRelation LowerMetadata(BoundMetadataRelation node)
    {
        var input = Lower(node.Input);
        return input with
        {
            Output = node.Output,
            SchemaName = node.Output.Name,
        };
    }

    private LoweredRelation LowerSearch(BoundSearchRelation node)
    {
        var input = Lower(node.Input);
        var relation = ComposableSqlPlanner.ApplySearch(
            input.AsComposable(),
            node.Search);
        return FromComposable(relation, node.Output);
    }

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
/// SQLKata result of visiting one bound relation. Public order and presentation remain
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
    public ReportSchema Schema => Output.ToReportSchema();

    internal ComposableSqlRelation AsComposable()
        => new(
            Query,
            Schema,
            PhysicalColumns,
            Names,
            SchemaName,
            StageCount);
}
