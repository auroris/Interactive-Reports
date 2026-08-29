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
/// plus this discovered set is the entire model. Labels here are the server's neutral
/// derivation (prettified names) — friendly names are client-side presentation,
/// delivered through the default report, never applied to the engine's schema.
/// </summary>
public static class SchemaDiscovery
{
    public static Task<ReportSchema> Discover(
        DbConnection connection,
        ReportDefinition def,
        IReadOnlyDictionary<string, object?> contextParams,
        CancellationToken ct = default)
        => Discover(connection, def, contextParams, logger: null, ct);

    public static async Task<ReportSchema> Discover(
        DbConnection connection,
        ReportDefinition def,
        IReadOnlyDictionary<string, object?> contextParams,
        ILogger? logger,
        CancellationToken ct = default)
    {
        var probe = new Query()
            .FromRaw(SqlKataSyntax.PreserveRaw(
                $"({def.Sql}) {QueryComposer.BaseAlias}")) // no AS: Oracle table aliases
            .WhereRaw("1 = 0");

        var compiled = DialectSupport.GetCompiler(def.GetEffectiveDialect()).Compile(probe);

        await using var cmd = CommandBuilder.Build(connection, compiled, contextParams, def, logger);
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

        return ReportSchema.Create(def.Name, columns);
    }
}
