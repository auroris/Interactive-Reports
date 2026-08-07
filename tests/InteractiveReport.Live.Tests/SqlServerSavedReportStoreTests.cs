using InteractiveReport.Core.Model;
using InteractiveReport.Core.SavedReports;
using Microsoft.Data.SqlClient;

namespace InteractiveReport.Core.Tests;

/// <summary>Live SQL Server execution of the dialect-neutral saved-report contract.</summary>
public sealed class SqlServerSavedReportStoreTests : SavedReportStoreCorpus
{
    private const string TableName = "IR_SAVED_REPORTS_TEST";

    protected override SqlSavedReportStore CreateStore()
    {
        var connectionString = Environment.GetEnvironmentVariable("IR_TEST_SQLSERVER");
        Skip.If(
            string.IsNullOrWhiteSpace(connectionString),
            "set IR_TEST_SQLSERVER to run live SQL Server saved-report verification");

        using (var connection = new SqlConnection(connectionString))
        {
            connection.Open();
            using var drop = connection.CreateCommand();
            drop.CommandText = $"IF OBJECT_ID(N'{TableName}', N'U') IS NOT NULL DROP TABLE [{TableName}]";
            drop.ExecuteNonQuery();
        }

        return new SqlSavedReportStore(
            () => new SavedReportStoreConfig(
                "Saved",
                ReportDialect.SqlServer,
                AutoCreate: true,
                TableName: TableName),
            new FixedConnectionFactory(() => new SqlConnection(connectionString!)));
    }
}
