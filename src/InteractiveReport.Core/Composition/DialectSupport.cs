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
        _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null),
    };

    /// <summary>
    /// Oracle treats '' as NULL, so text blank/nblank collapses to a pure NULL test there;
    /// elsewhere text-blank means (IS NULL OR = '').
    /// </summary>
    public static bool EmptyStringIsNull(ReportDialect dialect) => dialect == ReportDialect.Oracle;

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
        _ => throw new ArgumentOutOfRangeException(nameof(fn), fn, null),
    };
}
