using System.Collections.Concurrent;
using System.Data.Common;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Schema;
using Microsoft.Data.SqlClient;
using Oracle.ManagedDataAccess.Client;
using static InteractiveReport.Core.Tests.TestFixtures;

namespace InteractiveReport.Core.Tests;

/// <summary>
/// The M5 verification pass: the same engine corpus the SQLite e2e suite locks, executed
/// against real SQL Server and Oracle instances. Skipped unless the environment provides
/// connection strings:
///
///   IR_TEST_SQLSERVER  e.g. Server=vm;Database=irtest;User Id=irtest;Password=...;TrustServerCertificate=True
///   IR_TEST_ORACLE     e.g. User Id=irtest;Password=...;Data Source=vm:1521/XEPDB1
///
/// Each run drops and recreates a table named IR_TEST_ORDERS in that database and seeds
/// the canonical 10 rows (see docs/TESTING.md). Expected numbers are identical across
/// all dialects — including blank-count 4, which converges by design: SQLite/SqlServer
/// count 3 NULLs + 1 empty string; Oracle turns the empty string into a 4th NULL.
/// </summary>
public class LiveDialectTests
{
    public static TheoryData<ReportDialect> Dialects => new() { ReportDialect.SqlServer, ReportDialect.Oracle };

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
                    Expr = "CASE WHEN NOTES IS NULL OR NOTES = '' THEN 0 ELSE 1 END",
                },
            ],
            Filters = [Filter("c1", FilterOp.Eq, "BIG")],
            Sorts = [new SortRule { Col = "AMOUNT", Dir = SortDir.Desc }],
            Aggregates = [new AggregateRule { Col = "c2", Fn = AggregateFn.Sum }],
        }, NoParams);

        // BIG = amounts ≥ 6000: Stark 12000, Acme 9000, Globex 7500, Tyrell 6000.
        Assert.Equal(4, result.TotalRows);
        Assert.All(result.Rows, r => Assert.Equal("BIG", r["c1"]));
        // Of the BIG rows, those with a real note: insured, rush, refunded
        // (Globex's NOTES is NULL; the '' arm keeps the expression honest on
        // dialects where empty string and NULL differ).
        Assert.Equal(3m, Convert.ToDecimal(result.Aggregates["c2"]["sum"]));
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
        var env = dialect == ReportDialect.SqlServer ? "IR_TEST_SQLSERVER" : "IR_TEST_ORACLE";
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

    public DbConnection CreateConnection(string name) => _dialect == ReportDialect.SqlServer
        ? new SqlConnection(_connectionString)
        : new OracleConnection(_connectionString);

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

        Execute(conn, _dialect == ReportDialect.SqlServer
            ? "IF OBJECT_ID('IR_TEST_ORDERS', 'U') IS NOT NULL DROP TABLE IR_TEST_ORDERS"
            : """
              BEGIN
                  EXECUTE IMMEDIATE 'DROP TABLE IR_TEST_ORDERS';
              EXCEPTION WHEN OTHERS THEN
                  IF SQLCODE != -942 THEN RAISE; END IF;
              END;
              """);

        Execute(conn, _dialect == ReportDialect.SqlServer
            ? """
              CREATE TABLE IR_TEST_ORDERS (
                  ORDER_ID INT PRIMARY KEY,
                  CUSTOMER NVARCHAR(100) NOT NULL,
                  STATUS   NVARCHAR(20) NOT NULL,
                  AMOUNT   DECIMAL(12,2) NOT NULL,
                  NOTES    NVARCHAR(200) NULL
              )
              """
            : """
              CREATE TABLE IR_TEST_ORDERS (
                  ORDER_ID NUMBER(10) PRIMARY KEY,
                  CUSTOMER VARCHAR2(100) NOT NULL,
                  STATUS   VARCHAR2(20) NOT NULL,
                  AMOUNT   NUMBER(12,2) NOT NULL,
                  NOTES    VARCHAR2(200) NULL
              )
              """);

        // The canonical 10 rows — must match SqliteE2EFixture. On Oracle the ''
        // note becomes NULL at insert, which is exactly the semantic the blank
        // operator's dialect handling accounts for.
        var rows = new (int Id, string Customer, string Status, decimal Amount, string? Notes)[]
        {
            (1, "Acme Corp", "SHIPPED", 9000m, "rush"),
            (2, "Globex", "SHIPPED", 7500m, null),
            (3, "Initech", "SHIPPED", 5000m, ""),
            (4, "Acme Corp", "SHIPPED", 3000m, "fragile"),
            (5, "Hooli", "SHIPPED", 1500m, null),
            (6, "acme llc", "PENDING", 2000m, "call first"),
            (7, "Umbrella", "PENDING", 800m, null),
            (8, "Stark Ind", "PENDING", 12000m, "insured"),
            (9, "Wayne Ent", "NEW", 400m, "standard"),
            (10, "Tyrell Corp", "CANCELLED", 6000m, "refunded"),
        };

        var prefix = _dialect == ReportDialect.SqlServer ? "@" : ":";
        foreach (var r in rows)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                $"INSERT INTO IR_TEST_ORDERS (ORDER_ID, CUSTOMER, STATUS, AMOUNT, NOTES) " +
                $"VALUES ({prefix}id, {prefix}customer, {prefix}status, {prefix}amount, {prefix}notes)";
            AddParam(cmd, "id", r.Id);
            AddParam(cmd, "customer", r.Customer);
            AddParam(cmd, "status", r.Status);
            AddParam(cmd, "amount", r.Amount);
            AddParam(cmd, "notes", (object?)r.Notes ?? DBNull.Value);
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
