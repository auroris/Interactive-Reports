using System.Text;

namespace InteractiveReport.Core.Planning;

/// <summary>Renders bound relation trees as deterministic diagnostics for tests and troubleshooting.</summary>
internal static class BoundRelationPlanDebug
{
    /// <summary>
    /// Renders a bound relation tree as a deterministic, indented diagnostic snapshot.
    /// </summary>
    /// <param name="relation">The root relation node to render.</param>
    /// <returns>The deterministic multiline representation of the bound relation tree.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="relation"/> is <see langword="null"/>.</exception>
    public static string Render(BoundRelationNode relation)
    {
        ArgumentNullException.ThrowIfNull(relation);
        var result = new StringBuilder();
        Append(result, relation, 0);
        return result.ToString().TrimEnd();
    }

    /// <summary>
    /// Appends one relation node, its output columns, and its input subtree to the diagnostic buffer.
    /// </summary>
    /// <param name="target">The buffer receiving the rendered plan.</param>
    /// <param name="node">The relation node to render.</param>
    /// <param name="depth">The node depth used to indent the diagnostic output.</param>
    private static void Append(StringBuilder target, BoundRelationNode node, int depth)
    {
        target.Append(' ', depth * 2)
            .Append(NodeName(node))
            .Append(" path=")
            .Append(node.SourcePath)
            .Append(" output=")
            .Append(node.Output.Name)
            .Append('\n');

        foreach (var column in node.Output.Columns)
        {
            target.Append(' ', (depth + 1) * 2)
                .Append(column.LogicalId)
                .Append(':')
                .Append(column.Kind.ToString().ToLowerInvariant())
                .Append(" label=")
                .Append(column.EffectiveLabel)
                .Append(" lineage=")
                .Append(Lineage(column.Lineage))
                .Append(" mask=")
                .Append(column.ExportedMask ?? "-")
                .Append(" formatSource=")
                .Append(column.FormatSourceLogicalId ?? "-")
                .Append('\n');
        }

        switch (node)
        {
            case BoundExportReference reference:
                Append(target, reference.Target.Relation, depth + 1);
                break;
            case BoundComputeRelation compute:
                Append(target, compute.Input, depth + 1);
                break;
            case BoundFilterRelation filter:
                Append(target, filter.Input, depth + 1);
                break;
            case BoundGroupRelation group:
                Append(target, group.Input, depth + 1);
                break;
            case BoundChartRelation chart:
                Append(target, chart.Input, depth + 1);
                break;
            case BoundResolvedPivotRelation pivot:
                Append(target, pivot.Discovery, depth + 1);
                break;
            case BoundMetadataRelation metadata:
                Append(target, metadata.Input, depth + 1);
                break;
            case BoundSearchRelation search:
                Append(target, search.Input, depth + 1);
                break;
        }
    }

    /// <summary>
    /// Maps a relation-node type to its concise diagnostic name.
    /// </summary>
    /// <param name="node">The relation node to identify.</param>
    /// <returns>The diagnostic name for the relation-node kind.</returns>
    private static string NodeName(BoundRelationNode node)
        => node switch
        {
            BoundOpaqueSqlSource => "source",
            BoundExportReference reference => $"export-ref({reference.TableId})",
            BoundComputeRelation compute => $"compute({compute.Column.Output.LogicalId})",
            BoundFilterRelation filter => $"filter({filter.Predicates.Length})",
            BoundGroupRelation group => $"group({group.Dimensions.Length},{group.Metrics.Length})",
            BoundChartRelation => "chart",
            BoundResolvedPivotRelation pivot => $"pivot({pivot.Keys.Length})",
            BoundMetadataRelation => "metadata",
            BoundSearchRelation => "search",
            _ => node.GetType().Name,
        };

    /// <summary>
    /// Formats column-lineage metadata for deterministic plan diagnostics.
    /// </summary>
    /// <param name="lineage">The lineage metadata to format.</param>
    /// <returns>The deterministic diagnostic representation of the column lineage.</returns>
    private static string Lineage(BoundColumnLineage lineage)
        => lineage switch
        {
            BoundSourceColumnLineage source => $"source:{source.SourceLogicalId}",
            BoundPassThroughColumnLineage pass => $"pass:{pass.InputLogicalId}",
            BoundComputedColumnLineage computed =>
                $"compute:{string.Join(',', computed.InputLogicalIds)}",
            BoundAggregateColumnLineage aggregate =>
                $"aggregate:{aggregate.Function}:{aggregate.InputLogicalId ?? "*"}",
            BoundChartColumnLineage chart =>
                $"chart:{chart.Role}:{chart.InputLogicalId ?? "*"}:{chart.Function?.ToString() ?? "none"}",
            BoundPivotCellColumnLineage pivot =>
                $"pivot:{pivot.OwnerTableId}:{pivot.MetricId}:{pivot.Key.CanonicalIdentity}",
            _ => lineage.GetType().Name,
        };
}
