using System.Text.Json;
using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Validation;

/// <summary>Validates aggregate compatibility and removes duplicate column/function pairs.</summary>
internal static class AggregateRuleValidator
{
    internal sealed class Context
    {
        public HashSet<string> SeenKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

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
