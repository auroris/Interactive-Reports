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
/// Multi-statement query paths configured for snapshot consistency use a
/// dialect-appropriate read transaction and enlist every command in it.
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

    private static ReportDefinition Definition(ReportConsistency consistency = ReportConsistency.Snapshot) => new()
    {
        Name = "orders-consistency",
        Connection = "Recorded",
        Dialect = ReportDialect.Sqlite,
        Consistency = consistency,
        Sql = "SELECT ORDER_ID, CUSTOMER, AMOUNT FROM ORDERS",
    };

    [Fact]
    public async Task Grid_count_aggregates_and_rows_share_one_read_transaction()
    {
        var result = await Executor().Query(Definition(), Doc(source: new StageLayer
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
            Definition(),
            Doc(tail: [Group(["CUSTOMER"])]),
            NoParams);

        Assert.Equal(2, result.TotalRows);
        Assert.Equal([IsolationLevel.Serializable], _begun);
    }

    [Fact]
    public async Task Single_statement_chart_reads_run_without_a_transaction()
    {
        var result = await Executor().Query(
            Definition(),
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

    [Fact]
    public async Task Single_statement_Pivot_without_totals_runs_without_a_transaction()
    {
        var result = await Executor().Query(
            Definition(),
            Doc(tail:
            [
                Pivot(
                    rows: ["ORDER_ID"],
                    cols: ["CUSTOMER"],
                    values: [Metric("m1", "AMOUNT", AggregateFn.Sum)],
                    totals: false),
            ]),
            NoParams);

        Assert.NotEmpty(result.Rows);
        Assert.Empty(_begun);
    }

    [Fact]
    public async Task None_leaves_multi_statement_reads_untransacted()
    {
        var result = await Executor().Query(
            Definition(ReportConsistency.None),
            Doc(source: new StageLayer
            {
                Aggregates = [new AggregateRule { Col = "AMOUNT", Fn = AggregateFn.Sum }],
            }),
            NoParams);

        Assert.Equal(3, result.TotalRows);
        Assert.Equal(60m, Convert.ToDecimal(result.Aggregates!["AMOUNT"]["sum"]));
        Assert.Empty(_begun);
    }

    [Fact]
    public async Task Sql_server_snapshot_disabled_fails_before_opening_a_transaction()
    {
        var manager = new ReportConnectionManager(new UnusedFactory());
        await using var connection = new ControlStatementConnection(scalarResult: 0);
        var definition = new ReportDefinition
        {
            Name = "sql-server-snapshot",
            Connection = "unused",
            Dialect = ReportDialect.SqlServer,
            Consistency = ReportConsistency.Snapshot,
            Sql = "SELECT 1 AS ID",
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.BeginReadScope(connection, definition, CancellationToken.None));

        Assert.Contains("ALLOW_SNAPSHOT_ISOLATION", error.Message);
        Assert.Contains("consistency 'none'", error.Message);
        Assert.Empty(connection.IsolationLevels);
    }

    [Fact]
    public async Task Oracle_snapshot_scope_sets_read_only_and_ends_with_rollback()
    {
        var manager = new ReportConnectionManager(new UnusedFactory());
        await using var connection = new ControlStatementConnection();
        var definition = new ReportDefinition
        {
            Name = "oracle-snapshot",
            Connection = "unused",
            Dialect = ReportDialect.Oracle,
            Consistency = ReportConsistency.Snapshot,
            Sql = "SELECT 1 AS ID FROM DUAL",
        };

        await using var scope = await manager.BeginReadScope(connection, definition, CancellationToken.None);
        Assert.NotNull(scope.Transaction);
        Assert.Equal([IsolationLevel.ReadCommitted], connection.IsolationLevels);
        Assert.Equal([scope.Transaction], connection.CommandTransactions);
        Assert.Equal(["SET TRANSACTION READ ONLY"], connection.Commands);

        await scope.CompleteAsync(CancellationToken.None);

        Assert.Equal(["SET TRANSACTION READ ONLY", "ROLLBACK"], connection.Commands);
    }

    private sealed class RecordingFactory(ConsistentReadTests owner) : IReportConnectionFactory
    {
        public DbConnection CreateConnection(string name)
            => new RecordingConnection(new SqliteConnection(owner._cs), owner._begun);
    }

    private sealed class UnusedFactory : IReportConnectionFactory
    {
        public DbConnection CreateConnection(string name) => throw new NotSupportedException();
    }

    private sealed class ControlStatementConnection(object? scalarResult = null) : DbConnection
    {
        public List<string> Commands { get; } = [];
        public List<IsolationLevel> IsolationLevels { get; } = [];
        public List<DbTransaction?> CommandTransactions { get; } = [];
        public object? ScalarResult { get; } = scalarResult;

        [AllowNull]
        public override string ConnectionString { get; set; } = "";
        public override string Database => "test";
        public override string DataSource => "test";
        public override string ServerVersion => "test";
        public override ConnectionState State => ConnectionState.Open;
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        public override void Close() { }
        public override void Open() { }
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        {
            IsolationLevels.Add(isolationLevel);
            return new ControlStatementTransaction(this, isolationLevel);
        }
        protected override DbCommand CreateDbCommand() => new ControlStatementCommand(this);
    }

    private sealed class ControlStatementTransaction(
        ControlStatementConnection connection,
        IsolationLevel isolationLevel) : DbTransaction
    {
        public override IsolationLevel IsolationLevel => isolationLevel;
        protected override DbConnection DbConnection => connection;
        public override void Commit() => connection.Commands.Add("COMMIT");
        public override void Rollback() => connection.Commands.Add("ROLLBACK");
    }

    private sealed class ControlStatementCommand(ControlStatementConnection connection) : DbCommand
    {
        [AllowNull]
        public override string CommandText { get; set; } = "";
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; } = connection;
        protected override DbParameterCollection DbParameterCollection => throw new NotSupportedException();
        protected override DbTransaction? DbTransaction { get; set; }
        public override void Cancel() { }
        public override int ExecuteNonQuery()
        {
            connection.Commands.Add(CommandText);
            connection.CommandTransactions.Add(DbTransaction);
            return 0;
        }
        public override object? ExecuteScalar() => connection.ScalarResult;
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => throw new NotSupportedException();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
            => throw new NotSupportedException();
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
