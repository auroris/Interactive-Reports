using InteractiveReport.Core.Model;
using InteractiveReport.Core.SavedReports;
using Npgsql;

namespace InteractiveReport.Core.Tests;

/// <summary>
/// Live: every corpus test drops the dedicated table first, so AutoCreate re-runs the
/// Postgres DDL each time. Quoted identifiers are load-bearing because Postgres folds
/// unquoted names to lowercase.
/// </summary>
public sealed class PostgresSavedReportStoreTests : SavedReportStoreCorpus
{
    private const string TableName = "IR_SAVED_REPORTS_TEST";

    protected override SqlSavedReportStore CreateStore()
    {
        var connectionString = Environment.GetEnvironmentVariable("IR_TEST_POSTGRES");
        Skip.If(
            string.IsNullOrWhiteSpace(connectionString),
            "set IR_TEST_POSTGRES to run live Postgres saved-report verification");

        using (var connection = new NpgsqlConnection(connectionString))
        {
            connection.Open();
            using var drop = connection.CreateCommand();
            drop.CommandText = $"""DROP TABLE IF EXISTS "{TableName}" """;
            drop.ExecuteNonQuery();
        }

        return new SqlSavedReportStore(
            () => new SavedReportStoreConfig(
                "Saved",
                ReportDialect.Postgres,
                AutoCreate: true,
                TableName: TableName),
            new FixedConnectionFactory(() => new NpgsqlConnection(connectionString!)));
    }
}
