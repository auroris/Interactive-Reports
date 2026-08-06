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
            Filters = [Filter("STATUS", FilterOp.Eq, "SHIPPED")],
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
    public async Task Blank_counts_converge_across_dialects(ReportDialect dialect)
    {
        var live = LiveDb.For(dialect);
        var result = await live.Executor.Query(live.Definition(), new ReportState
        {
            Filters = [Filter("NOTES", FilterOp.Blank)],
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
                Filter("AMOUNT", FilterOp.Between, new[] { 1000, 8000 }),
                Filter("STATUS", FilterOp.In, new[] { "SHIPPED", "PENDING" }),
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
            Filters = [Filter("STATUS", FilterOp.Eq, "SHIPPED")],
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
            Filters = [Filter("c1", FilterOp.Ge, 10000)],
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
            Filters = [Filter("c1", FilterOp.Eq, "BIG")],
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
            Filters = [Filter("c1", FilterOp.Eq, 2026)],
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
    [InlineData(ReportDialect.SqlServer)]
    [InlineData(ReportDialect.Postgres)]
    public async Task Uuid_filters_bind_native_uuid_values(ReportDialect dialect)
    {
        // Postgres rejects uuid = text outright, so the validator binds Guid columns
        // as Guids. The uuid column is derived deterministically per row so the test
        // needs no schema change; values are read back and re-sent as the JSON
        // strings a client would put in the state document.
        var live = LiveDb.For(dialect);
        var def = live.Definition();
        def.Name = $"live-uuid-{dialect}";
        def.Sql = dialect == ReportDialect.Postgres
            ? "SELECT ORDER_ID, CUSTOMER, CAST(MD5(CAST(ORDER_ID AS TEXT)) AS UUID) AS ROW_UUID FROM IR_TEST_ORDERS"
            : "SELECT ORDER_ID, CUSTOMER, CONVERT(UNIQUEIDENTIFIER, HASHBYTES('MD5', CAST(ORDER_ID AS VARCHAR(10)))) AS ROW_UUID FROM IR_TEST_ORDERS";

        var all = await live.Executor.Query(def, new ReportState
        {
            Sorts = [new SortRule { Col = "ORDER_ID", Dir = SortDir.Asc }],
        }, NoParams);
        var uuidOf = (int index) => ((Guid)all.Rows[index]["ROW_UUID"]!).ToString();

        var eq = await live.Executor.Query(def, new ReportState
        {
            Filters = [Filter("ROW_UUID", FilterOp.Eq, uuidOf(0))],
        }, NoParams);
        Assert.Equal(1, eq.TotalRows);
        Assert.Equal(1, Convert.ToInt32(eq.Rows.Single()["ORDER_ID"]));

        var ne = await live.Executor.Query(def, new ReportState
        {
            Filters = [Filter("ROW_UUID", FilterOp.Ne, uuidOf(0))],
        }, NoParams);
        Assert.Equal(9, ne.TotalRows);

        var inList = await live.Executor.Query(def, new ReportState
        {
            Filters = [Filter("ROW_UUID", FilterOp.In, new[] { uuidOf(0), uuidOf(1) })],
        }, NoParams);
        Assert.Equal(2, inList.TotalRows);
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
            Filters = [Filter("STATUS", FilterOp.Eq, "SHIPPED")],
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
