using System.Data.Common;
using InteractiveReport.Core.Authorization;
using InteractiveReport.Core.Execution;

namespace InteractiveReport.Core.Tests;

/// <summary>
/// Dialect-neutral administrator-grant contract. Identities are opaque keys compared
/// ordinally: a case variant is a different account on every database, whatever its
/// default collation says about the two strings.
/// </summary>
public abstract class AdministratorStoreCorpus : IAsyncLifetime
{
    private SqlAdministratorStore? _store;

    /// <summary>Build (or skip) the store. Called once per test via the Store property.</summary>
    protected abstract SqlAdministratorStore CreateStore();

    /// <summary>Removes whatever the store created; live targets drop their scratch table here after every test.</summary>
    protected virtual Task CleanUp() => Task.CompletedTask;

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    Task IAsyncLifetime.DisposeAsync() => CleanUp();

    private SqlAdministratorStore Store => _store ??= CreateStore();

    private static string[] Sorted(IEnumerable<string> identities)
        => identities.Order(StringComparer.Ordinal).ToArray();

    [SkippableFact]
    public async Task Grants_are_idempotent_case_sensitive_and_revocable()
    {
        Assert.Empty(await Store.List());
        Assert.False(await Store.IsAdministrator("alice"));

        await Store.Grant(" Alice ");
        await Store.Grant("Alice");
        await Store.Grant("bob");

        Assert.Equal(["Alice", "bob"], Sorted(await Store.List()));
        Assert.True(await Store.IsAdministrator("Alice"));
        Assert.True(await Store.IsAdministrator(" Alice "));
        // Identities are opaque: a case variant is a different account.
        Assert.False(await Store.IsAdministrator("alice"));

        // Granting the variant adds a row rather than refreshing the existing one, even where
        // the database's default collation would call the two strings equal.
        await Store.Grant("alice");
        Assert.Equal(["Alice", "alice", "bob"], Sorted(await Store.List()));
        Assert.True(await Store.IsAdministrator("alice"));

        // Revoking a variant that was never granted removes nothing.
        Assert.False(await Store.Revoke("ALICE"));
        Assert.True(await Store.IsAdministrator("Alice"));
        Assert.True(await Store.Revoke("Alice"));
        Assert.False(await Store.Revoke("Alice"));
        Assert.False(await Store.IsAdministrator("Alice"));
        Assert.True(await Store.IsAdministrator("alice"));
        Assert.Equal(["alice", "bob"], Sorted(await Store.List()));
    }

    [SkippableFact]
    public async Task Blank_identities_are_rejected_before_reaching_the_database()
    {
        // Store is touched outside the assertion lambdas so an unconfigured live target
        // skips instead of failing the exception-type check.
        var store = Store;
        await Assert.ThrowsAsync<ArgumentException>(() => store.Grant(" "));
        await Assert.ThrowsAsync<ArgumentException>(() => store.IsAdministrator(""));
        await Assert.ThrowsAsync<ArgumentException>(() => store.Revoke(""));
        Assert.Empty(await store.List());
    }

    protected sealed class FixedConnectionFactory(Func<DbConnection> open) : IReportConnectionFactory
    {
        public DbConnection CreateConnection(string name) => open();
    }
}
