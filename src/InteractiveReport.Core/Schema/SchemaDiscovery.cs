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
                $"({def.Sql}) {SqlKataSyntax.BaseRelationAlias}")) // no AS: Oracle table aliases
            .WhereRaw("1 = 0");

        var compiled = DialectSupport.GetCompiler(def.GetEffectiveDialect()).Compile(probe);

        await using var cmd = CommandBuilder.Build(connection, compiled, contextParams, def, logger);
        await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SchemaOnly, ct);

        var columns = new List<ColumnModel>();
        var dialect = def.GetEffectiveDialect();
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
                // Microsoft.Data.Sqlite reports every source expression as BLOB /
                // byte[] during a zero-row probe. Only an origin column carries a
                // meaningful type there; treating the expression as a known BLOB
                // would make ordinary text/number literals impossible to filter.
                HasKnownType = col.DataType is not null
                    && (dialect != ReportDialect.Sqlite
                        || !string.IsNullOrWhiteSpace(col.BaseColumnName)),
                IsNullable = col.AllowDBNull ?? true,
            });
        }

        return ReportSchema.Create(def.Name, columns);
    }
}
