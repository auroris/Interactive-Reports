using System.Data;
using InteractiveReport.Core.Model;
using SqlKata.Compilers;

namespace InteractiveReport.Core.Composition;

public static class DialectSupport
{
    public static Compiler GetCompiler(ReportDialect dialect) => dialect switch
    {
        ReportDialect.SqlServer => new SqlServerCompiler(),
        ReportDialect.Oracle => new OracleCompiler(),
        ReportDialect.Sqlite => new SqliteCompiler(),
        ReportDialect.Postgres => new PostgresCompiler(),
        _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null),
    };

    /// <summary>
    /// The isolation level that gives one request's multiple read statements a single
    /// consistent view of the data, without read locks where the engine offers a
    /// snapshot: Postgres REPEATABLE READ and Oracle SERIALIZABLE are snapshot reads,
    /// SQLite transactions always read one snapshot, and SQL Server needs SNAPSHOT
    /// (gated on ALLOW_SNAPSHOT_ISOLATION — see ReportConnectionManager's probe;
    /// REPEATABLE READ there would take shared locks and still admit phantoms).
    /// </summary>
    public static IsolationLevel ConsistentReadIsolation(ReportDialect dialect) => dialect switch
    {
        ReportDialect.SqlServer => IsolationLevel.Snapshot,
        ReportDialect.Oracle => IsolationLevel.Serializable,
        ReportDialect.Sqlite => IsolationLevel.Serializable,
        ReportDialect.Postgres => IsolationLevel.RepeatableRead,
        _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null),
    };

    /// <summary>
    /// Aggregate SQL fragment. quotedCol arrives in SqlKata bracket form ("[COL]") so raw
    /// fragments still get dialect-correct identifier quoting. Count counts non-null
    /// values of the column (row count is TotalRows). SQL Server AVG over integers
    /// truncates, so it gets a float cast there.
    /// </summary>
    public static string AggregateExpression(ReportDialect dialect, AggregateFn fn, string quotedCol) => fn switch
    {
        AggregateFn.Count => $"COUNT({quotedCol})",
        AggregateFn.CountDistinct => $"COUNT(DISTINCT {quotedCol})",
        AggregateFn.Sum => $"SUM({quotedCol})",
        AggregateFn.Min => $"MIN({quotedCol})",
        AggregateFn.Max => $"MAX({quotedCol})",
        AggregateFn.Avg => dialect == ReportDialect.SqlServer
            ? $"AVG(CAST({quotedCol} AS FLOAT))"
            : $"AVG({quotedCol})",
        AggregateFn.Median => throw new InvalidOperationException(
            "Median requires QueryComposer's ranked aggregate shape."),
        _ => throw new ArgumentOutOfRangeException(nameof(fn), fn, null),
    };
}
