using InteractiveReport.Core.Model;
using InteractiveReport.Core.SavedReports;
using Oracle.ManagedDataAccess.Client;

namespace InteractiveReport.Core.Tests;

/// <summary>Live Oracle execution of the dialect-neutral saved-report contract.</summary>
public sealed class OracleSavedReportStoreTests : SavedReportStoreCorpus
{
    private const string TableName = "IR_SAVED_REPORTS_TEST";

    protected override SqlSavedReportStore CreateStore()
    {
        var connectionString = Environment.GetEnvironmentVariable("IR_TEST_ORACLE");
        Skip.If(
            string.IsNullOrWhiteSpace(connectionString),
            "set IR_TEST_ORACLE to run live Oracle saved-report verification");

        using (var connection = new OracleConnection(connectionString))
        {
            connection.Open();
            using var drop = connection.CreateCommand();
            drop.CommandText = $"""
                BEGIN
                    EXECUTE IMMEDIATE 'DROP TABLE {TableName}';
                EXCEPTION WHEN OTHERS THEN
                    IF SQLCODE != -942 THEN RAISE; END IF;
                END;
                """;
            drop.ExecuteNonQuery();
        }

        return new SqlSavedReportStore(
            () => new SavedReportStoreConfig(
                "Saved",
                ReportDialect.Oracle,
                AutoCreate: true,
                TableName: TableName),
            new FixedConnectionFactory(() => new OracleConnection(connectionString!)));
    }
}
