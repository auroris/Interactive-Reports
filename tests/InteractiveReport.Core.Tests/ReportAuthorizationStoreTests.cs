using System.Data.Common;
using InteractiveReport.Core.Authorization;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;
using Microsoft.Data.Sqlite;

namespace InteractiveReport.Core.Tests;

public sealed class ReportAuthorizationStoreTests : IDisposable
{
    private readonly SqliteConnection _keepAlive;
    private readonly SqlReportAuthorizationStore _store;

    public ReportAuthorizationStoreTests()
    {
        var connectionString = $"Data Source=authorization-{Guid.NewGuid():n};Mode=Memory;Cache=Shared";
        _keepAlive = new SqliteConnection(connectionString);
        _keepAlive.Open();
        _store = new SqlReportAuthorizationStore(
            () => new ReportAuthorizationStoreConfig("Authorization", ReportDialect.Sqlite),
            new ConnectionFactory(() => new SqliteConnection(connectionString)));
    }

    [Fact]
    public async Task Administrator_grants_are_idempotent_case_sensitive_and_revocable()
    {
        Assert.False((await _store.GetAdministratorAccess("alice")).Configured);

        await _store.GrantAdministrator(" Alice ");
        await _store.GrantAdministrator("Alice");

        var access = await _store.GetAdministratorAccess("Alice");
        Assert.True(access.Configured);
        Assert.True(access.UserGranted);
        Assert.False((await _store.GetAdministratorAccess("alice")).UserGranted);
        Assert.Single((await _store.ListAll())
            .Where(entry => entry.Kind == ReportAuthorizationEntryKind.Administrator));

        Assert.False(await _store.RevokeAdministrator("ALICE"));
        Assert.True(await _store.RevokeAdministrator("Alice"));
        Assert.False((await _store.GetAdministratorAccess("alice")).Configured);
    }

    [Fact]
    public async Task Report_restriction_and_user_grants_are_independent()
    {
        await _store.GrantReportUser("Orders", "bob");
        var staged = await _store.GetReportAccess("orders", "BOB");
        Assert.False(staged.Restricted);
        Assert.False(staged.UserGranted);
        Assert.True((await _store.GetReportAccess("orders", "bob")).UserGranted);

        await _store.SetReportRestricted("ORDERS", true);
        var active = await _store.GetReportAccess("orders", "bob");
        Assert.True(active.Restricted);
        Assert.True(active.UserGranted);
        Assert.False((await _store.GetReportAccess("orders", "carol")).UserGranted);

        Assert.False(await _store.RevokeReportUser("orders", "Bob"));
        Assert.True(await _store.RevokeReportUser("orders", "bob"));
        Assert.False((await _store.GetReportAccess("orders", "bob")).UserGranted);
        await _store.SetReportRestricted("orders", false);
        Assert.False((await _store.GetReportAccess("orders", null)).Restricted);
    }

    public void Dispose() => _keepAlive.Dispose();

    private sealed class ConnectionFactory(Func<DbConnection> create) : IReportConnectionFactory
    {
        public DbConnection CreateConnection(string name) => create();
    }
}
