using InteractiveReport.Core.Expressions;
using InteractiveReport.Core.Model;
using static InteractiveReport.Core.Tests.TestFixtures;

namespace InteractiveReport.Core.Tests;

/// <summary>
/// Fragment goldens: AST → per-dialect SQL with [bracket] identifiers and '?' bindings.
/// SqlKata translates the brackets at compile time; these lock what we hand it.
/// </summary>
public class ExprEmitterTests
{
    private static readonly DateTime RequestUtcNow =
        new(2026, 8, 29, 12, 34, 56, DateTimeKind.Utc);

    private static readonly IReadOnlyDictionary<string, ColumnModel> Schema =
        OrdersSchema.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

    private static (string Sql, IReadOnlyList<object> Bindings) Emit(string expr, ReportDialect dialect)
    {
        var (ast, error) = ExprParser.Parse(expr, Schema);
        Assert.Null(error);
        return ExprEmitter.Emit(ast!, dialect, RequestUtcNow);
    }

    [Fact]
    public void Literals_become_positional_bindings_never_inline_text()
    {
        var (sql, bindings) = Emit("ROUND(AMOUNT * 1.0825, 2)", ReportDialect.Sqlite);

        Assert.Equal("ROUND(([AMOUNT] * ?), ?)", sql);
        Assert.Equal([1.0825m, 2m], bindings);
    }

    [Fact]
    public void Two_arg_round_casts_into_postgres_only_signature()
    {
        // round(numeric, integer) is the only two-arg ROUND Postgres has; numeric
        // bindings and double-precision columns both need the casts.
        Assert.Equal("ROUND(CAST(([AMOUNT] * ?) AS NUMERIC), CAST(? AS INT))",
            Emit("ROUND(AMOUNT * 1.0825, 2)", ReportDialect.Postgres).Sql);
        Assert.Equal("ROUND([AMOUNT])",
            Emit("ROUND(AMOUNT)", ReportDialect.Postgres).Sql);
    }

    [Fact]
    public void Concat_is_variadic_concat_except_oracle_native_pipes()
    {
        Assert.Equal("CONCAT(UPPER([CUSTOMER]), ?)", Emit("UPPER(CUSTOMER) || '!'", ReportDialect.SqlServer).Sql);
        Assert.Equal("CONCAT(UPPER([CUSTOMER]), ?)", Emit("UPPER(CUSTOMER) || '!'", ReportDialect.Sqlite).Sql);
        Assert.Equal("CONCAT(UPPER([CUSTOMER]), ?)", Emit("UPPER(CUSTOMER) || '!'", ReportDialect.Postgres).Sql);
        Assert.Equal("(UPPER([CUSTOMER]) || ?)", Emit("UPPER(CUSTOMER) || '!'", ReportDialect.Oracle).Sql);
    }

    [Fact]
    public void Two_arg_substr_becomes_to_end_substring_on_sqlserver()
    {
        Assert.Equal("SUBSTRING([CUSTOMER], ?, LEN([CUSTOMER]))", Emit("SUBSTR(CUSTOMER, 2)", ReportDialect.SqlServer).Sql);
        Assert.Equal("SUBSTR([CUSTOMER], ?)", Emit("SUBSTR(CUSTOMER, 2)", ReportDialect.Oracle).Sql);
        Assert.Equal("SUBSTR([CUSTOMER], ?)", Emit("SUBSTR(CUSTOMER, 2)", ReportDialect.Sqlite).Sql);
        Assert.Equal("SUBSTR([CUSTOMER], ?)", Emit("SUBSTR(CUSTOMER, 2)", ReportDialect.Postgres).Sql);
    }

    [Fact]
    public void Length_maps_to_len_only_on_sqlserver()
    {
        Assert.Equal("LEN([CUSTOMER])", Emit("LENGTH(CUSTOMER)", ReportDialect.SqlServer).Sql);
        Assert.Equal("LENGTH([CUSTOMER])", Emit("LENGTH(CUSTOMER)", ReportDialect.Oracle).Sql);
        Assert.Equal("LENGTH([CUSTOMER])", Emit("LENGTH(CUSTOMER)", ReportDialect.Postgres).Sql);
    }

    [Fact]
    public void Date_parts_use_native_idioms_per_dialect()
    {
        Assert.Equal("YEAR([ORDER_DATE])", Emit("YEAR(ORDER_DATE)", ReportDialect.SqlServer).Sql);
        Assert.Equal("EXTRACT(YEAR FROM [ORDER_DATE])", Emit("YEAR(ORDER_DATE)", ReportDialect.Oracle).Sql);
        Assert.Equal("EXTRACT(YEAR FROM [ORDER_DATE])", Emit("YEAR(ORDER_DATE)", ReportDialect.Postgres).Sql);
        Assert.Equal("CAST(strftime('%Y', [ORDER_DATE]) AS INTEGER)", Emit("YEAR(ORDER_DATE)", ReportDialect.Sqlite).Sql);
        Assert.Equal("CAST(strftime('%m', [ORDER_DATE]) AS INTEGER)", Emit("MONTH(ORDER_DATE)", ReportDialect.Sqlite).Sql);
    }

    [Fact]
    public void Date_parts_on_iso_text_get_explicit_conversions_where_extract_is_strict()
    {
        // EXTRACT rejects text on Oracle and Postgres; SQL Server converts ISO text
        // implicitly and SQLite's strftime takes text natively.
        Assert.Equal("EXTRACT(YEAR FROM TO_DATE(SUBSTR([NOTES], 1, 10), 'YYYY-MM-DD'))",
            Emit("YEAR(NOTES)", ReportDialect.Oracle).Sql);
        Assert.Equal("EXTRACT(MONTH FROM CAST([NOTES] AS TIMESTAMP))",
            Emit("MONTH(NOTES)", ReportDialect.Postgres).Sql);
        Assert.Equal("YEAR([NOTES])", Emit("YEAR(NOTES)", ReportDialect.SqlServer).Sql);
        Assert.Equal("CAST(strftime('%Y', [NOTES]) AS INTEGER)", Emit("YEAR(NOTES)", ReportDialect.Sqlite).Sql);
    }

    // --- the date vocabulary --------------------------------------------------

    [Fact]
    public void Now_is_one_request_scoped_utc_binding_on_every_dialect()
    {
        foreach (var dialect in AllDialects)
        {
            var (sql, bindings) = Emit("NOW()", dialect);

            Assert.Equal("?", sql);
            Assert.Equal([RequestUtcNow], bindings);
        }
    }

    [Fact]
    public void To_date_converts_text_and_is_identity_on_dates()
    {
        Assert.Equal("CAST([NOTES] AS DATETIME2)", Emit("TO_DATE(NOTES)", ReportDialect.SqlServer).Sql);
        Assert.Equal("TO_DATE([NOTES], 'YYYY-MM-DD')", Emit("TO_DATE(NOTES)", ReportDialect.Oracle).Sql);
        Assert.Equal("TO_DATE([NOTES], 'YYYY-MM-DD')", Emit("TO_DATE(NOTES)", ReportDialect.Postgres).Sql);
        Assert.Equal("datetime([NOTES])", Emit("TO_DATE(NOTES)", ReportDialect.Sqlite).Sql);

        // Identity on a Date input — except SQLite, where TO_DATE canonicalizes to
        // the full datetime text every date producer emits.
        Assert.Equal("[ORDER_DATE]", Emit("TO_DATE(ORDER_DATE)", ReportDialect.SqlServer).Sql);
        Assert.Equal("[ORDER_DATE]", Emit("TO_DATE(ORDER_DATE)", ReportDialect.Oracle).Sql);
        Assert.Equal("datetime([ORDER_DATE])", Emit("TO_DATE(ORDER_DATE)", ReportDialect.Sqlite).Sql);

        // A validated ISO literal still binds as a parameter.
        var (sql, bindings) = Emit("TO_DATE('2026-01-01')", ReportDialect.Sqlite);
        Assert.Equal("datetime(?)", sql);
        Assert.Equal(["2026-01-01"], bindings);
    }

    [Fact]
    public void Date_trunc_uses_each_engines_truncation_idiom()
    {
        // DATE/DATEFROMPARTS on SQL Server, not DATEADD(DATEDIFF(…, 0, …)): the
        // integer epoch is legacy datetime, which ends the valid range at 1753
        // while TO_DATE accepts ISO years back to 0001.
        Assert.Equal("CAST(CAST(? AS DATE) AS DATETIME2)",
            Emit("DATE_TRUNC('DAY', NOW())", ReportDialect.SqlServer).Sql);
        Assert.Equal("CAST(DATEFROMPARTS(YEAR(?), MONTH(?), 1) AS DATETIME2)",
            Emit("DATE_TRUNC('MONTH', NOW())", ReportDialect.SqlServer).Sql);
        Assert.Equal("CAST(DATEFROMPARTS(YEAR(?), 1, 1) AS DATETIME2)",
            Emit("DATE_TRUNC('YEAR', NOW())", ReportDialect.SqlServer).Sql);
        Assert.Equal("TRUNC(?, 'DD')", Emit("DATE_TRUNC('DAY', NOW())", ReportDialect.Oracle).Sql);
        Assert.Equal("TRUNC(?, 'MM')", Emit("DATE_TRUNC('MONTH', NOW())", ReportDialect.Oracle).Sql);
        Assert.Equal("TRUNC(?, 'YYYY')", Emit("DATE_TRUNC('YEAR', NOW())", ReportDialect.Oracle).Sql);
        Assert.Equal("DATE_TRUNC('month', ?)", Emit("DATE_TRUNC('MONTH', NOW())", ReportDialect.Postgres).Sql);
        Assert.Equal("datetime(?, 'start of month')",
            Emit("DATE_TRUNC('MONTH', NOW())", ReportDialect.Sqlite).Sql);
    }

    [Fact]
    public void Date_arithmetic_is_whole_days_in_each_idiom()
    {
        Assert.Equal("DATEADD(DAY, ?, ?)", Emit("NOW() + 30", ReportDialect.SqlServer).Sql);
        Assert.Equal("DATEADD(DAY, -(?), ?)", Emit("NOW() - 30", ReportDialect.SqlServer).Sql);
        Assert.Equal("(? + ?)", Emit("NOW() + 30", ReportDialect.Oracle).Sql);
        Assert.Equal("(? - ?)", Emit("NOW() - 30", ReportDialect.Oracle).Sql);
        Assert.Equal("(? + (? * INTERVAL '1 day'))", Emit("NOW() + 30", ReportDialect.Postgres).Sql);
        Assert.Equal("(? - (? * INTERVAL '1 day'))", Emit("NOW() - 30", ReportDialect.Postgres).Sql);
        Assert.Equal("datetime(?, (?) || ' days')",
            Emit("NOW() + 30", ReportDialect.Sqlite).Sql);
        Assert.Equal("datetime(?, (-(?)) || ' days')",
            Emit("NOW() - 30", ReportDialect.Sqlite).Sql);

        // Integer-typed columns are provably whole days.
        Assert.Equal("DATEADD(DAY, [ORDER_ID], [ORDER_DATE])",
            Emit("TO_DATE(ORDER_DATE) + ORDER_ID", ReportDialect.SqlServer).Sql);
    }

    [Fact]
    public void To_string_translates_the_portable_format_and_binds_the_mask()
    {
        // The pinned culture keeps FORMAT deterministic — the session language
        // would otherwise choose digits and calendar.
        var (sql, bindings) = Emit("TO_STRING(ORDER_DATE)", ReportDialect.SqlServer);
        Assert.Equal("FORMAT([ORDER_DATE], ?, 'en-US')", sql);
        Assert.Equal(["yyyy'-'MM'-'dd"], bindings);

        (sql, bindings) = Emit("TO_STRING(ORDER_DATE)", ReportDialect.Oracle);
        Assert.Equal("TO_CHAR([ORDER_DATE], ?)", sql);
        Assert.Equal(["YYYY-MM-DD"], bindings);

        (sql, bindings) = Emit("TO_STRING(ORDER_DATE)", ReportDialect.Sqlite);
        Assert.Equal("strftime(?, [ORDER_DATE])", sql);
        Assert.Equal(["%Y-%m-%d"], bindings);

        // The T separator is quoted where TO_CHAR would read it as a pattern; on
        // SQL Server every separator is quoted so none go culture-dependent.
        Assert.Equal([RequestUtcNow, "YYYY-MM-DD\"T\"HH24:MI:SS"],
            Emit("TO_STRING(NOW(), 'YYYY-MM-DDTHH24:MI:SS')", ReportDialect.Postgres).Bindings);
        Assert.Equal([RequestUtcNow, "yyyy'-'MM'-'dd'T'HH':'mm':'ss"],
            Emit("TO_STRING(NOW(), 'YYYY-MM-DDTHH24:MI:SS')", ReportDialect.SqlServer).Bindings);
        Assert.Equal(["%Y-%m-%dT%H:%M:%S", RequestUtcNow],
            Emit("TO_STRING(NOW(), 'YYYY-MM-DDTHH24:MI:SS')", ReportDialect.Sqlite).Bindings);
    }

    [Fact]
    public void Date_producers_emit_typed_nulls()
    {
        // A bare NULL loses the type: Oracle types TO_DATE(NULL) + 1 as NUMBER,
        // and Postgres cannot resolve date_trunc over an untyped NULL at all.
        Assert.Equal("CAST(NULL AS DATETIME2)", Emit("TO_DATE(NULL)", ReportDialect.SqlServer).Sql);
        Assert.Equal("CAST(NULL AS DATE)", Emit("TO_DATE(NULL)", ReportDialect.Oracle).Sql);
        Assert.Equal("CAST(NULL AS TIMESTAMP)", Emit("TO_DATE(NULL)", ReportDialect.Postgres).Sql);
        Assert.Equal("NULL", Emit("TO_DATE(NULL)", ReportDialect.Sqlite).Sql);

        Assert.Equal("CAST(NULL AS DATE)", Emit("DATE_TRUNC('DAY', NULL)", ReportDialect.Oracle).Sql);
        Assert.Equal("(CAST(NULL AS DATE) + ?)", Emit("TO_DATE(NULL) + 1", ReportDialect.Oracle).Sql);
        Assert.Equal("(CAST(NULL AS TIMESTAMP) + (? * INTERVAL '1 day'))",
            Emit("TO_DATE(NULL) + 1", ReportDialect.Postgres).Sql);

        Assert.Equal("CAST(NULL AS NVARCHAR(30))", Emit("TO_STRING(NULL)", ReportDialect.SqlServer).Sql);
        Assert.Equal("CAST(NULL AS VARCHAR2(30))", Emit("TO_STRING(NULL)", ReportDialect.Oracle).Sql);
        Assert.Equal("CAST(NULL AS TEXT)", Emit("TO_STRING(NULL)", ReportDialect.Postgres).Sql);
    }

    [Fact]
    public void Date_comparisons_are_plain_infix_except_sqlite_normalizes_text_dates()
    {
        // Date-only text sorts before its own midnight timestamp, so SQLite wraps
        // non-canonical date operands in datetime(); producers emit canonical text
        // already and stay bare. Other dialects compare natively.
        Assert.Equal("CASE WHEN ([ORDER_DATE] < ?) THEN ? ELSE ? END",
            Emit("CASE WHEN ORDER_DATE < NOW() THEN 1 ELSE 0 END", ReportDialect.SqlServer).Sql);
        Assert.Equal("CASE WHEN ([ORDER_DATE] < ?) THEN ? ELSE ? END",
            Emit("CASE WHEN ORDER_DATE < NOW() THEN 1 ELSE 0 END", ReportDialect.Oracle).Sql);
        Assert.Equal("CASE WHEN (datetime([ORDER_DATE]) < ?) THEN ? ELSE ? END",
            Emit("CASE WHEN ORDER_DATE < NOW() THEN 1 ELSE 0 END", ReportDialect.Sqlite).Sql);
    }

    [Fact]
    public void Between_emits_portably_and_normalizes_dates_on_sqlite()
    {
        foreach (var d in AllDialects)
            Assert.Equal("CASE WHEN ([AMOUNT] BETWEEN ? AND ?) THEN ? ELSE ? END",
                Emit("CASE WHEN AMOUNT BETWEEN 1000 AND 8000 THEN 1 ELSE 0 END", d).Sql);

        Assert.Equal(
            "CASE WHEN ([ORDER_DATE] BETWEEN CAST(? AS DATETIME2) AND CAST(? AS DATETIME2)) THEN ? ELSE ? END",
            Emit("CASE WHEN ORDER_DATE BETWEEN TO_DATE('2026-01-01') AND TO_DATE('2026-12-31') THEN 1 ELSE 0 END",
                ReportDialect.SqlServer).Sql);
        Assert.Equal(
            "CASE WHEN (datetime([ORDER_DATE]) BETWEEN datetime(?) AND datetime(?)) THEN ? ELSE ? END",
            Emit("CASE WHEN ORDER_DATE BETWEEN TO_DATE('2026-01-01') AND TO_DATE('2026-12-31') THEN 1 ELSE 0 END",
                ReportDialect.Sqlite).Sql);
    }

    [Fact]
    public void Every_binary_is_parenthesized_and_unary_minus_wraps()
    {
        Assert.Equal("([AMOUNT] + (? * ?))", Emit("AMOUNT + 2 * 3", ReportDialect.Sqlite).Sql);
        foreach (var dialect in AllDialects)
            Assert.Equal("((1.0 * (-[AMOUNT])) / ?)", Emit("-AMOUNT / 2", dialect).Sql);
    }

    [Fact]
    public void Coalesce_passes_through_on_all_dialects()
    {
        foreach (var d in new[] { ReportDialect.SqlServer, ReportDialect.Oracle, ReportDialect.Sqlite })
            Assert.Equal("COALESCE([NOTES], ?)", Emit("COALESCE(NOTES, 'n/a')", d).Sql);
    }

    // --- CASE, conditions, NULL: the portable core emits identically everywhere ---

    private static readonly ReportDialect[] AllDialects =
        [ReportDialect.SqlServer, ReportDialect.Oracle, ReportDialect.Sqlite, ReportDialect.Postgres];

    [Fact]
    public void Searched_case_emits_identically_on_all_dialects()
    {
        foreach (var d in AllDialects)
        {
            var (sql, bindings) = Emit("CASE WHEN AMOUNT > 1000 THEN 'big' ELSE 'small' END", d);

            Assert.Equal("CASE WHEN ([AMOUNT] > ?) THEN ? ELSE ? END", sql);
            Assert.Equal([1000m, "big", "small"], bindings);
        }
    }

    [Fact]
    public void Simple_case_emits_the_operand_form()
    {
        foreach (var d in AllDialects)
            Assert.Equal("CASE [STATUS] WHEN ? THEN ? ELSE ? END",
                Emit("CASE STATUS WHEN 'S' THEN 1 ELSE 0 END", d).Sql);
    }

    [Fact]
    public void Conditions_parenthesize_and_null_literal_is_the_keyword_not_a_binding()
    {
        var (sql, bindings) = Emit(
            "CASE WHEN NOT (NOTES IS NULL) AND AMOUNT >= 10 THEN NULL ELSE AMOUNT END", ReportDialect.Sqlite);

        Assert.Equal("CASE WHEN ((NOT ([NOTES] IS NULL)) AND ([AMOUNT] >= ?)) THEN NULL ELSE [AMOUNT] END", sql);
        Assert.Equal([10m], bindings);
    }

    [Fact]
    public void Bang_not_equal_emits_angle_brackets()
    {
        Assert.Equal("CASE WHEN ([STATUS] <> ?) THEN ? ELSE ? END",
            Emit("CASE WHEN STATUS != 'X' THEN 1 ELSE 0 END", ReportDialect.Sqlite).Sql);
    }

    [Fact]
    public void Case_without_else_omits_the_clause()
    {
        Assert.Equal("CASE WHEN ([NOTES] IS NOT NULL) THEN UPPER([NOTES]) END",
            Emit("CASE WHEN NOTES IS NOT NULL THEN UPPER(NOTES) END", ReportDialect.Oracle).Sql);
    }

    [Fact]
    public void Dialect_functions_keep_their_idioms_inside_case_conditions()
    {
        Assert.Equal("CASE WHEN (LEN([CUSTOMER]) > ?) THEN ? ELSE ? END",
            Emit("CASE WHEN LENGTH(CUSTOMER) > 5 THEN 1 ELSE 0 END", ReportDialect.SqlServer).Sql);
        Assert.Equal("CASE WHEN (LENGTH([CUSTOMER]) > ?) THEN ? ELSE ? END",
            Emit("CASE WHEN LENGTH(CUSTOMER) > 5 THEN 1 ELSE 0 END", ReportDialect.Oracle).Sql);
    }

    [Fact]
    public void Boolean_valued_columns_lower_to_explicit_predicates_in_condition_position()
    {
        // T-SQL has no boolean expressions: "WHEN [FLAG]" is invalid there, so a
        // bool-valued operand in condition position becomes an explicit "= 1" test.
        var schema = OrdersSchema.Append(Col("IS_PRIORITY", typeof(bool)))
            .ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

        (string Sql, IReadOnlyList<object> Bindings) EmitBool(string expr, ReportDialect dialect)
        {
            var (ast, error) = ExprParser.Parse(expr, schema);
            Assert.Null(error);
            return ExprEmitter.Emit(ast!, dialect);
        }

        foreach (var d in AllDialects.Where(d => d != ReportDialect.Postgres))
        {
            Assert.Equal("CASE WHEN ([IS_PRIORITY] = 1) THEN ? ELSE ? END",
                EmitBool("CASE WHEN IS_PRIORITY THEN 1 ELSE 0 END", d).Sql);
            Assert.Equal("CASE WHEN ((NOT ([IS_PRIORITY] = 1)) AND ([AMOUNT] > ?)) THEN ? ELSE ? END",
                EmitBool("CASE WHEN NOT IS_PRIORITY AND AMOUNT > 5 THEN 1 ELSE 0 END", d).Sql);
        }

        // Postgres booleans are real conditions — "= 1" would be a boolean/integer
        // type error there, so the value emits bare.
        Assert.Equal("CASE WHEN [IS_PRIORITY] THEN ? ELSE ? END",
            EmitBool("CASE WHEN IS_PRIORITY THEN 1 ELSE 0 END", ReportDialect.Postgres).Sql);
        Assert.Equal("CASE WHEN ((NOT [IS_PRIORITY]) AND ([AMOUNT] > ?)) THEN ? ELSE ? END",
            EmitBool("CASE WHEN NOT IS_PRIORITY AND AMOUNT > 5 THEN 1 ELSE 0 END", ReportDialect.Postgres).Sql);
    }
}
