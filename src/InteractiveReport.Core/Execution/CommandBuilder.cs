using System.Collections.Concurrent;
using System.Data.Common;
using System.Reflection;
using InteractiveReport.Core.Model;
using Microsoft.Extensions.Logging;
using SqlKata;

namespace InteractiveReport.Core.Execution;

internal static class CommandBuilder
{
    private static readonly ConcurrentDictionary<Type, Action<DbCommand>?> BindByNameSetters = new();
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
        => Build(connection, compiled, contextParams, def.CommandTimeoutSeconds, def.Dialect, logger);

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

        // Logging discipline: SQL text at Debug only, and parameter VALUES never — the
        // values are user filters and row-security context, i.e. data.
        // Use the command's final text rather than the compiler result so the log is
        // exactly what Execute* submits to the provider. Structured logging keeps the
        // complete multi-line statement available to sinks without interpolating it
        // unless Debug is enabled.
        logger?.LogDebug("Executing report SQL:\n{Sql}", cmd.CommandText);

        return cmd;
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
