using System.Collections.Concurrent;
using System.Data.Common;
using System.Reflection;
using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Execution;

/// <summary>
/// Classifies provider exceptions per dialect without referencing provider
/// assemblies: provider-specific codes are read through cached reflection (the same
/// technique CommandBuilder uses for Oracle's BindByName), with the standard
/// DbException.SqlState where the provider populates it (Npgsql).
/// </summary>
internal static class DbErrorClassifier
{
    private static readonly ConcurrentDictionary<(Type Type, string Property), Func<DbException, int?>?> IntGetters = new();

    /// <summary>
    /// True when the exception is that dialect's unique-constraint (or unique-index)
    /// violation — the only insert failure an upsert may treat as "the row exists".
    /// SQL Server: 2627 (constraint) / 2601 (index); Oracle: ORA-00001; Postgres:
    /// SQLSTATE 23505; SQLite: extended result codes 1555 (primary key) / 2067 (unique).
    /// </summary>
    public static bool IsUniqueViolation(ReportDialect dialect, DbException exception) => dialect switch
    {
        ReportDialect.SqlServer => IntProperty(exception, "Number") is 2627 or 2601,
        ReportDialect.Oracle => IntProperty(exception, "Number") == 1,
        ReportDialect.Postgres => string.Equals(exception.SqlState, "23505", StringComparison.Ordinal),
        ReportDialect.Sqlite => IntProperty(exception, "SqliteExtendedErrorCode") is 1555 or 2067,
        _ => false,
    };

    private static int? IntProperty(DbException exception, string name)
    {
        var getter = IntGetters.GetOrAdd((exception.GetType(), name), static key =>
        {
            var property = key.Type.GetProperty(key.Property, BindingFlags.Public | BindingFlags.Instance);
            if (property is null || property.PropertyType != typeof(int) || !property.CanRead)
                return null;
            return ex => (int?)property.GetValue(ex);
        });
        return getter?.Invoke(exception);
    }
}
