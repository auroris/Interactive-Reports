using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Tests;

public sealed class OracleVersionDetectionTests
{
    [Fact]
    public async Task Oracle11g_server_version_automatically_switches_definition_to_oracle11g_mode()
    {
        var connectionName = "OracleDb";
        var fakeOracle11gConn = new VersionedOracleFakeConnection("11.2.0.4.0");
        var factory = new MutableConnectionFactory(connectionName, fakeOracle11gConn);
        var manager = new ReportConnectionManager(factory);

        var definition = new ReportDefinition
        {
            Name = "test_oracle_report",
            Connection = connectionName,
            Dialect = ReportDialect.Oracle,
            Sql = "SELECT * FROM ORDERS",
        };

        var openedConn = await manager.Open(definition, CancellationToken.None);
        Assert.Same(fakeOracle11gConn, openedConn);

        // Definition dialect should now be updated to Oracle11g
        Assert.Equal(ReportDialect.Oracle11g, definition.Dialect);
        Assert.Equal(ReportDialect.Oracle11g, definition.GetEffectiveDialect());

        // Factory should also have detected Oracle11g
        Assert.Equal(ReportDialect.Oracle11g, factory.DetectedDialect);
    }

    [Fact]
    public async Task Modern_oracle_server_version_keeps_oracle_mode()
    {
        var connectionName = "Oracle19cDb";
        var fakeOracle19cConn = new VersionedOracleFakeConnection("19.3.0.0.0");
        var factory = new MutableConnectionFactory(connectionName, fakeOracle19cConn);
        var manager = new ReportConnectionManager(factory);

        var definition = new ReportDefinition
        {
            Name = "test_oracle_report",
            Connection = connectionName,
            Dialect = ReportDialect.Oracle,
            Sql = "SELECT * FROM ORDERS",
        };

        var openedConn = await manager.Open(definition, CancellationToken.None);
        Assert.Same(fakeOracle19cConn, openedConn);

        // Definition dialect remains Oracle
        Assert.Equal(ReportDialect.Oracle, definition.Dialect);
        Assert.Equal(ReportDialect.Oracle, definition.GetEffectiveDialect());

        // Factory detected dialect remains unmodified
        Assert.Null(factory.DetectedDialect);
    }

    private sealed class MutableConnectionFactory(string name, DbConnection conn) : IReportConnectionFactory
    {
        public ReportDialect? DetectedDialect { get; private set; }

        public DbConnection CreateConnection(string requestedName)
        {
            if (!string.Equals(requestedName, name, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Unknown connection '{requestedName}'");
            return conn;
        }

        public void SetDetectedDialect(string connectionName, ReportDialect dialect)
        {
            DetectedDialect = dialect;
        }
    }

    private sealed class VersionedOracleFakeConnection(string version) : DbConnection
    {
        [AllowNull]
        public override string ConnectionString { get; set; } = "";
        public override string Database => "ORCL";
        public override string DataSource => "localhost";
        public override string ServerVersion => version;
        public override ConnectionState State => ConnectionState.Open;
        public override void ChangeDatabase(string databaseName) { }
        public override void Close() { }
        public override void Open() { }
        public override Task OpenAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
    }
}
