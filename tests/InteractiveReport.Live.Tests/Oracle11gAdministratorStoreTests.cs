using InteractiveReport.Core.Authorization;
using InteractiveReport.Core.Model;
using Oracle.ManagedDataAccess.Client;

namespace InteractiveReport.Core.Tests;

/// <summary>
/// The administrator-grant contract in Oracle 11g mode against the modern Oracle server,
/// with the vocabulary guard on every statement.
/// </summary>
public sealed class Oracle11gAdministratorStoreTests : AdministratorStoreCorpus
{
    private const string TableName = "IR_ADMINISTRATORS_11G_TEST";

    private static string? ConnectionString => Environment.GetEnvironmentVariable("IR_TEST_ORACLE");

    protected override SqlAdministratorStore CreateStore()
    {
        var connectionString = ConnectionString;
        Skip.If(
            string.IsNullOrWhiteSpace(connectionString),
            "set IR_TEST_ORACLE to run live Oracle 11g-mode administrator-store verification");

        DropTable(connectionString!);
        return new SqlAdministratorStore(
            () => new AdministratorStoreConfig(
                "Administrators",
                ReportDialect.Oracle11g,
                AutoCreate: true,
                TableName: TableName),
            new FixedConnectionFactory(() => new OracleConnection(connectionString!)),
            new Oracle11gVocabularyGuard<SqlAdministratorStore>());
    }

    protected override Task CleanUp()
    {
        if (ConnectionString is { } connectionString) DropTable(connectionString);
        return Task.CompletedTask;
    }

    private static void DropTable(string connectionString)
        => OracleTestSchema.Drop(connectionString, $"DROP TABLE \"{TableName}\"");
}
