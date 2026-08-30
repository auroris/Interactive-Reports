using InteractiveReport.Core.Composition;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Schema;
using SqlKata;

namespace InteractiveReport.Core.Tests;

public sealed class CanonicalSqlGoldenTests
{
    private static readonly DateTime EvaluationUtcNow =
        new(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc);

    public static IEnumerable<object[]> Dialects()
    {
        yield return [ReportDialect.Sqlite];
        yield return [ReportDialect.SqlServer];
        yield return [ReportDialect.Postgres];
        yield return [ReportDialect.Oracle];
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task Canonical_main_and_count_sql_are_exact_per_dialect(ReportDialect dialect)
    {
        var compiled = await Compile(dialect, sorted: true);
        var expected = Expected(dialect);

        Assert.Equal(expected.MainSql, compiled.Main.Sql);
        Assert.Equal(expected.MainBindings, compiled.Main.Bindings);
        Assert.Equal(expected.CountSql, compiled.Count.Sql);
        Assert.Equal(expected.CountBindings, compiled.Count.Bindings);
        Assert.DoesNotContain("ORDER BY", compiled.Count.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SqlServer_unsorted_paging_has_an_exact_fallback_order()
    {
        var compiled = await Compile(ReportDialect.SqlServer, sorted: false);

        Assert.Equal(
            "SELECT [__irc0] FROM (SELECT * FROM (SELECT * FROM (SELECT [CUSTOMER] AS [__irc0], [NOTES] AS [__irc1], [AMOUNT] AS [__irc2] FROM (SELECT CUSTOMER, NOTES, AMOUNT FROM ORDERS) ir_base) AS [ir_rel_0] WHERE ([__irc2] >= @p0)) AS [ir_rel_1] WHERE (LOWER([__irc0]) like @p1 OR LOWER([__irc1]) like @p2)) AS [ir_rel_0] ORDER BY (SELECT 0) OFFSET @p3 ROWS FETCH NEXT @p4 ROWS ONLY",
            compiled.Main.Sql);
        Assert.Equal([10m, "%acme%", "%acme%", 5L, 5], compiled.Main.Bindings);
    }

    private static GoldenSnapshot Expected(ReportDialect dialect)
        => dialect switch
        {
            ReportDialect.Sqlite => new(
                "SELECT \"__irc0\" FROM (SELECT * FROM (SELECT * FROM (SELECT \"CUSTOMER\" AS \"__irc0\", \"NOTES\" AS \"__irc1\", \"AMOUNT\" AS \"__irc2\" FROM (SELECT CUSTOMER, NOTES, AMOUNT FROM ORDERS) ir_base) AS \"ir_rel_0\" WHERE (\"__irc2\" >= @p0)) AS \"ir_rel_1\" WHERE (LOWER(\"__irc0\") like @p1 OR LOWER(\"__irc1\") like @p2)) AS \"ir_rel_0\" ORDER BY \"__irc1\" DESC NULLS FIRST LIMIT @p3 OFFSET @p4",
                [10m, "%acme%", "%acme%", 5, 5L],
                "SELECT COUNT(*) AS \"count\" FROM (SELECT * FROM (SELECT * FROM (SELECT \"CUSTOMER\" AS \"__irc0\", \"NOTES\" AS \"__irc1\", \"AMOUNT\" AS \"__irc2\" FROM (SELECT CUSTOMER, NOTES, AMOUNT FROM ORDERS) ir_base) AS \"ir_rel_0\" WHERE (\"__irc2\" >= @p0)) AS \"ir_rel_1\" WHERE (LOWER(\"__irc0\") like @p1 OR LOWER(\"__irc1\") like @p2)) AS \"ir_rel_0\"",
                [10m, "%acme%", "%acme%"]),
            ReportDialect.SqlServer => new(
                "SELECT [__irc0] FROM (SELECT * FROM (SELECT * FROM (SELECT [CUSTOMER] AS [__irc0], [NOTES] AS [__irc1], [AMOUNT] AS [__irc2] FROM (SELECT CUSTOMER, NOTES, AMOUNT FROM ORDERS) ir_base) AS [ir_rel_0] WHERE ([__irc2] >= @p0)) AS [ir_rel_1] WHERE (LOWER([__irc0]) like @p1 OR LOWER([__irc1]) like @p2)) AS [ir_rel_0] ORDER BY CASE WHEN [__irc1] IS NULL THEN 0 ELSE 1 END, [__irc1] DESC OFFSET @p3 ROWS FETCH NEXT @p4 ROWS ONLY",
                [10m, "%acme%", "%acme%", 5L, 5],
                "SELECT COUNT(*) AS [count] FROM (SELECT * FROM (SELECT * FROM (SELECT [CUSTOMER] AS [__irc0], [NOTES] AS [__irc1], [AMOUNT] AS [__irc2] FROM (SELECT CUSTOMER, NOTES, AMOUNT FROM ORDERS) ir_base) AS [ir_rel_0] WHERE ([__irc2] >= @p0)) AS [ir_rel_1] WHERE (LOWER([__irc0]) like @p1 OR LOWER([__irc1]) like @p2)) AS [ir_rel_0]",
                [10m, "%acme%", "%acme%"]),
            ReportDialect.Postgres => new(
                "SELECT \"__irc0\" FROM (SELECT * FROM (SELECT * FROM (SELECT \"CUSTOMER\" AS \"__irc0\", \"NOTES\" AS \"__irc1\", \"AMOUNT\" AS \"__irc2\" FROM (SELECT CUSTOMER, NOTES, AMOUNT FROM ORDERS) ir_base) AS \"ir_rel_0\" WHERE (\"__irc2\" >= @p0)) AS \"ir_rel_1\" WHERE (\"__irc0\" ilike @p1 OR \"__irc1\" ilike @p2)) AS \"ir_rel_0\" ORDER BY \"__irc1\" DESC NULLS FIRST LIMIT @p3 OFFSET @p4",
                [10m, "%Acme%", "%Acme%", 5, 5L],
                "SELECT COUNT(*) AS \"count\" FROM (SELECT * FROM (SELECT * FROM (SELECT \"CUSTOMER\" AS \"__irc0\", \"NOTES\" AS \"__irc1\", \"AMOUNT\" AS \"__irc2\" FROM (SELECT CUSTOMER, NOTES, AMOUNT FROM ORDERS) ir_base) AS \"ir_rel_0\" WHERE (\"__irc2\" >= @p0)) AS \"ir_rel_1\" WHERE (\"__irc0\" ilike @p1 OR \"__irc1\" ilike @p2)) AS \"ir_rel_0\"",
                [10m, "%Acme%", "%Acme%"]),
            ReportDialect.Oracle => new(
                "SELECT \"__irc0\" FROM (SELECT * FROM (SELECT * FROM (SELECT \"CUSTOMER\" AS \"__irc0\", \"NOTES\" AS \"__irc1\", \"AMOUNT\" AS \"__irc2\" FROM (SELECT CUSTOMER, NOTES, AMOUNT FROM ORDERS) ir_base) \"ir_rel_0\" WHERE (\"__irc2\" >= :p0)) \"ir_rel_1\" WHERE (LOWER(\"__irc0\") like :p1 OR LOWER(\"__irc1\") like :p2)) \"ir_rel_0\" ORDER BY \"__irc1\" DESC NULLS FIRST OFFSET :p3 ROWS FETCH NEXT :p4 ROWS ONLY",
                [10m, "%acme%", "%acme%", 5L, 5],
                "SELECT COUNT(*) \"count\" FROM (SELECT * FROM (SELECT * FROM (SELECT \"CUSTOMER\" AS \"__irc0\", \"NOTES\" AS \"__irc1\", \"AMOUNT\" AS \"__irc2\" FROM (SELECT CUSTOMER, NOTES, AMOUNT FROM ORDERS) ir_base) \"ir_rel_0\" WHERE (\"__irc2\" >= :p0)) \"ir_rel_1\" WHERE (LOWER(\"__irc0\") like :p1 OR LOWER(\"__irc1\") like :p2)) \"ir_rel_0\"",
                [10m, "%acme%", "%acme%"]),
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null),
        };

    private static async Task<(SqlResult Main, SqlResult Count)> Compile(
        ReportDialect dialect,
        bool sorted)
    {
        var definition = new ReportDefinition
        {
            Name = "orders",
            Connection = "unused",
            Dialect = dialect,
            Sql = "SELECT CUSTOMER, NOTES, AMOUNT FROM ORDERS",
        };
        var schema = ReportSchema.Create(
            definition.Name,
            [
                new ColumnModel { Name = "CUSTOMER", Label = "Customer", ClrType = typeof(string) },
                new ColumnModel { Name = "NOTES", Label = "Notes", ClrType = typeof(string) },
                new ColumnModel { Name = "AMOUNT", Label = "Amount", ClrType = typeof(decimal) },
            ]);
        var composables = new List<TableComposable>
        {
            new()
            {
                Kind = "filter",
                Filters = [new FilterRule { Expr = "AMOUNT >= 10" }],
            },
            new() { Kind = "select", Columns = ["CUSTOMER"] },
        };
        if (sorted)
            composables.Add(new TableComposable
            {
                Kind = "sort",
                Sorts =
                [
                    new SortRule
                    {
                        Col = "NOTES",
                        Dir = SortDir.Desc,
                        Nulls = NullPlacement.First,
                    },
                ],
            });
        var document = new ReportState
        {
            Search = "Acme",
            Page = new PageRequest { Index = 2, Size = 5 },
            ActiveTable = "result",
            Tables = new Dictionary<string, ReportTable>
            {
                ["result"] = new() { From = "definition", Composables = composables },
            },
        };
        var planner = new ComposableTableCompiler(
            definition,
            document,
            schema,
            EvaluationUtcNow,
            (_, _, _, _, _) => throw new InvalidOperationException("No Pivot expected."));
        var plan = planner.CompleteForTarget(await planner.Compile("result", default));
        var compiler = DialectSupport.GetCompiler(dialect);
        return (
            compiler.Compile(plan.ExecutionBundle.MainRows.Query),
            compiler.Compile(plan.ExecutionBundle.Count));
    }

    private sealed record GoldenSnapshot(
        string MainSql,
        object?[] MainBindings,
        string CountSql,
        object?[] CountBindings);
}
