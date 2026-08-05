using System.Data.Common;
using InteractiveReport.Core.Model;
using SqlKata;

namespace InteractiveReport.Core.Execution;

internal static class CommandBuilder
{
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
        ReportDefinition def)
        => Build(connection, compiled, contextParams, def.CommandTimeoutSeconds, def.Dialect);

    public static DbCommand Build(
        DbConnection connection,
        SqlResult compiled,
        IReadOnlyDictionary<string, object?> contextParams,
        int commandTimeoutSeconds,
        ReportDialect dialect)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = compiled.Sql;
        cmd.CommandTimeout = commandTimeoutSeconds;

        foreach (var (name, value) in compiled.NamedBindings)
            AddParameter(cmd, Normalize(name), value, dialect);

        foreach (var (name, value) in contextParams)
            AddParameter(cmd, name, value, dialect);

        return cmd;
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
