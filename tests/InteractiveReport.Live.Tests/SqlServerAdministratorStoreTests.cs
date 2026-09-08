using InteractiveReport.Core.Authorization;
using InteractiveReport.Core.Model;
using Microsoft.Data.SqlClient;

namespace InteractiveReport.Core.Tests;

/// <summary>
/// Live SQL Server execution of the administrator-grant contract. A scratch database on
/// SQL Server's case-insensitive default collation is where the binary identity column
/// proves it keeps case variants apart.
/// </summary>
public sealed class SqlServerAdministratorStoreTests : AdministratorStoreCorpus
{
    private const string TableName = "IR_ADMINISTRATORS_TEST";

    private static string? ConnectionString => Environment.GetEnvironmentVariable("IR_TEST_SQLSERVER");

    protected override SqlAdministratorStore CreateStore()
    {
        var connectionString = ConnectionString;
        Skip.If(
            string.IsNullOrWhiteSpace(connectionString),
            "set IR_TEST_SQLSERVER to run live SQL Server administrator-store verification");

        DropTable(connectionString!);
        return new SqlAdministratorStore(
            () => new AdministratorStoreConfig(
                "Administrators",
                ReportDialect.SqlServer,
                AutoCreate: true,
                TableName: TableName),
            new FixedConnectionFactory(() => new SqlConnection(connectionString!)));
    }

    protected override Task CleanUp()
    {
        if (ConnectionString is { } connectionString) DropTable(connectionString);
        return Task.CompletedTask;
    }

    private static void DropTable(string connectionString)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var drop = connection.CreateCommand();
        drop.CommandText = $"IF OBJECT_ID(N'{TableName}', N'U') IS NOT NULL DROP TABLE [{TableName}]";
        drop.ExecuteNonQuery();
    }
}
