using InteractiveReport.Core.Model;
using InteractiveReport.Core.SavedReports;
using Oracle.ManagedDataAccess.Client;

namespace InteractiveReport.Core.Tests;

/// <summary>
/// The saved-report contract in Oracle 11g mode against the modern Oracle server: sequence-and-
/// trigger identities and ROWNUM windows, with the vocabulary guard on every statement. The
/// trigger needs CREATE TRIGGER, which the setup notes in docs/TESTING.md grant.
/// </summary>
public sealed class Oracle11gSavedReportStoreTests : SavedReportStoreCorpus
{
    private const string TableName = "IR_SAVED_11G_TEST";

    private static string? ConnectionString => Environment.GetEnvironmentVariable("IR_TEST_ORACLE");

    protected override SqlSavedReportStore CreateStore()
    {
        var connectionString = ConnectionString;
        Skip.If(
            string.IsNullOrWhiteSpace(connectionString),
            "set IR_TEST_ORACLE to run live Oracle 11g-mode saved-report verification");
        Skip.IfNot(
            OracleTestSchema.HasPrivilege(connectionString!, "CREATE TRIGGER"),
            "grant CREATE TRIGGER to the Oracle test user; Oracle 11g mode persists identities with a sequence and a trigger");

        DropObjects(connectionString!);
        return new SqlSavedReportStore(
            () => new SavedReportStoreConfig(
                "Saved",
                ReportDialect.Oracle11g,
                AutoCreate: true,
                TableName: TableName),
            new FixedConnectionFactory(() => new OracleConnection(connectionString!)),
            new Oracle11gVocabularyGuard<SqlSavedReportStore>());
    }

    protected override Task CleanUp()
    {
        if (ConnectionString is { } connectionString) DropObjects(connectionString);
        return Task.CompletedTask;
    }

    /// <summary>Drops the table (its trigger goes with it) and the identity sequence.</summary>
    private static void DropObjects(string connectionString)
        => OracleTestSchema.Drop(
            connectionString,
            $"DROP TABLE \"{TableName}\"",
            $"DROP SEQUENCE \"{SqlSavedReportStore.OracleIdentifier(TableName, "_SEQ")}\"");
}
