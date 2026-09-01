using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Schema;
using Microsoft.Data.Sqlite;

namespace InteractiveReport.Core.Tests;

/// <summary>
/// One schema discovery is shared by every concurrent caller of the same definition. The
/// caller that happened to start it must not be able to fault the others by going away.
/// </summary>
public sealed class SchemaDiscoveryCancellationTests : IDisposable
{
    private static readonly IReadOnlyDictionary<string, object?> NoParams = new Dictionary<string, object?>();

    private readonly SqliteConnection _keepAlive;
    private readonly string _cs;
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public SchemaDiscoveryCancellationTests()
    {
        _cs = $"Data Source=discovery-{Guid.NewGuid():n};Mode=Memory;Cache=Shared";
        _keepAlive = new SqliteConnection(_cs);
        _keepAlive.Open();
        using var command = _keepAlive.CreateCommand();
        command.CommandText = "CREATE TABLE ORDERS (ORDER_ID INTEGER PRIMARY KEY, AMOUNT REAL NOT NULL)";
        command.ExecuteNonQuery();
    }

    public void Dispose() => _keepAlive.Dispose();

    [Fact]
    public async Task A_cancelled_first_requester_does_not_fault_the_waiters_sharing_its_discovery()
    {
        var executor = new ReportExecutor(new GatedFactory(this), new SchemaCache());
        var definition = new ReportDefinition
        {
            Name = "orders-discovery",
            Connection = "gated",
            Dialect = ReportDialect.Sqlite,
            Sql = "SELECT ORDER_ID, AMOUNT FROM ORDERS",
        };

        using var first = new CancellationTokenSource();
        var firstCall = executor.GetSchema(definition, NoParams, first.Token);
        var secondCall = executor.GetSchema(definition, NoParams, CancellationToken.None);

        // The first browser goes away while its discovery is still opening the connection.
        first.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstCall);
        Assert.False(secondCall.IsCompleted);

        _gate.SetResult();
        var schema = await secondCall;

        Assert.Contains(schema.Columns, column => column.Name == "AMOUNT");
    }

    private sealed class GatedFactory(SchemaDiscoveryCancellationTests owner) : IReportConnectionFactory
    {
        public DbConnection CreateConnection(string name)
            => new GatedConnection(new SqliteConnection(owner._cs), owner._gate.Task);
    }

    /// <summary>Delays opening until the test releases it, so a discovery can be observed in flight.</summary>
    private sealed class GatedConnection(SqliteConnection inner, Task gate) : DbConnection
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

        public override async Task OpenAsync(CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken);
            await inner.OpenAsync(cancellationToken);
        }

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
            => inner.BeginTransaction(isolationLevel);

        protected override DbCommand CreateDbCommand() => inner.CreateCommand();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
