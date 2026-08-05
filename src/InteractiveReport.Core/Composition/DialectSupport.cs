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
}
