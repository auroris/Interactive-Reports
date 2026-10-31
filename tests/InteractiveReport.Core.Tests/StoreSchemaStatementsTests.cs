using System.Data.Common;
using InteractiveReport.Core.Authorization;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.SavedReports;
using Microsoft.Data.Sqlite;

namespace InteractiveReport.Core.Tests;

/// <summary>
/// The stores' public schema statements, and their use of a table an administrator created for an account
/// that may not run DDL. A SQLite connection opened read-only stands in for that account: it refuses the
/// DDL the store needs and allows the read, which is the shape Oracle and PostgreSQL have without their rights.
/// </summary>
public sealed class StoreSchemaStatementsTests
{
    private const string Connection = "store";

    [Theory]
    [InlineData(ReportDialect.Sqlite, 4)]
    [InlineData(ReportDialect.SqlServer, 4)]
    [InlineData(ReportDialect.Postgres, 4)]
    [InlineData(ReportDialect.Oracle, 4)]
    [InlineData(ReportDialect.Oracle11g, 6)]
    public void Saved_report_statements_are_the_table_its_identity_and_its_indexes(ReportDialect dialect, int count)
    {
        var statements = SqlSavedReportStore.SchemaStatements(new SavedReportStoreConfig(Connection, dialect, TableName: "IR_VIEWS"));
        Assert.Equal(count, statements.Count);
        Assert.Contains("IR_VIEWS", statements[0]);
        Assert.Contains("CREATE TABLE", statements[0]);
        Assert.Contains("IR_VIEWS_TITLE_UX", statements[^3]);
        Assert.Equal(dialect == ReportDialect.Oracle11g, statements.Any(sql => sql.Contains("CREATE OR REPLACE TRIGGER", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_table_name_that_is_not_a_plain_identifier_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() => SqlSavedReportStore.SchemaStatements(new SavedReportStoreConfig(Connection, ReportDialect.Sqlite, TableName: "x; DROP")));
        Assert.Throws<InvalidOperationException>(() => SqlAdministratorStore.SchemaStatements(new AdministratorStoreConfig(Connection, ReportDialect.Sqlite, TableName: "x; DROP")));
    }

    [Fact]
    public async Task A_saved_report_store_whose_ddl_is_refused_uses_a_table_made_with_the_statements()
    {
        var file = Path.Combine(Directory.CreateTempSubdirectory("ir-schema-").FullName, "store.db");
        var config = new SavedReportStoreConfig(Connection, ReportDialect.Sqlite);
        foreach (var sql in SqlSavedReportStore.SchemaStatements(config)) await Run(file, sql);
        // The missing index gives the store's guarded DDL work to do, so a read-only connection really refuses it.
        await Run(file, "DROP INDEX IR_SAVED_REPORTS_DEFAULT_UX");
        var store = new SqlSavedReportStore(() => config, new Factory($"Data Source={file};Mode=ReadOnly;Pooling=False"));
        Assert.Empty(await store.ListFamily("tasks"));
    }

    [Fact]
    public async Task An_administrator_store_whose_ddl_is_refused_uses_a_table_made_with_its_statements()
    {
        var file = Path.Combine(Directory.CreateTempSubdirectory("ir-schema-").FullName, "store.db");
        var config = new AdministratorStoreConfig(Connection, ReportDialect.Sqlite, TableName: "IR_ADMINS");
        foreach (var sql in SqlAdministratorStore.SchemaStatements(config)) await Run(file, sql);
        var store = new SqlAdministratorStore(() => config, new Factory($"Data Source={file};Mode=ReadOnly;Pooling=False"));
        Assert.Empty(await store.List());
        Assert.Equal(config, store.Configuration);
    }

    [Fact]
    public async Task A_refused_store_without_its_table_still_fails_and_is_retried_rather_than_remembered()
    {
        var file = Path.Combine(Directory.CreateTempSubdirectory("ir-schema-").FullName, "store.db");
        await Run(file, "CREATE TABLE OTHER (ID INTEGER)");
        var config = new SavedReportStoreConfig(Connection, ReportDialect.Sqlite);
        var store = new SqlSavedReportStore(() => config, new Factory($"Data Source={file};Mode=ReadOnly;Pooling=False"));
        Assert.Equal(config, store.Configuration);
        await Assert.ThrowsAnyAsync<DbException>(() => store.ListFamily("tasks"));
        foreach (var sql in SqlSavedReportStore.SchemaStatements(config)) await Run(file, sql);
        Assert.Empty(await store.ListFamily("tasks"));
    }

    private static async Task Run(string file, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={file};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class Factory(string connectionString) : IReportConnectionFactory
    {
        public DbConnection CreateConnection(string name) => new SqliteConnection(connectionString);
        public void SetDetectedDialect(string name, ReportDialect dialect) { }
    }
}
