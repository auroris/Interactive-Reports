using InteractiveReport.Core.Model;
using InteractiveReport.Core.SavedReports;
using Microsoft.Data.Sqlite;

namespace InteractiveReport.Core.Tests;

public sealed class SqliteSavedReportStoreTests : SavedReportStoreCorpus, IDisposable
{
    private readonly SqliteConnection _keepAlive;
    private readonly string _cs;

    public SqliteSavedReportStoreTests()
    {
        _cs = $"Data Source=saved-{Guid.NewGuid():n};Mode=Memory;Cache=Shared";
        _keepAlive = new SqliteConnection(_cs);
        _keepAlive.Open();
    }

    protected override SqlSavedReportStore CreateStore() => new(
        () => new SavedReportStoreConfig("Saved", ReportDialect.Sqlite),
        new FixedConnectionFactory(() => new SqliteConnection(_cs)));

    [Fact]
    public async Task Configuration_change_initializes_and_uses_the_new_target_atomically()
    {
        var tableName = "IR_SAVED_A";
        var store = new SqlSavedReportStore(
            () => new SavedReportStoreConfig(
                "Saved",
                ReportDialect.Sqlite,
                AutoCreate: true,
                TableName: tableName),
            new FixedConnectionFactory(() => new SqliteConnection(_cs)));

        await store.Create(Report("First"));
        tableName = "IR_SAVED_B";
        await store.Create(Report("Second"));

        var currentTarget = await store.ListAll();
        Assert.Equal("Second", Assert.Single(currentTarget).Title);
    }

    public void Dispose() => _keepAlive.Dispose();

    private static SavedReport Report(string title) => new()
    {
        Id = SavedReport.NewId(),
        ReportName = "orders",
        Title = title,
        Owner = "alice",
        StateJson = "{}",
    };
}
