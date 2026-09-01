using System.Collections;
using InteractiveReport.Core.Model;
using SqlKata;
using SqlKata.Compilers;

namespace InteractiveReport.Core.Composition;

/// <summary>Creates SqlKata compilers with the Interactive Reports raw-SQL codec and supplies shared dialect expressions.</summary>
public static class DialectSupport
{
    /// <summary>
    /// Returns the compiler paired with Interactive Reports' raw-SQL codec. Queries produced
    /// by the relation lowerer must use this compiler so literal raw marker characters and question marks
    /// remain distinct from SqlKata syntax.
    /// </summary>
    /// <param name="dialect">The database dialect whose SQL rules apply.</param>
    /// <returns>A new compiler for the selected dialect.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="dialect"/> is unsupported.</exception>
    public static Compiler GetCompiler(ReportDialect dialect) => dialect switch
    {
        ReportDialect.SqlServer => new InteractiveReportSqlServerCompiler(),
        ReportDialect.Oracle => new InteractiveReportOracleCompiler(),
        ReportDialect.Oracle11g => new InteractiveReportOracle11gCompiler(),
        ReportDialect.Sqlite => new InteractiveReportSqliteCompiler(),
        ReportDialect.Postgres => new InteractiveReportPostgresCompiler(),
        _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null),
    };

    /// <summary>
    /// Restores literal question marks after SQL compilation has assigned parameter markers.
    /// </summary>
    /// <param name="result">The SqlKata result containing encoded raw fragments.</param>
    /// <returns>A result wrapper whose executable and diagnostic SQL restore protected literals.</returns>
    private static SqlResult RestoreLiteralQuestionMarks(SqlResult result)
        => new InteractiveReportSqlResult(result);

    private sealed class InteractiveReportSqlResult : SqlResult
    {
        private readonly SqlResult _encoded;

        /// <summary>
        /// Copies an encoded SqlKata result and restores protected literals in executable SQL.
        /// </summary>
        /// <param name="encoded">The compiler result containing reversible raw-SQL sentinels.</param>
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

        /// <summary>
        /// Formats debug SQL while protecting sentinel-bearing binding values until final restoration.
        /// </summary>
        /// <returns>The object's string representation.</returns>
        public override string ToString()
        {
            // SqlResult renders bindings into its debug string before this codec can restore
            // raw-SQL sentinels. Protect sentinel-bearing bound text as well, including values
            // expanded from IN-list bindings, so debug SQL remains a faithful representation
            // even for private-use Unicode data.
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

        /// <summary>
        /// Recursively protects literal question marks in a debug binding before SqlKata interpolates it.
        /// </summary>
        /// <param name="value">The scalar, byte array, or enumerable binding value.</param>
        /// <returns>A protected copy of strings and enumerables; other values are returned unchanged.</returns>
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
        /// <summary>
        /// Compiles one SQL Server query and restores literal question marks protected in raw fragments.
        /// </summary>
        /// <param name="query">The SqlKata query to compile.</param>
        /// <returns>A SQL Server result with executable and debug SQL decoded.</returns>
        public override SqlResult Compile(Query query)
            => RestoreLiteralQuestionMarks(base.Compile(query));
        /// <summary>
        /// Compiles a SQL Server query batch and restores literal question marks protected in raw fragments.
        /// </summary>
        /// <param name="queries">The query collection to combine or execute.</param>
        /// <returns>A SQL Server result with executable and debug SQL decoded.</returns>
        public override SqlResult Compile(IEnumerable<Query> queries)
            => RestoreLiteralQuestionMarks(base.Compile(queries));
        /// <summary>
        /// Quotes a SQL Server identifier after protecting literal question marks from SqlKata parsing.
        /// </summary>
        /// <param name="value">The identifier segment to protect and quote.</param>
        /// <returns>The SQL Server-quoted identifier.</returns>
        public override string WrapValue(string value)
            => base.WrapValue(SqlKataSyntax.ProtectQuestionMarks(value));
    }

    private sealed class InteractiveReportOracleCompiler : OracleCompiler
    {
        /// <summary>
        /// Compiles one Oracle query and restores literal question marks protected in raw fragments.
        /// </summary>
        /// <param name="query">The SqlKata query to compile.</param>
        /// <returns>An Oracle result with executable and debug SQL decoded.</returns>
        public override SqlResult Compile(Query query)
            => RestoreLiteralQuestionMarks(base.Compile(query));
        /// <summary>
        /// Compiles an Oracle query batch and restores literal question marks protected in raw fragments.
        /// </summary>
        /// <param name="queries">The query collection to combine or execute.</param>
        /// <returns>An Oracle result with executable and debug SQL decoded.</returns>
        public override SqlResult Compile(IEnumerable<Query> queries)
            => RestoreLiteralQuestionMarks(base.Compile(queries));
        /// <summary>
        /// Quotes an Oracle identifier after protecting literal question marks from SqlKata parsing.
        /// </summary>
        /// <param name="value">The identifier segment to protect and quote.</param>
        /// <returns>The Oracle-quoted identifier.</returns>
        public override string WrapValue(string value)
            => base.WrapValue(SqlKataSyntax.ProtectQuestionMarks(value));
    }

    private sealed class InteractiveReportOracle11gCompiler : OracleCompiler
    {
        public InteractiveReportOracle11gCompiler()
        {
            UseLegacyPagination = true;
        }

        /// <summary>
        /// Compiles one Oracle 11g query with ROWNUM pagination and restores literal question marks protected in raw fragments.
        /// </summary>
        /// <param name="query">The SqlKata query to compile.</param>
        /// <returns>An Oracle result with executable and debug SQL decoded.</returns>
        public override SqlResult Compile(Query query)
            => RestoreLiteralQuestionMarks(base.Compile(query));

        /// <summary>
        /// Compiles an Oracle 11g query batch and restores literal question marks protected in raw fragments.
        /// </summary>
        /// <param name="queries">The query collection to combine or execute.</param>
        /// <returns>An Oracle result with executable and debug SQL decoded.</returns>
        public override SqlResult Compile(IEnumerable<Query> queries)
            => RestoreLiteralQuestionMarks(base.Compile(queries));

        /// <summary>
        /// Quotes an Oracle identifier after protecting literal question marks from SqlKata parsing.
        /// </summary>
        /// <param name="value">The identifier segment to protect and quote.</param>
        /// <returns>The Oracle-quoted identifier.</returns>
        public override string WrapValue(string value)
            => base.WrapValue(SqlKataSyntax.ProtectQuestionMarks(value));
    }

    private sealed class InteractiveReportSqliteCompiler : SqliteCompiler
    {
        /// <summary>
        /// Compiles one SQLite query and restores literal question marks protected in raw fragments.
        /// </summary>
        /// <param name="query">The SqlKata query to compile.</param>
        /// <returns>A SQLite result with executable and debug SQL decoded.</returns>
        public override SqlResult Compile(Query query)
            => RestoreLiteralQuestionMarks(base.Compile(query));
        /// <summary>
        /// Compiles a SQLite query batch and restores literal question marks protected in raw fragments.
        /// </summary>
        /// <param name="queries">The query collection to combine or execute.</param>
        /// <returns>A SQLite result with executable and debug SQL decoded.</returns>
        public override SqlResult Compile(IEnumerable<Query> queries)
            => RestoreLiteralQuestionMarks(base.Compile(queries));
        /// <summary>
        /// Quotes a SQLite identifier after protecting literal question marks from SqlKata parsing.
        /// </summary>
        /// <param name="value">The identifier segment to protect and quote.</param>
        /// <returns>The SQLite-quoted identifier.</returns>
        public override string WrapValue(string value)
            => base.WrapValue(SqlKataSyntax.ProtectQuestionMarks(value));
    }

    private sealed class InteractiveReportPostgresCompiler : PostgresCompiler
    {
        /// <summary>
        /// Compiles one PostgreSQL query and restores literal question marks protected in raw fragments.
        /// </summary>
        /// <param name="query">The SqlKata query to compile.</param>
        /// <returns>A PostgreSQL result with executable and debug SQL decoded.</returns>
        public override SqlResult Compile(Query query)
            => RestoreLiteralQuestionMarks(base.Compile(query));
        /// <summary>
        /// Compiles a PostgreSQL query batch and restores literal question marks protected in raw fragments.
        /// </summary>
        /// <param name="queries">The query collection to combine or execute.</param>
        /// <returns>A PostgreSQL result with executable and debug SQL decoded.</returns>
        public override SqlResult Compile(IEnumerable<Query> queries)
            => RestoreLiteralQuestionMarks(base.Compile(queries));
        /// <summary>
        /// Quotes a PostgreSQL identifier after protecting literal question marks from SqlKata parsing.
        /// </summary>
        /// <param name="value">The identifier segment to protect and quote.</param>
        /// <returns>The PostgreSQL-quoted identifier.</returns>
        public override string WrapValue(string value)
            => base.WrapValue(SqlKataSyntax.ProtectQuestionMarks(value));
    }

    /// <summary>
    /// Builds an aggregate SQL fragment. <paramref name="quotedCol"/> is already encoded for a SqlKata raw
    /// fragment. Count counts non-null values of the column (row count is TotalRows). SQL Server AVG over
    /// integers truncates, so it gets a float cast there.
    /// </summary>
    /// <param name="dialect">The database dialect whose SQL rules apply.</param>
    /// <param name="fn">The aggregate function to emit.</param>
    /// <param name="quotedCol">The dialect-quoted SQL column expression to aggregate.</param>
    /// <returns>The SQL expression implementing the aggregate.</returns>
    /// <exception cref="InvalidOperationException">Thrown for median, which requires a ranked relation rather than a scalar expression.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="fn"/> is unsupported.</exception>
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
            "Median requires the ranked aggregate relation shape."),
        _ => throw new ArgumentOutOfRangeException(nameof(fn), fn, null),
    };
}
