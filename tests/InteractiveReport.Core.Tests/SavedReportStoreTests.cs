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

    [Fact]
    public async Task Concurrent_creates_return_distinct_generated_ids_without_leaving_returning_readers_open()
    {
        var store = CreateStore();
        var reports = Enumerable.Range(1, 12)
            .Select(index => Report($"Concurrent {index}"))
            .ToArray();

        await Task.WhenAll(reports.Select(report => store.Create(report)));

        Assert.Equal(reports.Length, reports.Select(report => report.Id).Distinct().Count());
        Assert.All(reports, report => Assert.True(report.Id > 0));
        Assert.Equal(reports.Length, (await store.ListAll()).Count);
    }

    public void Dispose() => _keepAlive.Dispose();

    private static SavedReport Report(string title) => new()
    {
        Id = 0,
        ReportName = "orders",
        Title = title,
        Owner = "alice",
        StateJson = "{}",
    };
}
