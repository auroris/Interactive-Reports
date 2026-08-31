using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Reflection;
using System.Text;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Validation;
using Microsoft.Extensions.Logging;
using SqlKata;

namespace InteractiveReport.Core.Execution;

/// <summary>
/// Converts compiled provider-neutral SQL and resolved context values into executable ADO.NET commands.
/// It owns parameter naming, provider-specific binding adjustments, the application parameter ceiling,
/// Oracle REF CURSOR batching, and SQL-only diagnostic logging. Callers own and dispose returned commands.
/// </summary>
internal static class CommandBuilder
{
    // Keep a provider-independent application ceiling below SQL Server's
    // 2100 hard limit. It also bounds deep Pivot chains and large expression lists before any
    // supported provider receives an unexpectedly large command.
    internal const int MaxParameters = 2000;
    private static readonly ConcurrentDictionary<Type, Action<DbCommand>?> BindByNameSetters = new();
    private static readonly ConcurrentDictionary<Type, Action<DbParameter>?> RefCursorSetters = new();
    /// <summary>
    /// Creates a command from a compiled SQLKata result using execution settings from the definition.
    /// Composer bindings are named p0, p1, ... (context parameter names matching that pattern are rejected
    /// at definition load); providers match parameter names prefix-insensitively, so one code path serves
    /// @-style and :-style dialects.
    /// </summary>
    /// <param name="connection">The connection that creates and will execute the command.</param>
    /// <param name="compiled">The SQL text and composer-generated named bindings.</param>
    /// <param name="contextParams">Request-scoped parameter values referenced by the report definition.</param>
    /// <param name="def">Supplies the command timeout and effective SQL dialect.</param>
    /// <param name="logger">Receives final SQL text; <see langword="null"/> disables logging.</param>
    /// <returns>A configured, unexecuted command owned by the caller.</returns>
    /// <exception cref="ReportValidationException">Thrown when compiled and context bindings exceed <see cref="MaxParameters"/>.</exception>
    /// <remarks>Creates a command and parameters from <paramref name="connection"/> and may emit a debug log; it does not open the connection or execute SQL.</remarks>
    public static DbCommand Build(
        DbConnection connection,
        SqlResult compiled,
        IReadOnlyDictionary<string, object?> contextParams,
        ReportDefinition def,
        ILogger? logger = null)
        => Build(connection, compiled, contextParams, def.CommandTimeoutSeconds, def.GetEffectiveDialect(), logger);

    /// <summary>
    /// Creates a provider command, binds compiled and context parameters, and applies the execution timeout.
    /// </summary>
    /// <param name="connection">The connection that creates and will execute the command.</param>
    /// <param name="compiled">The compiled SQL and ordered parameter bindings.</param>
    /// <param name="contextParams">Request-scoped parameter values referenced by the report definition.</param>
    /// <param name="commandTimeoutSeconds">The positive command timeout in seconds.</param>
    /// <param name="dialect">The database dialect whose SQL rules apply.</param>
    /// <param name="logger">Receives final SQL text; <see langword="null"/> disables logging.</param>
    /// <returns>A configured, unexecuted command owned by the caller.</returns>
    /// <exception cref="ReportValidationException">Thrown when the report state violates the report contract.</exception>
    /// <remarks>Creates a command and parameters from <paramref name="connection"/> and may emit a debug log; it does not open the connection or execute SQL.</remarks>
    public static DbCommand Build(
        DbConnection connection,
        SqlResult compiled,
        IReadOnlyDictionary<string, object?> contextParams,
        int commandTimeoutSeconds,
        ReportDialect dialect,
        ILogger? logger = null)
    {
        var parameterCount = (long)compiled.NamedBindings.Count + contextParams.Count;
        if (parameterCount > MaxParameters)
            throw new ReportValidationException(
                [new ValidationError(
                    "query",
                    $"report commands may contain at most {MaxParameters} parameters")]);
        var cmd = connection.CreateCommand();
        cmd.CommandText = compiled.Sql;
        cmd.CommandTimeout = commandTimeoutSeconds;

        // ODP.NET binds by position unless told otherwise. Context
        // parameters appear first in the SQL text (inside the base subquery) but are added last
        // here, so positional binding would silently misbind them. Set BindByName via
        // reflection to avoid a hard Oracle provider dependency.
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
    /// Builds one anonymous Oracle PL/SQL block whose ordered OUT REF CURSORs
    /// carry several report datasets. Named composer bindings are shared when their names and values agree;
    /// disagreement is an internal composition error rather than a reason to submit a command with ambiguous
    /// parameter meaning.
    /// </summary>
    /// <param name="connection">The Oracle connection that creates and will execute the command.</param>
    /// <param name="resultSets">The Oracle cursor result sets to combine into one executable batch.</param>
    /// <param name="contextParams">Request-scoped parameter values referenced by the report definition.</param>
    /// <param name="def">Supplies the required Oracle dialect and command timeout.</param>
    /// <param name="logger">Receives final PL/SQL text; <see langword="null"/> disables logging.</param>
    /// <returns>A configured, unexecuted Oracle command owned by the caller.</returns>
    /// <exception cref="InvalidOperationException">Thrown for a non-Oracle definition or conflicting binding values across result sets.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="resultSets"/> is empty.</exception>
    /// <exception cref="ReportValidationException">Thrown when input and output parameters exceed <see cref="MaxParameters"/>.</exception>
    /// <remarks>Creates one command, input parameters, and ordered output cursor parameters; it does not open the connection or execute the batch.</remarks>
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

        if ((long)inputs.Count + resultSets.Count > MaxParameters)
            throw new ReportValidationException(
                [new ValidationError(
                    "query",
                    $"report commands may contain at most {MaxParameters} parameters")]);

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
    /// Records final SQL text immediately before a caller submits a hand-built command. Parameter
    /// values are deliberately excluded: they can contain user filters, report documents, identities, and
    /// row-security context.
    /// </summary>
    /// <param name="command">The fully constructed command whose text will be logged.</param>
    /// <param name="logger">Receives the debug event; <see langword="null"/> makes the method a no-op.</param>
    /// <remarks>May emit a debug log. Parameter values are never logged.</remarks>
    internal static void Log(DbCommand command, ILogger? logger)
    {
        // Use the command's final text so the log matches what Execute*
        // submits to the provider. With no caller-supplied logger this is a true no-op.
        logger?.LogDebug("Executing report SQL:\n{Sql}", command.CommandText);
    }

    /// <summary>
    /// Enables an Oracle provider command's public <c>BindByName</c> property when available.
    /// </summary>
    /// <param name="cmd">The provider command to mutate.</param>
    /// <remarks>Caches a reflection setter per command type and silently does nothing for providers without the property.</remarks>
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

    /// <summary>
    /// Adds the Oracle output ref-cursor parameter required by a batch command.
    /// </summary>
    /// <param name="cmd">The Oracle command that will own the output parameter.</param>
    /// <param name="name">The unique output binding name.</param>
    /// <exception cref="InvalidOperationException">Thrown when the provider parameter type does not expose <c>OracleDbType.RefCursor</c>.</exception>
    /// <remarks>Creates and appends one output parameter and caches its provider-specific type setter.</remarks>
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

    /// <summary>
    /// Removes SQLKata or dialect parameter prefixes from a binding name.
    /// </summary>
    /// <param name="name">The raw binding name, optionally starting with <c>@</c> or <c>:</c>.</param>
    /// <returns>The parameter name without a provider prefix.</returns>
    private static string Normalize(string name) => name.TrimStart('@', ':');

    /// <summary>
    /// Creates and binds one provider parameter to the database command.
    /// </summary>
    /// <param name="cmd">The command that will own the parameter.</param>
    /// <param name="name">The normalized parameter name without a provider prefix.</param>
    /// <param name="value">The compiled binding value assigned to the parameter.</param>
    /// <param name="dialect">Controls provider-specific value normalization.</param>
    /// <remarks>Creates and appends one input parameter. SQLite decimal values are converted to its native floating-point representation.</remarks>
    private static void AddParameter(DbCommand cmd, string name, object? value, ReportDialect dialect)
    {
        // Microsoft.Data.SQLite binds decimal as text; comparisons against
        // affinity-less expressions (computed columns) then hit SQLite's REAL-always-<-TEXT
        // cross-type rule and match nothing. Double is exactly SQLite's native numeric storage,
        // so the conversion is faithful to the engine rather than lossy for it.
        if (dialect == ReportDialect.Sqlite && value is decimal dec)
            value = (double)dec;

        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }
}
