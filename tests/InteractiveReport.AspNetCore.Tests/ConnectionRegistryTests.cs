using System.Data.Common;
using InteractiveReport.Core.Model;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace InteractiveReport.AspNetCore.Tests;

/// <summary>
/// dataSource resolution: '=' discriminates a literal connection string from a
/// ConnectionStrings name, the provider ladder is explicit token → _ProviderName
/// companion → precise error, and resolved entries become content-addressed
/// synthetic connections whose name (and therefore schema-cache identity) shifts
/// when the underlying configuration changes.
/// </summary>
public sealed class ConnectionRegistryTests
{
    [Fact]
    public void Literal_data_sources_resolve_with_an_explicit_provider()
    {
        var registry = Registry();

        var (name, dialect) = registry.ResolveDataSource("Report 'orders'", "Data Source=:memory:", "sqlite");

        Assert.StartsWith("__ir:ds:", name);
        Assert.Equal(ReportDialect.Sqlite, dialect);
        using var connection = registry.CreateConnection(name);
        Assert.IsType<SqliteConnection>(connection);
        Assert.Equal("Data Source=:memory:", connection.ConnectionString);
    }

    [Fact]
    public void Named_data_sources_read_ConnectionStrings_and_its_provider_companion()
    {
        var registry = Registry(
            ("ConnectionStrings:AppDb", "Data Source=app.db"),
            ("ConnectionStrings:AppDb_ProviderName", "Microsoft.Data.Sqlite"));

        var (name, dialect) = registry.ResolveDataSource("Report 'orders'", "AppDb", provider: null);

        Assert.Equal(ReportDialect.Sqlite, dialect);
        using var connection = registry.CreateConnection(name);
        Assert.Equal("Data Source=app.db", connection.ConnectionString);
    }

    [Fact]
    public void A_missing_ConnectionStrings_name_is_never_treated_as_a_literal()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            Registry().ResolveDataSource("Report 'orders'", "umbracoDbDSN", provider: null));

        Assert.Contains("ConnectionStrings:umbracoDbDSN", error.Message);
        Assert.Contains("Report 'orders'", error.Message);
    }

    [Fact]
    public void A_literal_without_a_provider_lists_the_tokens()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            Registry().ResolveDataSource("Report 'orders'", "Data Source=app.db", provider: null));

        Assert.Contains("sqlite, sqlServer, postgres, oracle", error.Message);
        Assert.DoesNotContain("_ProviderName", error.Message);
    }

    [Fact]
    public void A_named_source_without_provider_information_suggests_the_companion_entry()
    {
        var registry = Registry(("ConnectionStrings:AppDb", "Data Source=app.db"));

        var error = Assert.Throws<InvalidOperationException>(() =>
            registry.ResolveDataSource("Report 'orders'", "AppDb", provider: null));

        Assert.Contains("AppDb_ProviderName", error.Message);
    }

    [Fact]
    public void An_unrecognized_provider_companion_lists_the_supported_invariants()
    {
        var registry = Registry(
            ("ConnectionStrings:AppDb", "Data Source=app.db"),
            ("ConnectionStrings:AppDb_ProviderName", "MySql.Data.MySqlClient"));

        var error = Assert.Throws<InvalidOperationException>(() =>
            registry.ResolveDataSource("Report 'orders'", "AppDb", provider: null));

        Assert.Contains("MySql.Data.MySqlClient", error.Message);
        Assert.Contains("Microsoft.Data.SqlClient", error.Message);
    }

    [Fact]
    public void An_explicit_provider_overrides_the_companion_entry()
    {
        var registry = Registry(
            ("ConnectionStrings:AppDb", "Data Source=app.db"),
            ("ConnectionStrings:AppDb_ProviderName", "MySql.Data.MySqlClient"));

        var (_, dialect) = registry.ResolveDataSource("Report 'orders'", "AppDb", "sqlite");

        Assert.Equal(ReportDialect.Sqlite, dialect);
    }

    [Fact]
    public void Synthetic_names_are_stable_for_identical_sources_and_shift_when_configuration_changes()
    {
        var source = new Dictionary<string, string?> { ["ConnectionStrings:AppDb"] = "Data Source=one.db" };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(source).Build();
        var registry = Registry(configuration);

        var (first, _) = registry.ResolveDataSource("Report 'a'", "AppDb", "sqlite");
        var (again, _) = registry.ResolveDataSource("Report 'b'", "AppDb", "sqlite");
        Assert.Equal(first, again);

        // A configuration edit re-addresses the source; the old entry stays valid for
        // in-flight requests, and the new name rolls the schema-cache identity.
        configuration["ConnectionStrings:AppDb"] = "Data Source=two.db";
        var (shifted, _) = registry.ResolveDataSource("Report 'a'", "AppDb", "sqlite");
        Assert.NotEqual(first, shifted);
        using var old = registry.CreateConnection(first);
        Assert.Equal("Data Source=one.db", old.ConnectionString);
        using var fresh = registry.CreateConnection(shifted);
        Assert.Equal("Data Source=two.db", fresh.ConnectionString);
    }

    [Fact]
    public void Unknown_connection_names_mention_both_registration_paths()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Registry().CreateConnection("ghost"));

        Assert.Contains("AddConnection(\"ghost\"", error.Message);
        Assert.Contains("dataSource", error.Message);
    }

    [Fact]
    public void Store_config_requires_a_named_connection_or_data_source_and_applies_the_prefix()
    {
        var factories = new Dictionary<string, Func<IServiceProvider, DbConnection>>(StringComparer.OrdinalIgnoreCase)
        {
            ["reports"] = _ => new SqliteConnection("Data Source=:memory:"),
        };
        var registry = new ReportConnectionRegistry(
            factories,
            new Dictionary<string, ReportDialect>(StringComparer.OrdinalIgnoreCase),
            NullServices.Instance,
            new ConfigurationBuilder().Build());

        var missing = Assert.Throws<InvalidOperationException>(() =>
            registry.ResolveStoreConfig(new SavedReportsOptions()));
        Assert.Contains("Saved-report storage is not configured", missing.Message);
        Assert.Contains("SavedReports:DataSource", missing.Message);

        var named = registry.ResolveStoreConfig(new SavedReportsOptions
        {
            Connection = "reports",
            TablePrefix = "APP_",
        });
        Assert.Equal("reports", named.ConnectionName);
        Assert.Equal(ReportDialect.Sqlite, named.Dialect);   // sniffed
        Assert.Equal("APP_IR_SAVED_REPORTS", named.TableName);

        var viaDataSource = registry.ResolveStoreConfig(new SavedReportsOptions
        {
            DataSource = "Data Source=saved.db",
            Provider = "sqlite",
            TableName = "SAVED",
        });
        Assert.StartsWith("__ir:ds:", viaDataSource.ConnectionName);
        Assert.Equal(ReportDialect.Sqlite, viaDataSource.Dialect);
        Assert.Equal("SAVED", viaDataSource.TableName);

        var invalidPrefix = Assert.Throws<InvalidOperationException>(() =>
            registry.ResolveStoreConfig(new SavedReportsOptions
            {
                Connection = "reports",
                TablePrefix = "bad prefix ",
            }));
        Assert.Contains("plain identifier", invalidPrefix.Message);

        var both = Assert.Throws<InvalidOperationException>(() => registry.ResolveStoreConfig(
            new SavedReportsOptions { DataSource = "Data Source=x.db", Connection = "reports" }));
        Assert.Contains("not both", both.Message);
    }

    private static ReportConnectionRegistry Registry(params (string Key, string Value)[] configuration)
        => Registry(new ConfigurationBuilder()
            .AddInMemoryCollection(configuration.ToDictionary(p => p.Key, p => (string?)p.Value))
            .Build());

    private static ReportConnectionRegistry Registry(IConfiguration configuration)
        => new(
            new Dictionary<string, Func<IServiceProvider, DbConnection>>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, ReportDialect>(StringComparer.OrdinalIgnoreCase),
            NullServices.Instance,
            configuration);

    private sealed class NullServices : IServiceProvider
    {
        public static readonly NullServices Instance = new();
        public object? GetService(Type serviceType) => null;
    }
}
