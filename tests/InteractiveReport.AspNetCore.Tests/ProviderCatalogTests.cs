using InteractiveReport.Core.Model;
using Microsoft.Data.Sqlite;

namespace InteractiveReport.AspNetCore.Tests;

/// <summary>
/// The provider catalog: tokens fix connection types and dialects; providers other
/// than SQLite load by reflection from the host's dependency graph. This test
/// project deliberately references only Microsoft.Data.Sqlite, so the missing-
/// assembly paths here are the real ones a consumer hits, package hints included.
/// </summary>
public sealed class ProviderCatalogTests
{
    [Theory]
    [InlineData("sqlite", ReportDialect.Sqlite)]
    [InlineData("SQLITE", ReportDialect.Sqlite)]
    [InlineData("sqlServer", ReportDialect.SqlServer)]
    [InlineData("sqlserver", ReportDialect.SqlServer)]
    [InlineData("postgres", ReportDialect.Postgres)]
    [InlineData("oracle", ReportDialect.Oracle)]
    public void Tokens_resolve_dialects_case_insensitively(string token, ReportDialect expected)
    {
        Assert.True(ProviderCatalog.TryGetDialect(token, out var dialect));
        Assert.Equal(expected, dialect);
        Assert.NotNull(ProviderCatalog.CanonicalToken(token));
    }

    [Fact]
    public void Unknown_tokens_do_not_resolve()
    {
        Assert.False(ProviderCatalog.TryGetDialect("mongo", out _));
        Assert.Null(ProviderCatalog.CanonicalToken("mongo"));
        Assert.Equal("sqlite, sqlServer, postgres, oracle", ProviderCatalog.TokenList);
    }

    [Theory]
    [InlineData("Microsoft.Data.Sqlite", "sqlite")]
    [InlineData("Microsoft.Data.SqlClient", "sqlServer")]
    [InlineData("System.Data.SqlClient", "sqlServer")]
    [InlineData("Npgsql", "postgres")]
    [InlineData("Oracle.ManagedDataAccess.Client", "oracle")]
    public void Provider_invariant_names_map_to_tokens(string invariant, string expected)
    {
        Assert.Equal(expected, ProviderCatalog.TokenForInvariantName(invariant));
    }

    [Fact]
    public void Unrecognized_invariant_names_return_null()
    {
        Assert.Null(ProviderCatalog.TokenForInvariantName("MySql.Data.MySqlClient"));
    }

    [Fact]
    public void Sqlite_activation_returns_an_unopened_connection_with_the_string_set()
    {
        using var connection = ProviderCatalog.CreateConnection(
            "sqlite", "Data Source=:memory:", "Report 'orders'");

        Assert.IsType<SqliteConnection>(connection);
        Assert.Equal("Data Source=:memory:", connection.ConnectionString);
        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
    }

    [Theory]
    [InlineData("sqlServer", "Microsoft.Data.SqlClient")]
    [InlineData("postgres", "Npgsql")]
    [InlineData("oracle", "Oracle.ManagedDataAccess.Core")]
    public void Missing_provider_assemblies_fail_naming_the_package(string token, string package)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            ProviderCatalog.CreateConnection(token, "ignored", "Report 'orders'"));

        Assert.Contains("Report 'orders'", error.Message);
        Assert.Contains($"Add a package reference to {package}", error.Message);
    }

    [Fact]
    public void A_rejected_connection_string_names_the_owner()
    {
        // Microsoft.Data.Sqlite validates keywords in the setter.
        var error = Assert.Throws<InvalidOperationException>(() =>
            ProviderCatalog.CreateConnection("sqlite", "Garbage=true", "SavedReports"));

        Assert.Contains("SavedReports", error.Message);
        Assert.Contains("rejected the connection string", error.Message);
    }
}
