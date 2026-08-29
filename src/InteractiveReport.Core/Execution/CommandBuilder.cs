using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Reflection;
using System.Text;
using InteractiveReport.Core.Model;
using Microsoft.Extensions.Logging;
using SqlKata;

namespace InteractiveReport.Core.Execution;

internal static class CommandBuilder
{
    private static readonly ConcurrentDictionary<Type, Action<DbCommand>?> BindByNameSetters = new();
    private static readonly ConcurrentDictionary<Type, Action<DbParameter>?> RefCursorSetters = new();
    /// <summary>
    /// Builds a DbCommand from a compiled SqlKata result plus server-resolved context
    /// parameters. Composer bindings are named p0, p1, ... (context parameter names
    /// matching that pattern are rejected at definition load); providers match parameter
    /// names prefix-insensitively, so one code path serves @-style and :-style dialects.
    /// </summary>
    public static DbCommand Build(
        DbConnection connection,
        SqlResult compiled,
        IReadOnlyDictionary<string, object?> contextParams,
        ReportDefinition def,
        ILogger? logger = null)
        => Build(connection, compiled, contextParams, def.CommandTimeoutSeconds, def.GetEffectiveDialect(), logger);

    public static DbCommand Build(
        DbConnection connection,
        SqlResult compiled,
        IReadOnlyDictionary<string, object?> contextParams,
        int commandTimeoutSeconds,
        ReportDialect dialect,
        ILogger? logger = null)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = compiled.Sql;
        cmd.CommandTimeout = commandTimeoutSeconds;

        // ODP.NET binds by POSITION unless told otherwise. Context parameters appear
        // first in the SQL text (inside the base subquery) but are added last here, so
        // positional binding would silently misbind them. Set BindByName via reflection
        // to avoid a hard Oracle provider dependency.
        if (dialect == ReportDialect.Oracle)
            EnableBindByName(cmd);

        foreach (var (name, value) in compiled.NamedBindings)
            AddParameter(cmd, Normalize(name), value, dialect);

        foreach (var (name, value) in contextParams)
            AddParameter(cmd, name, value, dialect);

        Log(cmd, logger);

        return cmd;
    }

    /// <summary>
    /// Builds one anonymous Oracle PL/SQL block whose ordered OUT REF CURSORs carry
    /// several report datasets. Named composer bindings are shared when their names
    /// and values agree; disagreement is an internal composition error rather than a
    /// reason to submit a command with ambiguous parameter meaning.
    /// </summary>
    public static DbCommand BuildOracleCursorBatch(
        DbConnection connection,
        IReadOnlyList<SqlResult> resultSets,
        IReadOnlyDictionary<string, object?> contextParams,
        ReportDefinition def,
        ILogger? logger = null)
    {
        if (def.GetEffectiveDialect() != ReportDialect.Oracle)
            throw new InvalidOperationException("Oracle REF CURSOR batches require the Oracle dialect.");
        if (resultSets.Count == 0)
            throw new ArgumentException("At least one result set is required.", nameof(resultSets));

        var inputs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var resultSet in resultSets)
        {
            foreach (var (rawName, value) in resultSet.NamedBindings)
            {
                var name = Normalize(rawName);
                if (inputs.TryGetValue(name, out var existing) && !Equals(existing, value))
                    throw new InvalidOperationException(
                        $"Oracle report batch binding '{name}' has conflicting values across result sets.");
                inputs[name] = value;
            }
        }

        foreach (var (name, value) in contextParams)
        {
            if (inputs.TryGetValue(name, out var existing) && !Equals(existing, value))
                throw new InvalidOperationException(
                    $"Oracle report batch binding '{name}' conflicts with a context parameter.");
            inputs[name] = value;
        }

        var cursorPrefix = "irResult";
        while (Enumerable.Range(0, resultSets.Count).Any(i => inputs.ContainsKey($"{cursorPrefix}{i}")))
            cursorPrefix += "_";

        var cursorNames = Enumerable.Range(0, resultSets.Count)
            .Select(i => $"{cursorPrefix}{i}")
            .ToArray();
        var sql = new StringBuilder("BEGIN\n");
        for (var i = 0; i < resultSets.Count; i++)
        {
            sql.Append("  OPEN :").Append(cursorNames[i]).Append(" FOR\n    ")
                .Append(resultSets[i].Sql.Replace("\n", "\n    ", StringComparison.Ordinal))
                .Append(";\n");
        }
        sql.Append("END;");

        var cmd = connection.CreateCommand();
        cmd.CommandText = sql.ToString();
        cmd.CommandTimeout = def.CommandTimeoutSeconds;
        EnableBindByName(cmd);

        // ODP.NET exposes REF CURSOR result sets in output-parameter binding order.
        foreach (var name in cursorNames)
            AddRefCursorParameter(cmd, name);
        foreach (var (name, value) in inputs)
            AddParameter(cmd, name, value, ReportDialect.Oracle);

        Log(cmd, logger);
        return cmd;
    }

    /// <summary>
    /// Logs the final SQL text immediately before a caller submits a hand-built
    /// command. Parameter values are deliberately excluded: they can contain user
    /// filters, report documents, identities, and row-security context.
    /// </summary>
    internal static void Log(DbCommand command, ILogger? logger)
    {
        // Use the command's final text so the log matches what Execute* submits to
        // the provider. With no caller-supplied logger this is a true no-op.
        logger?.LogDebug("Executing report SQL:\n{Sql}", command.CommandText);
    }

    private static void EnableBindByName(DbCommand cmd)
    {
        var setter = BindByNameSetters.GetOrAdd(cmd.GetType(), static type =>
        {
            var prop = type.GetProperty("BindByName", BindingFlags.Public | BindingFlags.Instance);
            if (prop is null || prop.PropertyType != typeof(bool) || !prop.CanWrite)
                return null;
            return c => prop.SetValue(c, true);
        });
        setter?.Invoke(cmd);
    }

    private static void AddRefCursorParameter(DbCommand cmd, string name)
    {
        var parameter = cmd.CreateParameter();
        parameter.ParameterName = name;
        parameter.Direction = ParameterDirection.Output;

        var setter = RefCursorSetters.GetOrAdd(parameter.GetType(), static type =>
        {
            var property = type.GetProperty("OracleDbType", BindingFlags.Public | BindingFlags.Instance);
            if (property is null || !property.PropertyType.IsEnum || !property.CanWrite)
                return null;
            var refCursor = Enum.Parse(property.PropertyType, "RefCursor", ignoreCase: false);
            return p => property.SetValue(p, refCursor);
        });
        if (setter is null)
            throw new InvalidOperationException(
                $"Oracle parameter type '{parameter.GetType().FullName}' does not expose OracleDbType.RefCursor.");

        setter(parameter);
        cmd.Parameters.Add(parameter);
    }

    private static string Normalize(string name) => name.TrimStart('@', ':');

    private static void AddParameter(DbCommand cmd, string name, object? value, ReportDialect dialect)
    {
        // Microsoft.Data.Sqlite binds decimal as TEXT; comparisons against affinity-less
        // expressions (computed columns) then hit SQLite's REAL-always-<-TEXT cross-type
        // rule and match nothing. Double is exactly SQLite's native numeric storage, so
        // the conversion is faithful to the engine rather than lossy for it.
        if (dialect == ReportDialect.Sqlite && value is decimal dec)
            value = (double)dec;

        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }
}
