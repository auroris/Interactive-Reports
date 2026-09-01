using InteractiveReport.Core.Model;
using InteractiveReport.Core.Validation;

namespace InteractiveReport.AspNetCore;

/// <summary>Builds the detached synthetic report document for a configured definition.</summary>
public static class ReportDocumentDefaults
{
    public static ReportState Create(ReportDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var state = ReportStateResolver.Resolve(definition.DefaultState, new ReportState());
        if (state.Tables is not { Count: > 0 })
        {
            state.ActiveTable = "base";
            state.Tables = new(StringComparer.OrdinalIgnoreCase)
            {
                ["base"] = new ReportTable { From = "definition" },
            };
        }

        var source = DefinitionInputTable(state);
        if (source is not null && definition.GetEffectiveColumnLabels() is { } definitionLabels)
        {
            source.Composables ??= [];
            var shapeIndex = source.Composables.FindIndex(IsShapeComposable);
            var inputCount = shapeIndex < 0 ? source.Composables.Count : shapeIndex;
            var labels = source.Composables
                .Take(inputCount)
                .FirstOrDefault(composable => IsComposableKind(composable, "labels"));
            if (labels is null)
            {
                labels = new TableComposable { Kind = "labels" };
                source.Composables.Insert(inputCount, labels);
            }
            labels.Labels ??= new(definitionLabels);
        }
        return state;
    }

    private static bool IsShapeComposable(TableComposable composable)
        => IsComposableKind(composable, "group")
            || IsComposableKind(composable, "pivot")
            || IsComposableKind(composable, "chart");

    private static bool IsComposableKind(TableComposable composable, string kind)
        => string.Equals(composable.Kind?.Trim(), kind, StringComparison.OrdinalIgnoreCase);

    private static ReportTable? DefinitionInputTable(ReportState state)
    {
        if (state.Tables is not { Count: > 0 } tables
            || string.IsNullOrWhiteSpace(state.ActiveTable))
            return null;

        var lookup = new Dictionary<string, ReportTable>(tables, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = state.ActiveTable;
        while (!string.Equals(current, "definition", StringComparison.OrdinalIgnoreCase))
        {
            if (!seen.Add(current) || !lookup.TryGetValue(current, out var table)) return null;
            if (string.Equals(table.From, "definition", StringComparison.OrdinalIgnoreCase)) return table;
            if (string.IsNullOrWhiteSpace(table.From)) return null;
            current = table.From;
        }
        return null;
    }
}
