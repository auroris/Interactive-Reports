using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Schema;

namespace InteractiveReport.Core.Tests;

public sealed class IdentifierTortureTests : IClassFixture<SqliteE2EFixture>
{
    private readonly ReportExecutor _executor;

    public IdentifierTortureTests(SqliteE2EFixture database)
        => _executor = new ReportExecutor(database, new SchemaCache());

    [Fact]
    public async Task Pathological_column_names_round_trip_through_sqlite()
    {
        var definition = new ReportDefinition
        {
            Name = "identifier-torture-Sqlite",
            Connection = "E2E",
            Dialect = ReportDialect.Sqlite,
            Sql = IdentifierTortureCorpus.DefinitionSql(ReportDialect.Sqlite, "ORDERS"),
        };

        await IdentifierTortureCorpus.AssertRoundTrip(_executor, definition);
    }
}
