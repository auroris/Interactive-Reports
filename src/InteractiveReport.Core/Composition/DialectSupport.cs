using System.Collections;
using InteractiveReport.Core.Model;
using SqlKata;
using SqlKata.Compilers;

namespace InteractiveReport.Core.Composition;

public static class DialectSupport
{
    /// <summary>
    /// Returns the compiler paired with Interactive Reports' raw-SQL codec. Queries
    /// produced by <see cref="QueryComposer"/> must use this compiler so literal raw
    /// marker characters and question marks remain distinct from SqlKata syntax.
    /// </summary>
    public static Compiler GetCompiler(ReportDialect dialect) => dialect switch
    {
        ReportDialect.SqlServer => new InteractiveReportSqlServerCompiler(),
        ReportDialect.Oracle => new InteractiveReportOracleCompiler(),
        ReportDialect.Sqlite => new InteractiveReportSqliteCompiler(),
        ReportDialect.Postgres => new InteractiveReportPostgresCompiler(),
        _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null),
    };

    private static SqlResult RestoreLiteralQuestionMarks(SqlResult result)
        => new InteractiveReportSqlResult(result);

    private sealed class InteractiveReportSqlResult : SqlResult
    {
        private readonly SqlResult _encoded;

        public InteractiveReportSqlResult(SqlResult encoded)
            : base("?", "\\")
        {
            _encoded = encoded;
            Query = encoded.Query;
            RawSql = SqlKataSyntax.RestoreCompiled(encoded.RawSql);
            Bindings = encoded.Bindings;
            Sql = SqlKataSyntax.RestoreCompiled(encoded.Sql);
            NamedBindings = encoded.NamedBindings;
        }

        public override string ToString()
        {
            // SqlResult renders bindings into its debug string before this codec can
            // restore raw-SQL sentinels. Protect sentinel-bearing bound text as well,
            // including values expanded from IN-list bindings, so debug SQL remains a
            // faithful representation even for private-use Unicode data.
            var debug = new SqlResult("?", "\\")
            {
                Query = _encoded.Query,
                RawSql = _encoded.RawSql,
                Bindings = _encoded.Bindings
                    .Select(value => ProtectDebugBinding(value)!)
                    .ToList(),
            };
            return SqlKataSyntax.RestoreCompiled(debug.ToString());
        }

        private static object? ProtectDebugBinding(object? value)
            => value switch
            {
                string text => SqlKataSyntax.ProtectQuestionMarks(text),
                byte[] => value,
                IEnumerable values => values.Cast<object?>()
                    .Select(ProtectDebugBinding)
                    .ToArray(),
                _ => value,
            };
    }

    private sealed class InteractiveReportSqlServerCompiler : SqlServerCompiler
    {
        public override SqlResult Compile(Query query)
            => RestoreLiteralQuestionMarks(base.Compile(query));
        public override SqlResult Compile(IEnumerable<Query> queries)
            => RestoreLiteralQuestionMarks(base.Compile(queries));
        public override string WrapValue(string value)
            => base.WrapValue(SqlKataSyntax.ProtectQuestionMarks(value));
    }

    private sealed class InteractiveReportOracleCompiler : OracleCompiler
    {
        public override SqlResult Compile(Query query)
            => RestoreLiteralQuestionMarks(base.Compile(query));
        public override SqlResult Compile(IEnumerable<Query> queries)
            => RestoreLiteralQuestionMarks(base.Compile(queries));
        public override string WrapValue(string value)
            => base.WrapValue(SqlKataSyntax.ProtectQuestionMarks(value));
    }

    private sealed class InteractiveReportSqliteCompiler : SqliteCompiler
    {
        public override SqlResult Compile(Query query)
            => RestoreLiteralQuestionMarks(base.Compile(query));
        public override SqlResult Compile(IEnumerable<Query> queries)
            => RestoreLiteralQuestionMarks(base.Compile(queries));
        public override string WrapValue(string value)
            => base.WrapValue(SqlKataSyntax.ProtectQuestionMarks(value));
    }

    private sealed class InteractiveReportPostgresCompiler : PostgresCompiler
    {
        public override SqlResult Compile(Query query)
            => RestoreLiteralQuestionMarks(base.Compile(query));
        public override SqlResult Compile(IEnumerable<Query> queries)
            => RestoreLiteralQuestionMarks(base.Compile(queries));
        public override string WrapValue(string value)
            => base.WrapValue(SqlKataSyntax.ProtectQuestionMarks(value));
    }

    /// <summary>
    /// Aggregate SQL fragment. quotedCol is already encoded for a SqlKata raw fragment.
    /// Count counts non-null values of the column (row count is
    /// TotalRows). SQL Server AVG over integers truncates, so it gets a float cast there.
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
