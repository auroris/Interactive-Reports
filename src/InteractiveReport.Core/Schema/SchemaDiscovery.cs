using System.Data;
using System.Data.Common;
using InteractiveReport.Core.Composition;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;
using Microsoft.Extensions.Logging;
using SqlKata;

namespace InteractiveReport.Core.Schema;

/// <summary>
/// Replaces APEX's data-dictionary knowledge: run the wrapped base query with a
/// WHERE 1=0 probe and read the result schema off the reader. The developer's SELECT
/// plus this discovered set is the entire model.
/// </summary>
public static class SchemaDiscovery
{
    public static async Task<ReportSchema> Discover(
        DbConnection connection,
        ReportDefinition def,
        IReadOnlyDictionary<string, object?> contextParams,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        var probe = new Query()
            .FromRaw($"({def.Sql}) {QueryComposer.BaseAlias}") // no AS: Oracle table aliases
            .WhereRaw("1 = 0");

        var compiled = DialectSupport.GetCompiler(def.Dialect).Compile(probe);

        await using var cmd = CommandBuilder.Build(connection, compiled, contextParams, def);
        await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SchemaOnly, ct);

        var friendly = FriendlyLabels(def);
        var columns = new List<ColumnModel>();
        foreach (var col in reader.GetColumnSchema())
        {
            var name = col.ColumnName;
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException(
                    $"Report '{def.Name}': base query returns an unnamed column (position {col.ColumnOrdinal}). Alias every expression.");

            columns.Add(new ColumnModel
            {
                Name = name,
                Label = friendly?.TryGetValue(name, out var label) == true ? label : ColumnModel.Prettify(name),
                ClrType = col.DataType ?? typeof(object),
                IsNullable = col.AllowDBNull ?? true,
            });
        }

        // A columnLabels entry naming no discovered column is inert, not fatal — the
        // mapping must survive schema drift — but it is worth one log line per discovery.
        if (friendly is not null && logger is not null)
        {
            foreach (var key in friendly.Keys)
            {
                if (!columns.Any(c => string.Equals(c.Name, key, StringComparison.OrdinalIgnoreCase)))
                    logger.LogWarning(
                        "Report {Report}: columnLabels entry '{Column}' matches no discovered column",
                        def.Name, key);
            }
        }

        return ReportSchema.Create(def.Name, columns);
    }

    private static Dictionary<string, string>? FriendlyLabels(ReportDefinition def)
    {
        if (def.ColumnLabels is not { Count: > 0 } configured) return null;

        // Last-wins on case collisions; the config store rejects those up front.
        var lookup = new Dictionary<string, string>(configured.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, label) in configured)
        {
            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(label))
                lookup[name] = label.Trim();
        }
        return lookup.Count > 0 ? lookup : null;
    }
}
