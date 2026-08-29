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
    public async Task Auto_create_upgrades_an_existing_table_with_the_primary_flag()
    {
        await using (var connection = new SqliteConnection(_cs))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IR_OLD_SAVED_REPORTS (
                    ID TEXT PRIMARY KEY,
                    REPORT_NAME TEXT NOT NULL,
                    TITLE TEXT NOT NULL,
                    OWNER TEXT NULL,
                    IS_GLOBAL INTEGER NOT NULL,
                    STATE_JSON TEXT NOT NULL,
                    MODIFIED_UTC TEXT NOT NULL,
                    ORIGIN TEXT NOT NULL DEFAULT 'user'
                );
                INSERT INTO IR_OLD_SAVED_REPORTS
                    (ID, REPORT_NAME, TITLE, OWNER, IS_GLOBAL, STATE_JSON, MODIFIED_UTC, ORIGIN)
                VALUES ('old-1', 'orders', 'Old', 'alice', 0, '{}', '2026-08-14T00:00:00.0000000Z', 'user');
                """;
            await command.ExecuteNonQueryAsync();
        }

        var store = new SqlSavedReportStore(
            () => new SavedReportStoreConfig(
                "Saved",
                ReportDialect.Sqlite,
                AutoCreate: true,
                TableName: "IR_OLD_SAVED_REPORTS"),
            new FixedConnectionFactory(() => new SqliteConnection(_cs)));

        var loaded = Assert.Single(await store.ListAll());

        Assert.False(loaded.IsPrimary);
        var expected = loaded with { };
        loaded.IsPrimary = true;
        Assert.True(await store.Update(loaded, expected));
        Assert.True((await store.Get(loaded.Id))!.IsPrimary);
    }

    [Fact]
    public async Task Auto_create_upgrades_an_existing_table_with_title_keys_and_the_unique_index()
    {
        await CreateLegacyTable("IR_LEGACY_TITLES", ("old-1", "Legacy View"));

        var store = new SqlSavedReportStore(
            () => new SavedReportStoreConfig(
                "Saved",
                ReportDialect.Sqlite,
                AutoCreate: true,
                TableName: "IR_LEGACY_TITLES"),
            new FixedConnectionFactory(() => new SqliteConnection(_cs)));

        // The first operation upgrades in place: TITLE_KEY backfilled from TITLE in
        // code, then the unique index makes the legacy row collide with new saves.
        Assert.Single(await store.ListAll());
        await Assert.ThrowsAsync<SavedReportTitleConflictException>(
            () => store.Create(Report("  legacy VIEW ")));
        await store.Create(Report("A fresh title"));
    }

    [Fact]
    public async Task Upgrade_over_preexisting_duplicate_titles_fails_with_guidance()
    {
        await CreateLegacyTable("IR_LEGACY_DUPES", ("old-1", "Twin"), ("old-2", "twin"));

        var store = new SqlSavedReportStore(
            () => new SavedReportStoreConfig(
                "Saved",
                ReportDialect.Sqlite,
                AutoCreate: true,
                TableName: "IR_LEGACY_DUPES"),
            new FixedConnectionFactory(() => new SqliteConnection(_cs)));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.ListAll());

        Assert.Contains("title uniqueness index", error.Message);
        Assert.Contains("duplicate user-saved titles", error.Message);
    }

    private async Task CreateLegacyTable(string tableName, params (string Id, string Title)[] rows)
    {
        await using var connection = new SqliteConnection(_cs);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE TABLE {tableName} (
                ID TEXT PRIMARY KEY,
                REPORT_NAME TEXT NOT NULL,
                TITLE TEXT NOT NULL,
                OWNER TEXT NULL,
                IS_GLOBAL INTEGER NOT NULL,
                STATE_JSON TEXT NOT NULL,
                MODIFIED_UTC TEXT NOT NULL,
                ORIGIN TEXT NOT NULL DEFAULT 'user'
            )
            """;
        await command.ExecuteNonQueryAsync();
        foreach (var (id, title) in rows)
        {
            command.CommandText = $$"""
                INSERT INTO {{tableName}}
                    (ID, REPORT_NAME, TITLE, OWNER, IS_GLOBAL, STATE_JSON, MODIFIED_UTC, ORIGIN)
                VALUES ('{{id}}', 'orders', '{{title}}', 'alice', 0, '{}', '2026-08-14T00:00:00.0000000Z', 'user')
                """;
            await command.ExecuteNonQueryAsync();
        }
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
