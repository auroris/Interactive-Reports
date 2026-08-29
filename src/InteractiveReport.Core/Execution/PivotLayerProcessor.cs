using InteractiveReport.Core.Model;
using InteractiveReport.Core.Schema;
using InteractiveReport.Core.Validation;

namespace InteractiveReport.Core.Execution;

/// <summary>
/// Binds and executes a Pivot stage's layer after the data-dependent wide schema is
/// known. Shape runs in SQL as a portable grouped query; the wide table then behaves
/// like any other report table for compute, filter, sort, highlight, projection, and
/// paging.
/// </summary>
internal static class PivotLayerProcessor
{
    public static ProcessedPivot Apply(
        PivotTable pivot,
        ValidatedState state,
        ReportDialect dialect,
        bool unpaged = false,
        int maxRows = 0)
    {
        var errors = new List<ValidationError>();
        var ignored = new List<IgnoredItem>();
        var schema = RuntimeSchema(pivot.Columns, state);
        var layer = TableLayerValidator.Validate(
            state.View.DeferredOutput ?? [],
            $"{state.Schema.Columns[0].Name}#pivot",
            schema,
            state.Policy,
            errors,
            ignored);

        if (errors.Count > 0)
            throw new ReportValidationException(errors);

        var processed = MaterializedTableProcessor.Apply(
            pivot.Columns,
            pivot.Rows,
            layer,
            state,
            dialect,
            pivot.Totals,
            unpaged,
            maxRows);

        return new ProcessedPivot(
            layer,
            processed.Columns,
            processed.AvailableColumns,
            processed.Rows,
            processed.Totals,
            processed.BreakTotals,
            processed.BreakContinues,
            processed.Highlights,
            processed.TotalRows,
            processed.Truncated,
            state.Ignored.Concat(ignored).ToList());
    }

    private static ReportSchema RuntimeSchema(
        IReadOnlyList<ColumnInfo> columns,
        ValidatedState state)
    {
        var rowDimensions = state.View.PivotRows.ToDictionary(
            column => column.Name,
            StringComparer.OrdinalIgnoreCase);
        return ReportSchema.Create(
            "pivot",
            columns.Select(column => rowDimensions.TryGetValue(column.Name, out var dimension)
                ? new ColumnModel
                {
                    Name = dimension.Name,
                    Label = column.Label,
                    ClrType = dimension.ClrType,
                    IsNullable = dimension.IsNullable,
                    IsComputed = dimension.IsComputed,
                }
                : new ColumnModel
                {
                    Name = column.Name,
                    Label = column.Label,
                    ClrType = column.Type switch
                    {
                        "number" => typeof(decimal),
                        "date" => typeof(DateTime),
                        "bool" => typeof(bool),
                        "text" => typeof(string),
                        _ => typeof(object),
                    },
                    IsComputed = column.Computed,
                }));
    }

}

internal sealed record ProcessedPivot(
    ValidTableLayer Layer,
    IReadOnlyList<ColumnInfo> Columns,
    IReadOnlyList<ColumnInfo> AvailableColumns,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> Totals,
    IReadOnlyList<BreakTotal> BreakTotals,
    bool BreakContinues,
    IReadOnlyList<HighlightHit> Highlights,
    long TotalRows,
    bool Truncated,
    IReadOnlyList<IgnoredItem> Ignored);
