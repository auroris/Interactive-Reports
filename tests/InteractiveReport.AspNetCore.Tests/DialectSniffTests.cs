using System.Data;
using System.Data.Common;
using InteractiveReport.Core.Model;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace InteractiveReport.AspNetCore.Tests;

/// <summary>
/// Dialect sniffing: an unopened connection's type names its dialect. The real
/// SQLite type proves the happy path and the base-type walk; the other providers'
/// type names are covered through the string hook (this project deliberately does
/// not reference them — the live battery asserts the real types).
/// </summary>
public sealed class DialectSniffTests
{
    [Fact]
    public void Sqlite_connections_sniff_to_the_sqlite_dialect()
    {
        Assert.Equal(ReportDialect.Sqlite, ProviderCatalog.FromConnectionType(typeof(SqliteConnection)));
    }

    [Fact]
    public void Provider_subclasses_sniff_through_the_inheritance_chain()
    {
        Assert.Equal(ReportDialect.Sqlite, ProviderCatalog.FromConnectionType(typeof(DerivedSqliteConnection)));
    }

    [Fact]
    public void Unrecognized_connection_types_do_not_sniff()
    {
        Assert.Null(ProviderCatalog.FromConnectionType(typeof(FakeConnection)));
    }

    [Theory]
    [InlineData("Microsoft.Data.SqlClient.SqlConnection", ReportDialect.SqlServer)]
    [InlineData("System.Data.SqlClient.SqlConnection", ReportDialect.SqlServer)]
    [InlineData("Npgsql.NpgsqlConnection", ReportDialect.Postgres)]
    [InlineData("Oracle.ManagedDataAccess.Client.OracleConnection", ReportDialect.Oracle)]
    [InlineData("Oracle.DataAccess.Client.OracleConnection", ReportDialect.Oracle)]
    [InlineData("Microsoft.Data.Sqlite.SqliteConnection", ReportDialect.Sqlite)]
    [InlineData("System.Data.SQLite.SQLiteConnection", ReportDialect.Sqlite)]
    public void The_type_name_table_covers_every_supported_provider(string typeName, ReportDialect expected)
    {
        Assert.Equal(expected, ProviderCatalog.FromTypeFullName(typeName));
    }

    [Fact]
    public void Undeclared_code_registered_connections_sniff_once_and_cache()
    {
        var created = 0;
        var registry = Registry(("sniffed", _ => { created++; return new SqliteConnection("Data Source=:memory:"); }));

        Assert.Equal(ReportDialect.Sqlite, registry.ResolveDialect("sniffed"));
        Assert.Equal(ReportDialect.Sqlite, registry.ResolveDialect("sniffed"));
        Assert.Equal(1, created);
    }

    [Fact]
    public void Unrecognized_wrapper_types_name_the_declaring_overload()
    {
        var registry = Registry(("wrapped", _ => new FakeConnection()));

        var error = Assert.Throws<InvalidOperationException>(() => registry.ResolveDialect("wrapped"));

        Assert.Contains("FakeConnection", error.Message);
        Assert.Contains("AddConnection(\"wrapped\", factory, ReportDialect.", error.Message);
    }

    [Fact]
    public void Declared_dialects_win_without_invoking_the_factory()
    {
        var registry = Registry(declared: ("declared", ReportDialect.Oracle),
            factory: ("declared", _ => throw new InvalidOperationException("factory must not run")));

        Assert.Equal(ReportDialect.Oracle, registry.ResolveDialect("declared"));
    }

    private static ReportConnectionRegistry Registry(
        params (string Name, Func<IServiceProvider, DbConnection> Factory)[] factories)
        => Registry(declared: null, factories);

    private static ReportConnectionRegistry Registry(
        (string Name, ReportDialect Dialect)? declared,
        params (string Name, Func<IServiceProvider, DbConnection> Factory)[] factory)
    {
        var factories = new Dictionary<string, Func<IServiceProvider, DbConnection>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, create) in factory) factories[name] = create;
        var dialects = new Dictionary<string, ReportDialect>(StringComparer.OrdinalIgnoreCase);
        if (declared is { } d) dialects[d.Name] = d.Dialect;
        return new ReportConnectionRegistry(
            factories, dialects, NullServices.Instance, new ConfigurationBuilder().Build());
    }

    private sealed class NullServices : IServiceProvider
    {
        public static readonly NullServices Instance = new();
        public object? GetService(Type serviceType) => null;
    }

    private sealed class DerivedSqliteConnection() : SqliteConnection("Data Source=:memory:");

    internal sealed class FakeConnection : DbConnection
    {
        public override string ConnectionString { get; set; } = "";
        public override string Database => "";
        public override string DataSource => "";
        public override string ServerVersion => "";
        public override ConnectionState State => ConnectionState.Closed;
        public override void ChangeDatabase(string databaseName) { }
        public override void Close() { }
        public override void Open() { }
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
    }
}
