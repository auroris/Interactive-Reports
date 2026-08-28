using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Schema;
using Microsoft.Data.Sqlite;
using static InteractiveReport.Core.Tests.TestFixtures;

namespace InteractiveReport.Core.Tests;

/// <summary>
/// Multi-statement query paths must read one database snapshot: the executor wraps
/// them in a dialect-appropriate read transaction and every command joins it.
/// Recording the provider calls (instead of racing a concurrent writer) keeps the
/// assertion deterministic; Microsoft.Data.Sqlite additionally enforces that every
/// command on a transacted connection carries the transaction, so the existing
/// end-to-end suite would fail loudly on any unenlisted command.
/// </summary>
public sealed class ConsistentReadTests : IDisposable
{
    private static readonly IReadOnlyDictionary<string, object?> NoParams = new Dictionary<string, object?>();

    private readonly SqliteConnection _keepAlive;
    private readonly string _cs;
    private readonly List<IsolationLevel> _begun = [];

    public ConsistentReadTests()
    {
        _cs = $"Data Source=consistent-{Guid.NewGuid():n};Mode=Memory;Cache=Shared";
        _keepAlive = new SqliteConnection(_cs);
        _keepAlive.Open();
        using var command = _keepAlive.CreateCommand();
        command.CommandText = """
            CREATE TABLE ORDERS (ORDER_ID INTEGER PRIMARY KEY, CUSTOMER TEXT NOT NULL, AMOUNT REAL NOT NULL);
            INSERT INTO ORDERS VALUES (1, 'a', 10), (2, 'b', 20), (3, 'a', 30);
            """;
        command.ExecuteNonQuery();
    }

    public void Dispose() => _keepAlive.Dispose();

    private ReportExecutor Executor() => new(new RecordingFactory(this), new SchemaCache());

    private static ReportDefinition Definition => new()
    {
        Name = "orders-consistency",
        Connection = "Recorded",
        Dialect = ReportDialect.Sqlite,
        Sql = "SELECT ORDER_ID, CUSTOMER, AMOUNT FROM ORDERS",
    };

    [Fact]
    public async Task Grid_count_aggregates_and_rows_share_one_read_transaction()
    {
        var result = await Executor().Query(Definition, Doc(source: new StageLayer
        {
            Aggregates = [new AggregateRule { Col = "AMOUNT", Fn = AggregateFn.Sum }],
        }), NoParams);

        Assert.Equal(3, result.TotalRows);
        Assert.Equal(60m, Convert.ToDecimal(result.Aggregates!["AMOUNT"]["sum"]));
        // Schema discovery runs untransacted; the query path then begins exactly one.
        Assert.Equal([IsolationLevel.Serializable], _begun);
    }

    [Fact]
    public async Task Group_stage_count_and_rows_share_one_read_transaction()
    {
        var result = await Executor().Query(
            Definition,
            Doc(tail: [Group(["CUSTOMER"])]),
            NoParams);

        Assert.Equal(2, result.TotalRows);
        Assert.Equal([IsolationLevel.Serializable], _begun);
    }

    [Fact]
    public async Task Single_statement_chart_reads_run_without_a_transaction()
    {
        var result = await Executor().Query(
            Definition,
            Doc(tail:
            [
                ChartStage(shape =>
                {
                    shape.Type = "bar";
                    shape.Label = "CUSTOMER";
                    shape.Fn = AggregateFn.Count;
                }),
            ]),
            NoParams);

        Assert.Equal(2, result.TotalRows);
        Assert.Empty(_begun);
    }

    private sealed class RecordingFactory(ConsistentReadTests owner) : IReportConnectionFactory
    {
        public DbConnection CreateConnection(string name)
            => new RecordingConnection(new SqliteConnection(owner._cs), owner._begun);
    }

    private sealed class RecordingConnection(SqliteConnection inner, List<IsolationLevel> begun) : DbConnection
    {
        [AllowNull]
        public override string ConnectionString
        {
            get => inner.ConnectionString;
            set => inner.ConnectionString = value;
        }

        public override string Database => inner.Database;
        public override string DataSource => inner.DataSource;
        public override string ServerVersion => inner.ServerVersion;
        public override ConnectionState State => inner.State;
        public override void ChangeDatabase(string databaseName) => inner.ChangeDatabase(databaseName);
        public override void Close() => inner.Close();
        public override void Open() => inner.Open();

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        {
            begun.Add(isolationLevel);
            return inner.BeginTransaction(isolationLevel);
        }

        // Commands bind to the inner connection, matching the transactions above.
        protected override DbCommand CreateDbCommand() => inner.CreateCommand();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
