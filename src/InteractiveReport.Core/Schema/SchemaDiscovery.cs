using System.Data;
using System.Data.Common;
using InteractiveReport.Core.Composition;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;
using SqlKata;

namespace InteractiveReport.Core.Schema;

/// <summary>
/// Replaces APEX's data-dictionary knowledge: run the wrapped base query with a
/// WHERE 1=0 probe and read the result schema off the reader. The developer's SELECT
/// plus this discovered set is the entire model.
/// </summary>
public static class SchemaDiscovery
{
    public static async Task<IReadOnlyList<ColumnModel>> Discover(
        DbConnection connection,
        ReportDefinition def,
        IReadOnlyDictionary<string, object?> contextParams,
        CancellationToken ct = default)
    {
        var probe = new Query()
            .FromRaw($"({def.Sql}) {QueryComposer.BaseAlias}") // no AS: Oracle table aliases
            .WhereRaw("1 = 0");

        var compiled = DialectSupport.GetCompiler(def.Dialect).Compile(probe);

        await using var cmd = CommandBuilder.Build(connection, compiled, contextParams, def);
        await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SchemaOnly, ct);

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
                Label = ColumnModel.Prettify(name),
                ClrType = col.DataType ?? typeof(object),
                IsNullable = col.AllowDBNull ?? true,
            });
        }

        if (columns.Count == 0)
            throw new InvalidOperationException($"Report '{def.Name}': base query returned no columns.");

        return columns;
    }
}
