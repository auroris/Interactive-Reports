using System.Data;
using System.Data.Common;
using InteractiveReport.Core.Composition;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;
using Microsoft.Extensions.Logging;
using SqlKata;

namespace InteractiveReport.Core.Schema;

/// <summary>
/// Replaces external data-dictionary knowledge by running the wrapped base query with a
/// WHERE 1=0 probe and read the result schema off the reader. The developer's SELECT
/// plus this discovered set is the entire model. Labels here are the server's neutral
/// derivation (prettified names) — friendly names are client-side presentation,
/// delivered through the default report, never applied to the engine's schema.
/// </summary>
public static class SchemaDiscovery
{
    /// <summary>
    /// Probes a report's base query and returns its ordered result schema without logging SQL.
    /// </summary>
    /// <param name="connection">The open report connection on which to run the zero-row probe.</param>
    /// <param name="def">The resolved report definition containing the base SQL and dialect.</param>
    /// <param name="contextParams">Trusted values for context parameters referenced by the base SQL.</param>
    /// <param name="ct">Signals that the operation should be canceled; defaults to <c>default</c>.</param>
    /// <returns>A task containing the validated schema in provider column order.</returns>
    public static Task<ReportSchema> Discover(
        DbConnection connection,
        ReportDefinition def,
        IReadOnlyDictionary<string, object?> contextParams,
        CancellationToken ct = default)
        => Discover(connection, def, contextParams, logger: null, ct);

    /// <summary>
    /// Probes a report's base query and returns its ordered result schema.
    /// </summary>
    /// <param name="connection">The open report connection on which to run the zero-row probe.</param>
    /// <param name="def">The resolved report definition containing the base SQL and dialect.</param>
    /// <param name="contextParams">Trusted values for context parameters referenced by the base SQL.</param>
    /// <param name="logger">The host-provided logger that receives diagnostic events; <see langword="null"/> disables logging.</param>
    /// <param name="ct">Signals that the operation should be canceled; defaults to <c>default</c>.</param>
    /// <returns>A task containing the validated schema in provider column order.</returns>
    /// <remarks>Executes the base query with a false predicate and schema-only reader behavior.</remarks>
    /// <exception cref="InvalidOperationException">Thrown when the probe returns an unnamed, empty, or duplicate schema.</exception>
    public static async Task<ReportSchema> Discover(
        DbConnection connection,
        ReportDefinition def,
        IReadOnlyDictionary<string, object?> contextParams,
        ILogger? logger,
        CancellationToken ct = default)
    {
        var probe = new Query()
            .FromRaw(SqlKataSyntax.PreserveRaw(
                $"({def.Sql}) {SqlKataSyntax.BaseRelationAlias}")) // Provider constraint: no AS: Oracle table aliases.
            .WhereRaw("1 = 0");

        var compiled = DialectSupport.GetCompiler(def.GetEffectiveDialect()).Compile(probe);

        await using var cmd = CommandBuilder.Build(connection, compiled, contextParams, def, logger);
        DbDataReader reader;
        try
        {
            reader = await cmd.ExecuteReaderAsync(CommandBehavior.SchemaOnly, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var diagnosis = DbErrorClassifier.Classify(def.GetEffectiveDialect(), ex);
            logger?.LogError(
                ex,
                "Schema discovery probe failed for report '{Report}' on connection '{Connection}' (Dialect: {Dialect}, Category: {Category}, Code: {ProviderCode}): {Summary}. Hint: {Hint}",
                def.Name,
                def.Connection,
                def.GetEffectiveDialect(),
                diagnosis.Category,
                diagnosis.ProviderCode ?? "none",
                diagnosis.Summary,
                diagnosis.RemediationHint ?? "Check report base SQL syntax, table existence, and database user permissions.");
            throw;
        }

        await using (reader)
        {
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
                    // Provider constraint: microsoft.Data.Sqlite reports every source expression as
                    // BLOB / byte[] during a zero-row probe. Only an origin column carries a
                    // meaningful type there; treating the expression as a known BLOB would make
                    // ordinary text/number literals impossible to filter.
                    HasKnownType = col.DataType is not null
                        && (dialect != ReportDialect.Sqlite
                            || !string.IsNullOrWhiteSpace(col.BaseColumnName)),
                    IsNullable = col.AllowDBNull ?? true,
                });
            }

            return ReportSchema.Create(def.Name, columns);
        }
    }
}
