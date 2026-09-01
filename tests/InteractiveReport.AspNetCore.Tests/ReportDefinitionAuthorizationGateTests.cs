using System.Security.Claims;
using InteractiveReport.Core.Definitions;
using InteractiveReport.Core.Model;
using Microsoft.Extensions.Options;

namespace InteractiveReport.AspNetCore.Tests;

/// <summary>
/// Pins the report-level authorization gate to the cheap envelope lookup. A definition store
/// that also implements <see cref="IReportDefinitionAuthorizationStore"/> must be able to deny a
/// caller before its executable definition is loaded: loading is where the built-in store
/// deep-copies, validates, and resolves the connection (which may invoke a host connection
/// factory), and where a replacement store would go to its database. Collapsing this into a
/// single <see cref="IReportDefinitionStore.Find"/> call would let an unauthorized caller drive
/// that work and turn a misconfigured report's exception into an observable server error.
/// </summary>
public sealed class ReportDefinitionAuthorizationGateTests
{
    [Fact]
    public async Task Denied_caller_is_rejected_without_loading_the_executable_definition()
    {
        var store = new GatedDefinitionStore();
        var service = new ReportAuthorizationService(new InteractiveReportLogging());

        var resolved = await service.ResolveDefinition("orders", Anonymous(store));

        Assert.Equal(ReportAuthorizationFailureKind.Unauthenticated, resolved.Failure?.Kind);
        Assert.Null(resolved.Definition);
        Assert.Equal(1, store.AuthorizationLookups);
        Assert.Equal(0, store.DefinitionLoads);
    }

    [Fact]
    public async Task Authorized_caller_still_loads_the_executable_definition()
    {
        var store = new GatedDefinitionStore();
        var service = new ReportAuthorizationService(new InteractiveReportLogging());

        var resolved = await service.ResolveDefinition("orders", Authenticated(store));

        Assert.Null(resolved.Failure);
        Assert.Equal("orders", resolved.Definition?.Name);
        Assert.Equal(1, store.AuthorizationLookups);
        Assert.Equal(1, store.DefinitionLoads);
    }

    [Fact]
    public async Task A_misconfigured_report_stays_hidden_behind_the_gate()
    {
        // Loading throws the way the built-in store throws for a report naming an unknown
        // connection. The denied caller must still see the ordinary authentication failure
        // rather than the definition's configuration error.
        var store = new GatedDefinitionStore { FailDefinitionLoad = true };
        var service = new ReportAuthorizationService(new InteractiveReportLogging());

        var resolved = await service.ResolveDefinition("orders", Anonymous(store));

        Assert.Equal(ReportAuthorizationFailureKind.Unauthenticated, resolved.Failure?.Kind);
        Assert.Equal(0, store.DefinitionLoads);
    }

    private static InteractiveReportRequestContext Anonymous(GatedDefinitionStore store)
        => Context(store, new ClaimsPrincipal(new ClaimsIdentity()));

    private static InteractiveReportRequestContext Authenticated(GatedDefinitionStore store)
        => Context(store, new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "alice")], "test")));

    private static InteractiveReportRequestContext Context(
        GatedDefinitionStore store,
        ClaimsPrincipal user)
        => new()
        {
            User = user,
            RequestServices = new StubServices(store),
            TraceIdentifier = "trace",
        };

    /// <summary>A store that records each phase and can fail the expensive one on demand.</summary>
    private sealed class GatedDefinitionStore : IReportDefinitionStore, IReportDefinitionAuthorizationStore
    {
        public int AuthorizationLookups { get; private set; }
        public int DefinitionLoads { get; private set; }
        public bool FailDefinitionLoad { get; init; }

        public ValueTask<ReportDefinitionAuthorization?> FindAuthorization(
            string name,
            CancellationToken ct = default)
        {
            AuthorizationLookups++;
            return ValueTask.FromResult<ReportDefinitionAuthorization?>(new(name, null));
        }

        public ValueTask<ReportDefinition?> Find(string name, CancellationToken ct = default)
        {
            DefinitionLoads++;
            if (FailDefinitionLoad)
                throw new InvalidOperationException(
                    $"Report '{name}': connection 'missing' is not registered.");
            return ValueTask.FromResult<ReportDefinition?>(new ReportDefinition
            {
                Name = name,
                Connection = "db",
                Dialect = ReportDialect.Sqlite,
                Sql = "select 1 as ID",
            });
        }
    }

    private sealed class StubServices(GatedDefinitionStore store) : IServiceProvider
    {
        private readonly OptionsMonitorStub _options = new(new InteractiveReportOptions());

        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(IReportDefinitionStore)) return store;
            if (serviceType == typeof(IOptionsMonitor<InteractiveReportOptions>)) return _options;
            return null;
        }
    }

    private sealed class OptionsMonitorStub(InteractiveReportOptions options)
        : IOptionsMonitor<InteractiveReportOptions>
    {
        public InteractiveReportOptions CurrentValue => options;
        public InteractiveReportOptions Get(string? name) => options;
        public IDisposable? OnChange(Action<InteractiveReportOptions, string?> listener) => null;
    }
}
