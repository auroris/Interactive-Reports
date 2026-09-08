using System.Reflection;
using InteractiveReport.Core.Authorization;
using InteractiveReport.Core.Model;
using Microsoft.Data.Sqlite;

namespace InteractiveReport.Core.Tests;

public sealed class SqliteAdministratorStoreTests : AdministratorStoreCorpus, IDisposable
{
    private readonly SqliteConnection _keepAlive;
    private readonly string _connectionString;

    public SqliteAdministratorStoreTests()
    {
        _connectionString = $"Data Source=administrators-{Guid.NewGuid():n};Mode=Memory;Cache=Shared";
        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();
    }

    protected override SqlAdministratorStore CreateStore() => new(
        () => new AdministratorStoreConfig("Administrators", ReportDialect.Sqlite),
        new FixedConnectionFactory(() => new SqliteConnection(_connectionString)));

    [Fact]
    public void Sql_server_table_ddl_collates_the_identity_binary()
    {
        // Under SQL Server's case-insensitive default collation, an uncollated key let UPDATE
        // and DELETE match a case variant, so granting "alice" merged into "Alice" and revoking
        // "ALICE" removed "Alice". The live corpus proves the behavior; this pins the DDL.
        var method = typeof(SqlAdministratorStore).GetMethod(
            "CreateTableSql", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var config = new AdministratorStoreConfig("Admins", ReportDialect.SqlServer, TableName: "IR_ADMINS");
        var ddl = (string)method.Invoke(null, [config])!;

        Assert.Contains("IDENTITY_VALUE NVARCHAR(400) COLLATE Latin1_General_100_BIN2 PRIMARY KEY", ddl);
    }

    public void Dispose() => _keepAlive.Dispose();
}
