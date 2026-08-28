using InteractiveReport.Core.Model;
using InteractiveReport.Tests;
using Microsoft.Data.Sqlite;

namespace InteractiveReport.AspNetCore.Tests;

public sealed class PersistenceHttpTests
{
    [Fact]
    public async Task Default_document_persists_through_literal_and_registered_storage_targets()
    {
        var tempRoot = Directory.CreateTempSubdirectory("interactive-report-persistence-").FullName;
        var contentRoot = Path.Combine(tempRoot, "host");
        var dataPath = Path.Combine(tempRoot, "report-data.db");
        Directory.CreateDirectory(contentRoot);
        var connectionString = $"Data Source={dataPath};Pooling=False";

        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IR_PERSISTENCE_TEST (
                    ID INTEGER PRIMARY KEY,
                    LABEL TEXT NOT NULL,
                    AMOUNT REAL NOT NULL
                );
                INSERT INTO IR_PERSISTENCE_TEST (ID, LABEL, AMOUNT)
                VALUES (1, 'first', 12.5), (2, 'second', 25.0);
                """;
            await command.ExecuteNonQueryAsync();
        }

        try
        {
            Assert.NotEqual(
                Path.GetFullPath(dataPath),
                Path.GetFullPath(PersistenceHttpScenario.ExplicitFileStorePath(contentRoot)));

            await PersistenceHttpScenario.Run(
                ReportDialect.Sqlite,
                () => new SqliteConnection(connectionString),
                "SELECT * FROM IR_PERSISTENCE_TEST",
                ["ID", "LABEL", "AMOUNT"],
                contentRoot,
                defaultStoreTable: "IR_SAVED_REPORTS",
                explicitStoreTable: "IR_SAVED_REPORTS");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }
}
