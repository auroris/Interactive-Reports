using System.Data.Common;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Schema;
using Microsoft.Data.Sqlite;
using static InteractiveReport.Core.Tests.TestFixtures;

namespace InteractiveReport.Core.Tests;

/// <summary>
/// Full engine pass — discover, validate, compose, execute — against a real (in-memory,
/// shared-cache) SQLite database with hand-written rows, so assertions are exact.
/// </summary>
public sealed class SqliteEndToEndTests : IClassFixture<SqliteE2EFixture>
{
    private readonly SqliteE2EFixture _db;
    private readonly ReportExecutor _executor;

    public SqliteEndToEndTests(SqliteE2EFixture db)
    {
        _db = db;
        _executor = new ReportExecutor(db, new SchemaCache());
    }

    private static readonly IReadOnlyDictionary<string, object?> NoParams = new Dictionary<string, object?>();

    private ReportDefinition Definition => new()
    {
        Name = "orders-e2e",
        Connection = "E2E",
        Dialect = ReportDialect.Sqlite,
        Sql = "SELECT ORDER_ID, CUSTOMER, STATUS, AMOUNT, NOTES FROM ORDERS",
    };

    [Fact]
    public async Task Schema_is_discovered_from_the_probe()
    {
        var schema = await _executor.GetSchema(Definition, NoParams);

        Assert.Equal(["ORDER_ID", "CUSTOMER", "STATUS", "AMOUNT", "NOTES"], schema.Select(c => c.Name));
        Assert.Equal(ColumnKind.Number, schema.Single(c => c.Name == "AMOUNT").Kind);
        Assert.Equal(ColumnKind.Text, schema.Single(c => c.Name == "CUSTOMER").Kind);
    }

    [Fact]
    public async Task Filter_sort_page_end_to_end()
    {
        var result = await _executor.Query(Definition, new ReportState
        {
            Filters = [Filter("STATUS", FilterOp.Eq, "SHIPPED")],
            Sorts = [new SortRule { Col = "AMOUNT", Dir = SortDir.Desc }],
            Page = new PageRequest { Index = 1, Size = 3 },
        }, NoParams);

        Assert.Equal(5, result.TotalRows);                       // 5 SHIPPED rows seeded
        Assert.Equal(3, result.Rows.Count);                      // but only a page of 3
        var amounts = result.Rows.Select(r => Convert.ToDecimal(r["AMOUNT"])).ToArray();
        Assert.Equal([9000m, 7500m, 5000m], amounts);            // descending, from the top
    }

    [Fact]
    public async Task Second_page_continues_the_ordering()
    {
        var state = new ReportState
        {
            Filters = [Filter("STATUS", FilterOp.Eq, "SHIPPED")],
            Sorts = [new SortRule { Col = "AMOUNT", Dir = SortDir.Desc }],
            Page = new PageRequest { Index = 2, Size = 3 },
        };

        var result = await _executor.Query(Definition, state, NoParams);

        Assert.Equal(5, result.TotalRows);
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal([3000m, 1500m], result.Rows.Select(r => Convert.ToDecimal(r["AMOUNT"])));
    }

    [Fact]
    public async Task Search_is_case_insensitive_across_text_columns()
    {
        var result = await _executor.Query(Definition, new ReportState { Search = "ACME" }, NoParams);

        // 'Acme Corp' ×2 and 'acme llc' ×1
        Assert.Equal(3, result.TotalRows);
    }

    [Fact]
    public async Task Blank_matches_null_and_empty_string_on_sqlite()
    {
        var result = await _executor.Query(Definition, new ReportState
        {
            Filters = [Filter("NOTES", FilterOp.Blank)],
        }, NoParams);

        Assert.Equal(4, result.TotalRows);                       // 3 NULL + 1 ''
    }

    [Fact]
    public async Task Unknown_column_in_saved_state_degrades_to_ignored()
    {
        var result = await _executor.Query(Definition, new ReportState
        {
            Filters = [Filter("REMOVED_COLUMN", FilterOp.Eq, "x")],
        }, NoParams);

        Assert.Equal(10, result.TotalRows);                      // filter dropped, all rows
        Assert.Contains(result.Ignored, i => i.Kind == "filter" && i.Detail.Contains("REMOVED_COLUMN"));
    }

    [Fact]
    public async Task Between_and_in_compose_together()
    {
        var result = await _executor.Query(Definition, new ReportState
        {
            Filters =
            [
                Filter("AMOUNT", FilterOp.Between, new[] { 1000, 8000 }),
                Filter("STATUS", FilterOp.In, new[] { "SHIPPED", "PENDING" }),
            ],
        }, NoParams);

        // SHIPPED: 7500, 5000, 3000, 1500 in range; PENDING: 2000 in range
        Assert.Equal(5, result.TotalRows);
    }

    [Fact]
    public async Task Aggregates_cover_the_whole_filtered_set_not_the_page()
    {
        var result = await _executor.Query(Definition, new ReportState
        {
            Filters = [Filter("STATUS", FilterOp.Eq, "SHIPPED")],
            Page = new PageRequest { Index = 1, Size = 2 },
            Aggregates =
            [
                new AggregateRule { Col = "AMOUNT", Fn = AggregateFn.Sum },
                new AggregateRule { Col = "AMOUNT", Fn = AggregateFn.Avg },
                new AggregateRule { Col = "AMOUNT", Fn = AggregateFn.Min },
                new AggregateRule { Col = "AMOUNT", Fn = AggregateFn.Max },
            ],
        }, NoParams);

        Assert.Equal(2, result.Rows.Count);                      // page of 2...
        var amount = result.Aggregates["AMOUNT"];
        Assert.Equal(26000m, Convert.ToDecimal(amount["sum"]));  // ...totals over all 5
        Assert.Equal(5200m, Convert.ToDecimal(amount["avg"]));
        Assert.Equal(1500m, Convert.ToDecimal(amount["min"]));
        Assert.Equal(9000m, Convert.ToDecimal(amount["max"]));
    }

    [Fact]
    public async Task CountDistinct_counts_values_not_rows()
    {
        var result = await _executor.Query(Definition, new ReportState
        {
            Aggregates = [new AggregateRule { Col = "CUSTOMER", Fn = AggregateFn.CountDistinct }],
        }, NoParams);

        // 10 rows, but 'Acme Corp' appears twice → 9 distinct
        Assert.Equal(9L, Convert.ToInt64(result.Aggregates["CUSTOMER"]["countDistinct"]));
    }

    [Fact]
    public async Task Break_totals_group_and_order_like_the_page()
    {
        var result = await _executor.Query(Definition, new ReportState
        {
            Breaks = ["STATUS"],
            Aggregates = [new AggregateRule { Col = "AMOUNT", Fn = AggregateFn.Sum }],
        }, NoParams);

        Assert.Equal(
            ["CANCELLED", "NEW", "PENDING", "SHIPPED"],
            result.BreakTotals.Select(b => (string)b.Key["STATUS"]!));
        Assert.Equal(4, result.BreakTotals.Count);
        Assert.Equal([1L, 1L, 3L, 5L], result.BreakTotals.Select(b => b.Rows));
        Assert.Equal(
            [6000m, 400m, 14800m, 26000m],
            result.BreakTotals.Select(b => Convert.ToDecimal(b.Aggregates["AMOUNT"]["sum"])));

        // Page rows arrive grouped: STATUS sorts first even with no user sort.
        Assert.Equal("CANCELLED", result.Rows[0]["STATUS"]);
    }

    [Fact]
    public async Task User_sort_direction_on_break_column_reverses_groups()
    {
        var result = await _executor.Query(Definition, new ReportState
        {
            Breaks = ["STATUS"],
            Sorts = [new SortRule { Col = "STATUS", Dir = SortDir.Desc }],
        }, NoParams);

        Assert.Equal("SHIPPED", result.BreakTotals[0].Key["STATUS"]);
        Assert.Equal("SHIPPED", result.Rows[0]["STATUS"]);
    }

    [Fact]
    public async Task Aggregates_over_empty_filtered_set_are_null()
    {
        var result = await _executor.Query(Definition, new ReportState
        {
            Filters = [Filter("STATUS", FilterOp.Eq, "NO_SUCH_STATUS")],
            Aggregates = [new AggregateRule { Col = "AMOUNT", Fn = AggregateFn.Sum }],
        }, NoParams);

        Assert.Equal(0, result.TotalRows);
        Assert.Null(result.Aggregates["AMOUNT"]["sum"]);
    }
}

public sealed class SqliteE2EFixture : IReportConnectionFactory, IDisposable
{
    private const string ConnectionString = "Data Source=ir-e2e;Mode=Memory;Cache=Shared";
    private readonly SqliteConnection _keepAlive;

    public SqliteE2EFixture()
    {
        _keepAlive = new SqliteConnection(ConnectionString);
        _keepAlive.Open();

        using var cmd = _keepAlive.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE ORDERS (
                ORDER_ID INTEGER PRIMARY KEY,
                CUSTOMER TEXT NOT NULL,
                STATUS   TEXT NOT NULL,
                AMOUNT   NUMERIC NOT NULL,
                NOTES    TEXT NULL
            );
            INSERT INTO ORDERS (CUSTOMER, STATUS, AMOUNT, NOTES) VALUES
                ('Acme Corp',   'SHIPPED',   9000, 'rush'),
                ('Globex',      'SHIPPED',   7500, NULL),
                ('Initech',     'SHIPPED',   5000, ''),
                ('Acme Corp',   'SHIPPED',   3000, 'fragile'),
                ('Hooli',       'SHIPPED',   1500, NULL),
                ('acme llc',    'PENDING',   2000, 'call first'),
                ('Umbrella',    'PENDING',    800, NULL),
                ('Stark Ind',   'PENDING',  12000, 'insured'),
                ('Wayne Ent',   'NEW',        400, 'standard'),
                ('Tyrell Corp', 'CANCELLED', 6000, 'refunded');
            """;
        cmd.ExecuteNonQuery();
    }

    public DbConnection CreateConnection(string name) => new SqliteConnection(ConnectionString);

    public void Dispose() => _keepAlive.Dispose();
}
