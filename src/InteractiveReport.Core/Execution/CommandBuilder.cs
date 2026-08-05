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
        => Build(connection, compiled, contextParams, def.CommandTimeoutSeconds);

    public static DbCommand Build(
        DbConnection connection,
        SqlResult compiled,
        IReadOnlyDictionary<string, object?> contextParams,
        int commandTimeoutSeconds)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = compiled.Sql;
        cmd.CommandTimeout = commandTimeoutSeconds;

        foreach (var (name, value) in compiled.NamedBindings)
            AddParameter(cmd, Normalize(name), value);

        foreach (var (name, value) in contextParams)
            AddParameter(cmd, name, value);

        return cmd;
    }

    private static string Normalize(string name) => name.TrimStart('@', ':');

    private static void AddParameter(DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }
}
