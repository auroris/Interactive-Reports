using InteractiveReport.Core.Model;
using InteractiveReport.Tests;
using Microsoft.Data.Sqlite;

namespace InteractiveReport.Core.Tests;

/// <summary>
/// Runs the complete schema-default → query → save → process restart → load path.
/// The first pass uses the zero-configuration local SQLite store; the second moves
/// persistence into the report database without changing the document being saved.
/// </summary>
public sealed class PersistenceHttpLiveTests
{
    public static TheoryData<ReportDialect> Dialects => new()
    {
        ReportDialect.SqlServer,
        ReportDialect.Oracle,
        ReportDialect.Postgres,
    };

    [SkippableTheory]
    [MemberData(nameof(Dialects))]
    public async Task Synthetic_default_document_roundtrips_through_both_persistence_targets(
        ReportDialect dialect)
    {
        var live = LiveDb.For(dialect);
        var suffix = Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        var defaultTable = $"IR_D_{suffix}";
        var explicitTable = $"IR_E_{suffix}";
        var contentRoot = Directory.CreateTempSubdirectory("interactive-report-live-persistence-").FullName;

        try
        {
            await PersistenceHttpScenario.Run(
                dialect,
                () => live.CreateConnection("live"),
                "SELECT * FROM IR_TEST_ORDERS",
                ["ORDER_ID", "CUSTOMER", "STATUS", "AMOUNT", "NOTES", "ORDER_DATE", "ORDER_DATE_TEXT"],
                contentRoot,
                defaultTable,
                explicitTable);
        }
        finally
        {
            await PersistenceHttpScenario.DropTableIfExists(
                () => live.CreateConnection("live"), dialect, defaultTable);
            await PersistenceHttpScenario.DropTableIfExists(
                () => live.CreateConnection("live"), dialect, explicitTable);
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(contentRoot))
                Directory.Delete(contentRoot, recursive: true);
        }
    }
}
