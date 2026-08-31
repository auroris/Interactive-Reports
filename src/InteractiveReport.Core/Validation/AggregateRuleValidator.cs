using System.Text.Json;
using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Validation;

/// <summary>Validates aggregate compatibility and removes duplicate column/function pairs.</summary>
internal static class AggregateRuleValidator
{
    /// <summary>Tracks aggregate identities already emitted for the table currently being bound.</summary>
    internal sealed class Context
    {
        /// <summary>Gets the case-insensitive set of column/function keys already accepted.</summary>
        public HashSet<string> SeenKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves one aggregate rule against the current columns, validates type compatibility, and suppresses duplicates.
    /// </summary>
    /// <param name="columnName">The logical or physical column name to resolve.</param>
    /// <param name="function">The aggregate function requested for the column.</param>
    /// <param name="rulePath">The aggregate rule path to attach to compatibility errors.</param>
    /// <param name="columns">The columns available at this relation stage.</param>
    /// <param name="errors">The validation list that receives aggregate/type incompatibilities.</param>
    /// <param name="ignored">The diagnostics list that receives unknown-column rules.</param>
    /// <param name="context">The table-local aggregate identity set.</param>
    /// <returns>The resolved aggregate, or <see langword="null"/> when the column is unknown, the function is invalid, or the pair is a duplicate.</returns>
    /// <remarks>Adds unknown-column diagnostics to <paramref name="ignored"/> and incompatibility errors to <paramref name="errors"/>.</remarks>
    internal static ValidAggregate? Bind(
        string columnName,
        AggregateFn function,
        string rulePath,
        IReadOnlyDictionary<string, ColumnModel> columns,
        List<ValidationError> errors,
        List<IgnoredItem> ignored,
        Context context)
    {
        if (!columns.TryGetValue(columnName, out var column))
        {
            ignored.Add(new IgnoredItem(
                "aggregate",
                $"unknown column '{columnName}'"));
            return null;
        }

        if (!AggregateCatalog.IsCompatible(column.Kind, function))
        {
            var functionName = JsonNamingPolicy.CamelCase.ConvertName(function.ToString());
            errors.Add(new ValidationError(
                rulePath,
                $"aggregate '{functionName}' is not valid for {column.KindName} column '{column.Name}'"));
            return null;
        }

        return context.SeenKeys.Add($"{column.Name}\0{function}")
            ? new ValidAggregate(column, function)
            : null;
    }
}
