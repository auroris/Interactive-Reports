using InteractiveReport.Core.Authorization;
using InteractiveReport.Core.Model;
using Npgsql;

namespace InteractiveReport.Core.Tests;

/// <summary>Live PostgreSQL execution of the administrator-grant contract.</summary>
public sealed class PostgresAdministratorStoreTests : AdministratorStoreCorpus
{
    private const string TableName = "IR_ADMINISTRATORS_TEST";

    private static string? ConnectionString => Environment.GetEnvironmentVariable("IR_TEST_POSTGRES");

    protected override SqlAdministratorStore CreateStore()
    {
        var connectionString = ConnectionString;
        Skip.If(
            string.IsNullOrWhiteSpace(connectionString),
            "set IR_TEST_POSTGRES to run live Postgres administrator-store verification");

        DropTable(connectionString!);
        return new SqlAdministratorStore(
            () => new AdministratorStoreConfig(
                "Administrators",
                ReportDialect.Postgres,
                AutoCreate: true,
                TableName: TableName),
            new FixedConnectionFactory(() => new NpgsqlConnection(connectionString!)));
    }

    protected override Task CleanUp()
    {
        if (ConnectionString is { } connectionString) DropTable(connectionString);
        return Task.CompletedTask;
    }

    private static void DropTable(string connectionString)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var drop = connection.CreateCommand();
        drop.CommandText = $"""DROP TABLE IF EXISTS "{TableName}" """;
        drop.ExecuteNonQuery();
    }
}
