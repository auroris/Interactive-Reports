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
        /// Quotes an Oracle identifier after protecting literal question marks from SqlKata parsing.
        /// </summary>
        /// <param name="value">The identifier segment to protect and quote.</param>
        /// <returns>The Oracle-quoted identifier.</returns>
        public override string WrapValue(string value)
            => base.WrapValue(SqlKataSyntax.ProtectQuestionMarks(value));
    }

    /// <summary>
    /// Oracle 11g has no OFFSET/FETCH, so every limited query becomes a ROWNUM window. SqlKata's
    /// <c>UseLegacyPagination</c> wrapper is not used: for a page beyond the first it returns its ROWNUM
    /// helper column through <c>SELECT *</c>, so the page carries one more column than its projection.
    /// This compiler emits the same window but names the query's own output columns in the outer SELECT.
    /// </summary>
    private sealed class InteractiveReportOracle11gCompiler : OracleCompiler
    {
        private const string WrapperAlias = "results_wrapper";
        private const string RowNumberAlias = "row_num";

        /// <summary>
        /// Compiles one Oracle 11g query with ROWNUM pagination and restores literal question marks protected in raw fragments.
        /// </summary>
        /// <param name="query">The SqlKata query to compile.</param>
        /// <returns>An Oracle result with executable and debug SQL decoded.</returns>
        public override SqlResult Compile(Query query)
            => RestoreLiteralQuestionMarks(base.Compile(query));

        /// <summary>
        /// Quotes an Oracle identifier after protecting literal question marks from SqlKata parsing.
        /// </summary>
        /// <param name="value">The identifier segment to protect and quote.</param>
        /// <returns>The Oracle-quoted identifier.</returns>
        public override string WrapValue(string value)
            => base.WrapValue(SqlKataSyntax.ProtectQuestionMarks(value));

        /// <summary>
        /// Emits no OFFSET/FETCH clause; <see cref="CompileSelectQuery"/> applies the ROWNUM window instead.
        /// </summary>
        /// <param name="ctx">The result being compiled.</param>
        /// <returns>Always <see langword="null"/>.</returns>
        public override string? CompileLimit(SqlResult ctx) => null;

        /// <summary>
        /// Compiles a select statement and wraps a limited or offset query in a ROWNUM window that
        /// preserves the projection width.
        /// </summary>
        /// <param name="query">The SqlKata query to compile.</param>
        /// <returns>The compiled result whose raw SQL and positional bindings include the window.</returns>
        /// <exception cref="InvalidOperationException">Thrown when an offset query has no projection this compiler can name in the outer SELECT.</exception>
        protected override SqlResult CompileSelectQuery(Query query)
        {
            var ctx = base.CompileSelectQuery(query);
            var limit = ctx.Query.GetOneComponent<LimitClause>("limit", EngineCode)?.Limit ?? 0;
            var offset = ctx.Query.GetOneComponent<OffsetClause>("offset", EngineCode)?.Offset ?? 0;
            if (limit == 0 && offset == 0) return ctx;

            if (offset == 0)
            {
                ctx.RawSql = $"SELECT * FROM ({ctx.RawSql}) WHERE ROWNUM <= ?";
                ctx.Bindings.Add(limit);
                return ctx;
            }

            var projection = string.Join(", ", OutputColumns(ctx.Query));
            var ranked = $"SELECT {WrapValue(WrapperAlias)}.*, ROWNUM {WrapValue(RowNumberAlias)} "
                + $"FROM ({ctx.RawSql}) {WrapValue(WrapperAlias)}";
            if (limit == 0)
            {
                ctx.RawSql = $"SELECT {projection} FROM ({ranked}) WHERE {WrapValue(RowNumberAlias)} > ?";
                ctx.Bindings.Add(offset);
                return ctx;
            }

            ctx.RawSql = $"SELECT {projection} FROM ({ranked} WHERE ROWNUM <= ?) WHERE {WrapValue(RowNumberAlias)} > ?";
            ctx.Bindings.Add(limit + offset);
            ctx.Bindings.Add(offset);
            return ctx;
        }

        /// <summary>
        /// Names each select item's output column exactly as the inner statement emits it.
        /// </summary>
        /// <param name="query">The query whose select components are re-projected.</param>
        /// <returns>Quoted output identifiers in projection order.</returns>
        /// <exception cref="InvalidOperationException">Thrown for <c>SELECT *</c> or a select item whose output name cannot be determined.</exception>
        private IEnumerable<string> OutputColumns(Query query)
        {
            var columns = query.GetComponents<AbstractColumn>("select", EngineCode);
            if (columns.Count == 0)
                throw new InvalidOperationException(
                    "Oracle 11g pagination beyond the first page requires an explicit projection; SELECT * cannot be re-projected around the ROWNUM window.");
            foreach (var column in columns)
            {
                yield return column switch
                {
                    Column plain => ColumnOutputName(plain.Name),
                    RawColumn raw => RawOutputName(raw.Expression),
                    QueryColumn sub when !string.IsNullOrWhiteSpace(sub.Query.QueryAlias) => WrapValue(sub.Query.QueryAlias),
                    AggregatedColumn aggregate => AggregateOutputName(aggregate),
                    _ => throw new InvalidOperationException(
                        $"Oracle 11g pagination cannot re-project a {column.GetType().Name} select item."),
                };
            }
        }

        /// <summary>
        /// Resolves an aggregated select item's output name. SqlKata names such an item only when its
        /// column carries an alias; an unaliased aggregate has no stable name to re-project.
        /// </summary>
        /// <param name="aggregate">The aggregated select item.</param>
        /// <returns>The quoted alias.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the aggregate carries no alias.</exception>
        private string AggregateOutputName(AggregatedColumn aggregate)
        {
            if (aggregate.Column is Column { Name: var name })
            {
                var (_, alias) = SplitAlias(name);
                if (!string.IsNullOrWhiteSpace(alias)) return WrapValue(alias);
            }
            throw new InvalidOperationException(
                "Oracle 11g pagination beyond the first page requires every aggregate select item to carry an alias.");
        }

        /// <summary>
        /// Resolves a plain column's output name the way SqlKata's <c>Wrap</c> does: the alias when present,
        /// otherwise the last dotted segment.
        /// </summary>
        /// <param name="name">The select item as written, optionally qualified or aliased.</param>
        /// <returns>The quoted output identifier.</returns>
        private string ColumnOutputName(string name)
        {
            var (columnName, alias) = SplitAlias(name);
            var output = alias ?? columnName;
            if (output.Contains('*'))
                throw new InvalidOperationException(
                    "Oracle 11g pagination beyond the first page cannot re-project a wildcard select item.");
            var segment = output.LastIndexOf('.') is var dot && dot >= 0 ? output[(dot + 1)..] : output;
            return WrapValue(segment);
        }

        /// <summary>
        /// Extracts the trailing <c>AS alias</c> from a raw select expression. Every raw projection in the
        /// engine ends with one, written through <see cref="SqlKataSyntax.Identifier"/>.
        /// </summary>
        /// <param name="expression">The raw select expression.</param>
        /// <returns>The alias rendered exactly as the inner statement renders it.</returns>
        private string RawOutputName(string expression)
        {
            var text = expression.TrimEnd();
            var start = AliasStart(text);
            var head = start > 0 ? text[..start].TrimEnd() : "";
            if (start <= 0
                || head.Length < 3
                || !head.EndsWith("AS", StringComparison.OrdinalIgnoreCase)
                || !char.IsWhiteSpace(head[^3]))
                throw new InvalidOperationException(
                    "Oracle 11g pagination beyond the first page requires every raw select item to end with an AS alias.");
            return WrapIdentifiers(text[start..]);
        }

        /// <summary>
        /// Finds where the trailing identifier token starts: a SqlKata <c>[marker]</c> token, a
        /// double-quoted identifier with doubled embedded quotes, or a bare identifier.
        /// </summary>
        /// <param name="text">The trimmed raw expression.</param>
        /// <returns>The token's start index, or -1 when the text does not end with an identifier token.</returns>
        private static int AliasStart(string text)
        {
            if (text.Length == 0) return -1;
            if (text[^1] == ']')
            {
                // Markers inside a token are backslash-escaped, so the first unescaped '[' opens it.
                for (var index = text.Length - 2; index >= 0; index--)
                {
                    if (text[index] == '[' && (index == 0 || text[index - 1] != '\\')) return index;
                }
                return -1;
            }
            if (text[^1] == '"')
            {
                var index = text.Length - 2;
                while (index >= 0)
                {
                    if (text[index] != '"')
                    {
                        index--;
                        continue;
                    }
                    if (index > 0 && text[index - 1] == '"')
                    {
                        index -= 2;
                        continue;
                    }
                    return index;
                }
                return -1;
            }
            var start = text.Length;
            while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] is '_' or '$' or '#'))
                start--;
            return start == text.Length ? -1 : start;
        }
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
