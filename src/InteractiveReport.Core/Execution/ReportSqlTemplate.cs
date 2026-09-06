using System.Text;
using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Execution;

/// <summary>Resolves the one server-owned SQL expression slot without parsing or rewriting SQL clauses.</summary>
internal static class ReportSqlTemplate
{
    internal const string Marker = "{{RowRestriction}}";

    internal static bool RequiresRowRestriction(string sql)
        => sql.Contains("{{", StringComparison.Ordinal) && MarkerPositions(sql).Count != 0;

    internal static void RequireResolved(ReportDefinition definition)
    {
        if (RequiresRowRestriction(definition.Sql))
            throw new InvalidOperationException(
                $"Report '{definition.Name}': {Marker} must be resolved before executing report SQL.");
    }

    /// <summary>Produces a detached definition and bindings; neither input is mutated.</summary>
    internal static (ReportDefinition Definition, IReadOnlyDictionary<string, object?> Parameters) Bind(
        ReportDefinition definition,
        string expression,
        IReadOnlyList<object?> values,
        IReadOnlyDictionary<string, object?> contextParameters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        var positions = MarkerPositions(definition.Sql);
        if (positions.Count == 0)
            throw new InvalidOperationException($"Report '{definition.Name}': SQL has no {Marker} expression slot.");
        if (RequiresRowRestriction(expression))
            throw new InvalidOperationException("A row restriction cannot contain another row-restriction slot.");

        var parameters = new Dictionary<string, object?>(contextParameters, StringComparer.OrdinalIgnoreCase);
        var predicate = new StringBuilder();
        var valueIndex = 0;
        var parameterIndex = 0;
        var prefix = definition.GetEffectiveDialect() is ReportDialect.Oracle or ReportDialect.Oracle11g ? ":" : "@";
        var previous = 0;
        foreach (var (start, length) in SqlCodeScanner.CodeSpans(expression))
        {
            predicate.Append(expression, previous, start - previous);
            var end = start + length;
            for (var index = start; index < end; index++)
            {
                if (expression[index] != '?')
                {
                    predicate.Append(expression[index]);
                    continue;
                }
                // PostgreSQL operators may contain a literal question mark. ?? escapes one
                // outside quotes; quoted text and comments are copied without interpretation.
                if (index + 1 < end && expression[index + 1] == '?')
                {
                    predicate.Append('?');
                    index++;
                    continue;
                }
                if (valueIndex >= values.Count)
                    throw new InvalidOperationException("A row restriction has more placeholders than parameter values.");
                string name;
                do name = $"__ir_row_{parameterIndex++}";
                while (parameters.ContainsKey(name)
                    || definition.Sql.Contains(name, StringComparison.OrdinalIgnoreCase)
                    || expression.Contains(name, StringComparison.OrdinalIgnoreCase));
                parameters.Add(name, values[valueIndex++]);
                predicate.Append(prefix).Append(name);
            }
            previous = end;
        }
        predicate.Append(expression, previous, expression.Length - previous);
        if (valueIndex != values.Count)
            throw new InvalidOperationException("A row restriction has more parameter values than placeholders.");
        if (parameters.Count > CommandBuilder.MaxParameters)
            throw new InvalidOperationException($"Row restriction and context parameters exceed {CommandBuilder.MaxParameters} bindings.");

        // Newlines keep a trailing line comment in an application expression from consuming
        // the closing parenthesis or the next clause in the configured SQL.
        var replacement = "(\n" + predicate + "\n)";
        var sql = new StringBuilder();
        previous = 0;
        foreach (var position in positions)
        {
            sql.Append(definition.Sql, previous, position - previous).Append(replacement);
            previous = position + Marker.Length;
        }
        sql.Append(definition.Sql, previous, definition.Sql.Length - previous);
        return (definition.WithRowRestrictionSql(sql.ToString()), parameters);
    }

    private static List<int> MarkerPositions(string sql)
    {
        var positions = new List<int>();
        foreach (var (start, length) in SqlCodeScanner.CodeSpans(sql))
        {
            var end = start + length;
            for (var index = start; index + 1 < end; index++)
            {
                if (sql[index] != '{' || sql[index + 1] != '{') continue;
                if (index + Marker.Length > end
                    || !sql.AsSpan(index, Marker.Length).SequenceEqual(Marker))
                    throw new InvalidOperationException($"Unknown SQL template expression; the supported marker is {Marker} (case-sensitive).");
                positions.Add(index);
                index += Marker.Length - 1;
            }
        }
        return positions;
    }

}
