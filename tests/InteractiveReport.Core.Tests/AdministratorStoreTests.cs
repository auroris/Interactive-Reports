using System.Data.Common;
using InteractiveReport.Core.Authorization;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;
using Microsoft.Data.Sqlite;

namespace InteractiveReport.Core.Tests;

public sealed class AdministratorStoreTests : IDisposable
{
    private readonly SqliteConnection _keepAlive;
    private readonly SqlAdministratorStore _store;

    public AdministratorStoreTests()
    {
        var connectionString = $"Data Source=administrators-{Guid.NewGuid():n};Mode=Memory;Cache=Shared";
        _keepAlive = new SqliteConnection(connectionString);
        _keepAlive.Open();
        _store = new SqlAdministratorStore(
            () => new AdministratorStoreConfig("Administrators", ReportDialect.Sqlite),
            new ConnectionFactory(() => new SqliteConnection(connectionString)));
    }

    [Fact]
    public async Task Grants_are_idempotent_case_sensitive_and_revocable()
    {
        Assert.Empty(await _store.List());
        Assert.False(await _store.IsAdministrator("alice"));

        await _store.Grant(" Alice ");
        await _store.Grant("Alice");
        await _store.Grant("bob");

        Assert.Equal(["Alice", "bob"], await _store.List());
        Assert.True(await _store.IsAdministrator("Alice"));
        Assert.True(await _store.IsAdministrator(" Alice "));
        // Identities are opaque: a case variant is a different account.
        Assert.False(await _store.IsAdministrator("alice"));

        Assert.False(await _store.Revoke("ALICE"));
        Assert.True(await _store.Revoke("Alice"));
        Assert.False(await _store.Revoke("Alice"));
        Assert.False(await _store.IsAdministrator("Alice"));
        Assert.Equal(["bob"], await _store.List());
    }

    [Fact]
    public async Task Blank_identities_are_rejected_before_reaching_the_database()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _store.Grant(" "));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.IsAdministrator(""));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.Revoke(""));
        Assert.Empty(await _store.List());
    }

    public void Dispose() => _keepAlive.Dispose();

    private sealed class ConnectionFactory(Func<DbConnection> create) : IReportConnectionFactory
    {
        public DbConnection CreateConnection(string name) => create();
    }
}
