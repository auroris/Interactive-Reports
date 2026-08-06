using System.Collections.Concurrent;
using System.Data.Common;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Schema;
using Microsoft.Data.SqlClient;
using Npgsql;
using Oracle.ManagedDataAccess.Client;
using static InteractiveReport.Core.Tests.TestFixtures;

namespace InteractiveReport.Core.Tests;

/// <summary>
/// The M5 verification pass: the same engine corpus the SQLite e2e suite locks, executed
/// against real SQL Server, Oracle, and PostgreSQL instances. Skipped unless the
/// environment provides connection strings:
///
///   IR_TEST_SQLSERVER  e.g. Server=vm;Database=irtest;User Id=irtest;Password=...;TrustServerCertificate=True
///   IR_TEST_ORACLE     e.g. User Id=irtest;Password=...;Data Source=vm:1521/XEPDB1
///   IR_TEST_POSTGRES   e.g. Host=vm;Port=5432;Database=irtest;Username=irtest;Password=...
///
/// Each run drops and recreates a table named IR_TEST_ORDERS in that database and seeds
/// the canonical 10 rows (see docs/TESTING.md). Expected numbers are identical across
/// all dialects — including blank-count 4, which converges by design: SQLite/SqlServer/
/// Postgres count 3 NULLs + 1 empty string; Oracle turns the empty string into a 4th NULL.
/// (Postgres folds unquoted identifiers to lowercase; the engine's case-insensitive
/// schema matching and response dictionaries absorb that without special-casing.)
/// </summary>
public class LiveDialectTests
{
    public static TheoryData<ReportDialect> Dialects => new() { ReportDialect.SqlServer, ReportDialect.Oracle, ReportDialect.Postgres };

    private static readonly IReadOnlyDictionary<string, object?> NoParams = new Dictionary<string, object?>();

    [SkippableTheory]
    [MemberData(nameof(Dialects))]
    public async Task Schema_discovery_reports_number_and_text_kinds(ReportDialect dialect)
    {
        var live = LiveDb.For(dialect);
        var schema = await live.Executor.GetSchema(live.Definition(), NoParams);

        Assert.Equal(ColumnKind.Number, schema.Single(c => c.Name.Equals("AMOUNT", StringComparison.OrdinalIgnoreCase)).Kind);
        Assert.Equal(ColumnKind.Text, schema.Single(c => c.Name.Equals("CUSTOMER", StringComparison.OrdinalIgnoreCase)).Kind);
    }

    [SkippableTheory]
    [MemberData(nameof(Dialects))]
    public async Task Filter_sort_page(ReportDialect dialect)
    {
        var live = LiveDb.For(dialect);
        var result = await live.Executor.Query(live.Definition(), new ReportState
        {
            Filters = [Filter("STATUS = 'SHIPPED'")],
            Sorts = [new SortRule { Col = "AMOUNT", Dir = SortDir.Desc }],
            Page = new PageRequest { Index = 1, Size = 3 },
        }, NoParams);

        Assert.Equal(5, result.TotalRows);
        Assert.Equal([9000m, 7500m, 5000m], result.Rows.Select(r => Convert.ToDecimal(r["AMOUNT"])));
    }

    [SkippableTheory]
    [MemberData(nameof(Dialects))]
    public async Task Search_is_case_insensitive(ReportDialect dialect)
    {
        var live = LiveDb.For(dialect);
        var result = await live.Executor.Query(live.Definition(), new ReportState { Search = "ACME" }, NoParams);

        Assert.Equal(3, result.TotalRows);
    }

    [SkippableTheory]
    [MemberData(nameof(Dialects))]
    public async Task Highlight_conditions_use_the_shared_expression_pipeline(ReportDialect dialect)
    {
        var live = LiveDb.For(dialect);
        var result = await live.Executor.Query(live.Definition(), new ReportState
        {
            Sorts = [new SortRule { Col = "AMOUNT", Dir = SortDir.Desc }],
            Highlights =
            [
                new HighlightRule
                {
                    Id = "large", Scope = "row", Expr = "ROUND(AMOUNT, 0) >= 9000",
                    Style = new HighlightStyle { Bg = "#fee2e2" },
                },
                new HighlightRule
                {
                    Id = "acme", Scope = "cell", Col = "CUSTOMER",
                    Expr = "CONTAINS(CUSTOMER, 'ACME')",
                    Style = new HighlightStyle { Bg = "#fef3c7" },
                },
            ],
        }, NoParams);

        Assert.Equal(2, result.Highlights.Count(hit => hit.Id == "large"));
        Assert.Equal(3, result.Highlights.Count(hit => hit.Id == "acme"));
        Assert.All(result.Highlights.Where(hit => hit.Id == "acme"),
            hit => Assert.Equal("CUSTOMER", hit.Col, ignoreCase: true));
        Assert.All(result.Rows, row =>
            Assert.DoesNotContain(row.Keys, key => key.StartsWith("__ir_highlight_", StringComparison.OrdinalIgnoreCase)));
    }

    [SkippableTheory]
    [MemberData(nameof(Dialects))]
    public async Task Blank_counts_converge_across_dialects(ReportDialect dialect)
    {
        var live = LiveDb.For(dialect);
        var result = await live.Executor.Query(live.Definition(), new ReportState
        {
            Filters = [Filter("NOTES IS NULL OR NOTES = ''")],
        }, NoParams);

        Assert.Equal(4, result.TotalRows);
    }

    [SkippableTheory]
    [MemberData(nameof(Dialects))]
    public async Task Between_and_in_compose(ReportDialect dialect)
    {
        var live = LiveDb.For(dialect);
        var result = await live.Executor.Query(live.Definition(), new ReportState
        {
            Filters =
            [
                Filter("AMOUNT BETWEEN 1000 AND 8000"),
                Filter("IN_LIST(STATUS, 'SHIPPED', 'PENDING')"),
            ],
        }, NoParams);

        Assert.Equal(5, result.TotalRows);
    }

    [SkippableTheory]
    [MemberData(nameof(Dialects))]
    public async Task Aggregates_are_exact(ReportDialect dialect)
    {
        var live = LiveDb.For(dialect);
        var result = await live.Executor.Query(live.Definition(), new ReportState
        {
            Filters = [Filter("STATUS = 'SHIPPED'")],
            Aggregates =
            [
                new AggregateRule { Col = "AMOUNT", Fn = AggregateFn.Sum },
                new AggregateRule { Col = "AMOUNT", Fn = AggregateFn.Avg },
                new AggregateRule { Col = "CUSTOMER", Fn = AggregateFn.CountDistinct },
            ],
        }, NoParams);

        var amount = result.Aggregates["AMOUNT"];
        Assert.Equal(26000m, Convert.ToDecimal(amount["sum"]));
        Assert.Equal(5200m, Convert.ToDecimal(amount["avg"]));   // SqlServer AVG float-cast path
        Assert.Equal(4L, Convert.ToInt64(result.Aggregates["CUSTOMER"]["countDistinct"]));
    }

    [SkippableTheory]
    [MemberData(nameof(Dialects))]
    public async Task Breaks_group_and_total(ReportDialect dialect)
    {
        var live = LiveDb.For(dialect);
        var result = await live.Executor.Query(live.Definition(), new ReportState
        {
            Breaks = ["STATUS"],
            Aggregates = [new AggregateRule { Col = "AMOUNT", Fn = AggregateFn.Sum }],
        }, NoParams);

        Assert.Equal(4, result.BreakTotals.Count);
        Assert.Equal([1L, 1L, 3L, 5L], result.BreakTotals.Select(b => b.Rows));
        Assert.Equal(
            [6000m, 400m, 14800m, 26000m],
            result.BreakTotals.Select(b => Convert.ToDecimal(b.Aggregates["AMOUNT"]["sum"])));
    }

    [SkippableTheory]
    [MemberData(nameof(Dialects))]
    public async Task Computed_columns_filter_sort_and_concatenate(ReportDialect dialect)
    {
        var live = LiveDb.For(dialect);
        var result = await live.Executor.Query(live.Definition(), new ReportState
        {
            Computed =
            [
                new ComputedColumn { Id = "c1", Expr = "ROUND(AMOUNT * 2, 0)" },
                new ComputedColumn { Id = "c2", Expr = "UPPER(CUSTOMER) || '!'" },
            ],
            Filters = [Filter("c1 >= 10000")],
            Sorts = [new SortRule { Col = "c1", Dir = SortDir.Desc }],
        }, NoParams);

        Assert.Equal(5, result.TotalRows);
        Assert.Equal(24000m, Convert.ToDecimal(result.Rows[0]["c1"]));
        Assert.Equal("STARK IND!", result.Rows[0]["c2"]);
    }

    [SkippableTheory]
    [MemberData(nameof(Dialects))]
    public async Task Case_computed_column_filters_and_aggregates(ReportDialect dialect)
    {
        var live = LiveDb.For(dialect);
        var result = await live.Executor.Query(live.Definition(), new ReportState
        {
            Computed =
            [
                new ComputedColumn
                {
                    Id = "c1",
                    Expr = "CASE WHEN AMOUNT >= 6000 THEN 'BIG' WHEN AMOUNT >= 2000 THEN 'MID' ELSE 'SMALL' END",
                },
                new ComputedColumn
                {
                    Id = "c2",
                    Expr = "CASE WHEN NOTES IS NULL THEN 0 ELSE 1 END",
                },
            ],
            Filters = [Filter("c1 = 'BIG'")],
            Sorts = [new SortRule { Col = "AMOUNT", Dir = SortDir.Desc }],
            Aggregates = [new AggregateRule { Col = "c2", Fn = AggregateFn.Sum }],
        }, NoParams);

        // BIG = amounts ≥ 6000: Stark 12000, Acme 9000, Globex 7500, Tyrell 6000.
        Assert.Equal(4, result.TotalRows);
        Assert.All(result.Rows, r => Assert.Equal("BIG", r["c1"]));
        // Of the BIG rows, those with a real note: insured, rush, refunded.
        // Globex's NOTES is NULL. Empty-string parity is covered separately below.
        Assert.Equal(3m, Convert.ToDecimal(result.Aggregates["c2"]["sum"]));
    }

    [SkippableTheory]
    [MemberData(nameof(Dialects))]
    public async Task Case_blank_condition_exercises_null_and_empty_string_rows(ReportDialect dialect)
    {
        var live = LiveDb.For(dialect);
        var result = await live.Executor.Query(live.Definition(), new ReportState
        {
            Computed =
            [
                new ComputedColumn
                {
                    Id = "c1",
                    Expr = "CASE WHEN NOTES IS NULL OR NOTES = '' THEN 1 ELSE 0 END",
                },
            ],
            Aggregates = [new AggregateRule { Col = "c1", Fn = AggregateFn.Sum }],
        }, NoParams);

        // SQL Server preserves the seeded empty string; Oracle stores it as NULL.
        // The explicit portable blank condition must count the same four rows on both.
        Assert.Equal(4m, Convert.ToDecimal(result.Aggregates["c1"]["sum"]));
    }

    [SkippableFact]
    public async Task Boolean_column_condition_executes_on_sqlserver()
    {
        var live = LiveDb.For(ReportDialect.SqlServer);
        var def = live.Definition();
        def.Name = "live-SqlServer-bool-expression";
        def.Sql = """
            SELECT ORDER_ID, CUSTOMER, STATUS, AMOUNT, NOTES,
                   CAST(CASE WHEN AMOUNT >= 5000 THEN 1 ELSE 0 END AS bit) AS LARGE_FLAG
            FROM IR_TEST_ORDERS
            """;

        var result = await live.Executor.Query(def, new ReportState
        {
            Computed =
            [
                new ComputedColumn
                {
                    Id = "c1",
                    Expr = "CASE WHEN LARGE_FLAG THEN 1 ELSE 0 END",
                },
            ],
            Aggregates = [new AggregateRule { Col = "c1", Fn = AggregateFn.Sum }],
        }, NoParams);

        Assert.Equal(5m, Convert.ToDecimal(result.Aggregates["c1"]["sum"]));
    }

    [SkippableFact]
    public async Task Boolean_column_condition_executes_on_postgres()
    {
        // Postgres has REAL booleans: the same expression that lowers to "= 1" on
        // SQL Server must emit the column bare here (boolean = integer is a type
        // error in Postgres) — the two live boolean tests pin both sides.
        var live = LiveDb.For(ReportDialect.Postgres);
        var def = live.Definition();
        def.Name = "live-Postgres-bool-expression";
        def.Sql = """
            SELECT ORDER_ID, CUSTOMER, STATUS, AMOUNT, NOTES,
                   (AMOUNT >= 5000) AS LARGE_FLAG
            FROM IR_TEST_ORDERS
            """;

        var result = await live.Executor.Query(def, new ReportState
        {
            Computed =
            [
                new ComputedColumn { Id = "c1", Expr = "CASE WHEN LARGE_FLAG THEN 1 ELSE 0 END" },
            ],
            Aggregates = [new AggregateRule { Col = "c1", Fn = AggregateFn.Sum }],
        }, NoParams);

        Assert.Equal(5m, Convert.ToDecimal(result.Aggregates["c1"]["sum"]));
    }

    [SkippableTheory]
    [MemberData(nameof(Dialects))]
    public async Task Date_parts_extract_from_native_and_iso_text_dates(ReportDialect dialect)
    {
        // EXTRACT is strictly typed on Oracle/Postgres: the text column exercises the
        // per-dialect conversions the emitter wraps around ISO date text.
        var live = LiveDb.For(dialect);
        var def = live.Definition();
        def.Name = $"live-dates-{dialect}";
        def.Sql = "SELECT ORDER_ID, AMOUNT, ORDER_DATE, ORDER_DATE_TEXT FROM IR_TEST_ORDERS";

        var result = await live.Executor.Query(def, new ReportState
        {
            Computed =
            [
                new ComputedColumn { Id = "c1", Expr = "YEAR(ORDER_DATE)" },
                new ComputedColumn { Id = "c2", Expr = "YEAR(ORDER_DATE_TEXT)" },
                new ComputedColumn { Id = "c3", Expr = "MONTH(ORDER_DATE_TEXT)" },
                new ComputedColumn { Id = "c4", Expr = "DAY(ORDER_DATE)" },
            ],
            Filters = [Filter("c1 = 2026")],
            Sorts = [new SortRule { Col = "ORDER_ID", Dir = SortDir.Asc }],
        }, NoParams);

        // 2026 rows: ids 6–10.
        Assert.Equal(5, result.TotalRows);
        Assert.All(result.Rows, r =>
            Assert.Equal(Convert.ToInt32(r["c1"]), Convert.ToInt32(r["c2"])));   // text agrees with native
        var first = result.Rows[0];                                              // id 6 → 2026-02-16
        Assert.Equal(6, Convert.ToInt32(first["ORDER_ID"]));
        Assert.Equal(2, Convert.ToInt32(first["c3"]));
        Assert.Equal(16, Convert.ToInt32(first["c4"]));
    }

    [SkippableTheory]
    [MemberData(nameof(Dialects))]
    public async Task Date_vocabulary_agrees_across_engines(ReportDialect dialect)
    {
        // The decided date design (ARCHITECTURE §8): NOW/TO_DATE/DATE_TRUNC/TO_STRING,
        // whole-day arithmetic, plain SQL comparisons, inclusive BETWEEN. Bounds are
        // fixed dates so counts are clock-independent; only c6 needs a sane engine
        // clock (anything past 2020).
        var live = LiveDb.For(dialect);
        var def = live.Definition();
        def.Name = $"live-date-vocab-{dialect}";
        def.Sql = "SELECT ORDER_ID, AMOUNT, ORDER_DATE, ORDER_DATE_TEXT FROM IR_TEST_ORDERS";

        var result = await live.Executor.Query(def, new ReportState
        {
            Computed =
            [
                // Inclusive 2026 window over the native date column: ids 6–10.
                new ComputedColumn { Id = "c1", Expr = "CASE WHEN ORDER_DATE BETWEEN TO_DATE('2026-01-01') AND TO_DATE('2026-12-31') THEN 1 ELSE 0 END" },
                // Text→date conversion agrees with the native date at day granularity.
                new ComputedColumn { Id = "c2", Expr = "CASE WHEN TO_DATE(ORDER_DATE_TEXT) = DATE_TRUNC('DAY', ORDER_DATE) THEN 1 ELSE 0 END" },
                // Whole-day arithmetic: the shifted date passes the original.
                new ComputedColumn { Id = "c3", Expr = "CASE WHEN TO_DATE(ORDER_DATE_TEXT) + 1 > ORDER_DATE THEN 1 ELSE 0 END" },
                // Format translation parity: YYYY-MM equals the ISO text prefix.
                new ComputedColumn { Id = "c4", Expr = "TO_STRING(ORDER_DATE, 'YYYY-MM')" },
                // Feb 2026 via month truncation: ids 6, 7, 10.
                new ComputedColumn { Id = "c5", Expr = "CASE WHEN DATE_TRUNC('MONTH', ORDER_DATE) = TO_DATE('2026-02-01') THEN 1 ELSE 0 END" },
                // NOW() on the engine clock, exercised through BETWEEN + arithmetic.
                new ComputedColumn { Id = "c6", Expr = "CASE WHEN NOW() BETWEEN TO_DATE('2020-01-01') AND NOW() + 1 THEN 1 ELSE 0 END" },
                // NULL keeps its Date type through producers and arithmetic: with a
                // bare NULL, Oracle typed the sum as NUMBER and Postgres either made
                // an INTERVAL of it or could not resolve date_trunc at all.
                new ComputedColumn { Id = "c7", Expr = "CASE WHEN TO_DATE(NULL) + 1 IS NULL AND DATE_TRUNC('DAY', TO_DATE(NULL)) IS NULL THEN 1 ELSE 0 END" },
            ],
            Aggregates =
            [
                new AggregateRule { Col = "c1", Fn = AggregateFn.Sum },
                new AggregateRule { Col = "c2", Fn = AggregateFn.Sum },
                new AggregateRule { Col = "c3", Fn = AggregateFn.Sum },
                new AggregateRule { Col = "c5", Fn = AggregateFn.Sum },
                new AggregateRule { Col = "c6", Fn = AggregateFn.Sum },
                new AggregateRule { Col = "c7", Fn = AggregateFn.Sum },
            ],
            Sorts = [new SortRule { Col = "ORDER_ID", Dir = SortDir.Asc }],
        }, NoParams);

        Assert.Equal(5m, Convert.ToDecimal(result.Aggregates["c1"]["sum"]));
        Assert.Equal(10m, Convert.ToDecimal(result.Aggregates["c2"]["sum"]));
        Assert.Equal(10m, Convert.ToDecimal(result.Aggregates["c3"]["sum"]));
        Assert.Equal(3m, Convert.ToDecimal(result.Aggregates["c5"]["sum"]));
        Assert.Equal(10m, Convert.ToDecimal(result.Aggregates["c6"]["sum"]));
        Assert.Equal(10m, Convert.ToDecimal(result.Aggregates["c7"]["sum"]));
        Assert.All(result.Rows, r =>
            Assert.Equal(((string)r["ORDER_DATE_TEXT"]!)[..7], (string)r["c4"]!));
    }

    [SkippableTheory]
    [MemberData(nameof(Dialects))]
    public async Task Definition_timezone_pins_the_session_where_one_exists(ReportDialect dialect)
    {
        // def.TimeZone pins the session on engines that have session timezones
        // (Oracle ALTER SESSION, Postgres SET TIME ZONE) so NOW() follows it; on
        // SQL Server the configured value is deliberately ignored — the query must
        // simply run as if the setting were absent.
        var live = LiveDb.For(dialect);
        var def = live.Definition();
        def.Name = $"live-tz-{dialect}";
        def.TimeZone = "Pacific/Auckland";
        def.Sql = dialect switch
        {
            ReportDialect.Oracle => "SELECT SESSIONTIMEZONE AS TZ FROM DUAL",
            ReportDialect.Postgres => "SELECT current_setting('TimeZone') AS \"TZ\"",
            _ => "SELECT ORDER_ID FROM IR_TEST_ORDERS",
        };

        var result = await live.Executor.Query(def, new ReportState(), NoParams);

        if (dialect == ReportDialect.SqlServer)
            Assert.Equal(10, result.TotalRows);
        else
            Assert.Equal("Pacific/Auckland", (string)result.Rows.Single()["TZ"]!);
    }

    [SkippableTheory]
    [MemberData(nameof(Dialects))]
    public async Task Context_params_bind_by_name(ReportDialect dialect)
    {
        // Context param appears FIRST in the SQL but is added LAST by CommandBuilder —
        // this is the BindByName regression trap on Oracle.
        var live = LiveDb.For(dialect);
        var def = live.Definition();
        def.Name = $"live-ctx-{dialect}";
        var marker = dialect == ReportDialect.Oracle ? ":minAmount" : "@minAmount";
        def.Sql = $"SELECT ORDER_ID, CUSTOMER, STATUS, AMOUNT, NOTES FROM IR_TEST_ORDERS WHERE AMOUNT >= {marker}";

        var result = await live.Executor.Query(def, new ReportState
        {
            Filters = [Filter("STATUS = 'SHIPPED'")],
        }, new Dictionary<string, object?> { ["minAmount"] = 5000m });

        Assert.Equal(3, result.TotalRows);   // SHIPPED with AMOUNT >= 5000: 9000, 7500, 5000
    }

    [SkippableTheory]
    [MemberData(nameof(Dialects))]
    public async Task GroupBy_and_pivot_views(ReportDialect dialect)
    {
        var live = LiveDb.For(dialect);

        var grouped = await live.Executor.Query(live.Definition(), new ReportState
        {
            View = new ViewSpec
            {
                Mode = "groupBy",
                GroupBy = ["STATUS"],
                Values = [new AggregateRule { Col = "AMOUNT", Fn = AggregateFn.Sum }],
            },
        }, NoParams);
        Assert.Equal([1L, 1L, 3L, 5L], grouped.Rows.Select(r => Convert.ToInt64(r["__count"])));

        var pivot = await live.Executor.Query(live.Definition(), new ReportState
        {
            View = new ViewSpec
            {
                Mode = "pivot",
                Rows = ["CUSTOMER"],
                Cols = ["STATUS"],
                Values = [new AggregateRule { Col = "AMOUNT", Fn = AggregateFn.Sum }],
            },
        }, NoParams);
        var acme = pivot.Rows.Single(r => (string?)r["CUSTOMER"] == "Acme Corp");
        Assert.Equal(12000m, Convert.ToDecimal(acme["p3_0"]));
    }

    [SkippableTheory]
    [MemberData(nameof(Dialects))]
    public async Task Export_truncates_at_max_rows(ReportDialect dialect)
    {
        var live = LiveDb.For(dialect);
        var def = live.Definition();
        def.Name = $"live-export-{dialect}";
        def.MaxRows = 3;

        var export = await live.Executor.Export(def, new ReportState
        {
            Sorts = [new SortRule { Col = "AMOUNT", Dir = SortDir.Desc }],
        }, NoParams);

        Assert.True(export.Truncated);
        Assert.Equal(3, export.Rows.Count);
    }
}

/// <summary>
/// One lazily-seeded database per dialect per test run. Skips (never fails) when the
/// environment variable is absent.
/// </summary>
internal sealed class LiveDb : IReportConnectionFactory
{
    private static readonly ConcurrentDictionary<ReportDialect, Lazy<LiveDb>> Instances = new();

    private readonly ReportDialect _dialect;
    private readonly string _connectionString;
    public ReportExecutor Executor { get; }

    public static LiveDb For(ReportDialect dialect)
    {
        var env = dialect switch
        {
            ReportDialect.SqlServer => "IR_TEST_SQLSERVER",
            ReportDialect.Oracle => "IR_TEST_ORACLE",
            ReportDialect.Postgres => "IR_TEST_POSTGRES",
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null),
        };
        var cs = Environment.GetEnvironmentVariable(env);
        Skip.If(string.IsNullOrWhiteSpace(cs), $"set {env} to run live {dialect} verification");

        return Instances.GetOrAdd(dialect, d => new Lazy<LiveDb>(() => new LiveDb(d, cs!))).Value;
    }

    private LiveDb(ReportDialect dialect, string connectionString)
    {
        _dialect = dialect;
        _connectionString = connectionString;
        Executor = new ReportExecutor(this, new SchemaCache());
        Seed();
    }

    public DbConnection CreateConnection(string name) => _dialect switch
    {
        ReportDialect.SqlServer => new SqlConnection(_connectionString),
        ReportDialect.Oracle => new OracleConnection(_connectionString),
        ReportDialect.Postgres => new NpgsqlConnection(_connectionString),
        _ => throw new ArgumentOutOfRangeException(nameof(_dialect), _dialect, null),
    };

    public ReportDefinition Definition() => new()
    {
        Name = $"live-{_dialect}",
        Connection = "live",
        Dialect = _dialect,
        Sql = "SELECT ORDER_ID, CUSTOMER, STATUS, AMOUNT, NOTES FROM IR_TEST_ORDERS",
    };

    private void Seed()
    {
        using var conn = CreateConnection("live");
        conn.Open();

        Execute(conn, _dialect switch
        {
            ReportDialect.SqlServer => "IF OBJECT_ID('IR_TEST_ORDERS', 'U') IS NOT NULL DROP TABLE IR_TEST_ORDERS",
            ReportDialect.Postgres => "DROP TABLE IF EXISTS IR_TEST_ORDERS",
            _ => """
                 BEGIN
                     EXECUTE IMMEDIATE 'DROP TABLE IR_TEST_ORDERS';
                 EXCEPTION WHEN OTHERS THEN
                     IF SQLCODE != -942 THEN RAISE; END IF;
                 END;
                 """,
        });

        Execute(conn, _dialect switch
        {
            ReportDialect.SqlServer => """
                CREATE TABLE IR_TEST_ORDERS (
                    ORDER_ID INT PRIMARY KEY,
                    CUSTOMER NVARCHAR(100) NOT NULL,
                    STATUS   NVARCHAR(20) NOT NULL,
                    AMOUNT   DECIMAL(12,2) NOT NULL,
                    NOTES    NVARCHAR(200) NULL,
                    ORDER_DATE DATE NOT NULL,
                    ORDER_DATE_TEXT NVARCHAR(10) NOT NULL
                )
                """,
            // Unquoted on purpose: names fold to lowercase, matching the unquoted
            // identifiers in the definition's base SELECT.
            ReportDialect.Postgres => """
                CREATE TABLE IR_TEST_ORDERS (
                    ORDER_ID INT PRIMARY KEY,
                    CUSTOMER VARCHAR(100) NOT NULL,
                    STATUS   VARCHAR(20) NOT NULL,
                    AMOUNT   NUMERIC(12,2) NOT NULL,
                    NOTES    VARCHAR(200) NULL,
                    ORDER_DATE DATE NOT NULL,
                    ORDER_DATE_TEXT VARCHAR(10) NOT NULL
                )
                """,
            _ => """
                CREATE TABLE IR_TEST_ORDERS (
                    ORDER_ID NUMBER(10) PRIMARY KEY,
                    CUSTOMER VARCHAR2(100) NOT NULL,
                    STATUS   VARCHAR2(20) NOT NULL,
                    AMOUNT   NUMBER(12,2) NOT NULL,
                    NOTES    VARCHAR2(200) NULL,
                    ORDER_DATE DATE NOT NULL,
                    ORDER_DATE_TEXT VARCHAR2(10) NOT NULL
                )
                """,
        });

        // The canonical 10 rows — must match SqliteE2EFixture. On Oracle the ''
        // note becomes NULL at insert, which is exactly the semantic the blank
        // operator's dialect handling accounts for. ORDER_DATE_TEXT is the same
        // date as ISO text, for the date-part-on-text expression coverage.
        var rows = new (int Id, string Customer, string Status, decimal Amount, string? Notes, string Date)[]
        {
            (1, "Acme Corp", "SHIPPED", 9000m, "rush", "2025-11-03"),
            (2, "Globex", "SHIPPED", 7500m, null, "2025-07-21"),
            (3, "Initech", "SHIPPED", 5000m, "", "2025-03-09"),
            (4, "Acme Corp", "SHIPPED", 3000m, "fragile", "2025-12-30"),
            (5, "Hooli", "SHIPPED", 1500m, null, "2025-05-14"),
            (6, "acme llc", "PENDING", 2000m, "call first", "2026-02-16"),
            (7, "Umbrella", "PENDING", 800m, null, "2026-02-08"),
            (8, "Stark Ind", "PENDING", 12000m, "insured", "2026-06-27"),
            (9, "Wayne Ent", "NEW", 400m, "standard", "2026-04-01"),
            (10, "Tyrell Corp", "CANCELLED", 6000m, "refunded", "2026-02-19"),
        };

        var prefix = _dialect == ReportDialect.SqlServer ? "@" : ":";
        foreach (var r in rows)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                $"INSERT INTO IR_TEST_ORDERS (ORDER_ID, CUSTOMER, STATUS, AMOUNT, NOTES, ORDER_DATE, ORDER_DATE_TEXT) " +
                $"VALUES ({prefix}id, {prefix}customer, {prefix}status, {prefix}amount, {prefix}notes, {prefix}orderDate, {prefix}orderDateText)";
            AddParam(cmd, "id", r.Id);
            AddParam(cmd, "customer", r.Customer);
            AddParam(cmd, "status", r.Status);
            AddParam(cmd, "amount", r.Amount);
            AddParam(cmd, "notes", (object?)r.Notes ?? DBNull.Value);
            AddParam(cmd, "orderDate", DateTime.ParseExact(r.Date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
            AddParam(cmd, "orderDateText", r.Date);
            cmd.ExecuteNonQuery();
        }
    }

    private static void Execute(DbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void AddParam(DbCommand cmd, string name, object value)
    {
        if (cmd is OracleCommand oracle) oracle.BindByName = true;
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
