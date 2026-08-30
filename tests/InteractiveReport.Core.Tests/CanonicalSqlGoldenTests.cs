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
        throw new InvalidOperationException(
            $"DIALECT={dialect}\nMAIN={compiled.Main.Sql}\nMAIN_BINDINGS={Bindings(compiled.Main)}"
            + $"\nCOUNT={compiled.Count.Sql}\nCOUNT_BINDINGS={Bindings(compiled.Count)}");
    }

    [Fact]
    public async Task SqlServer_unsorted_paging_has_an_exact_fallback_order()
    {
        var compiled = await Compile(ReportDialect.SqlServer, sorted: false);
        throw new InvalidOperationException(
            $"MAIN={compiled.Main.Sql}\nMAIN_BINDINGS={Bindings(compiled.Main)}");
    }

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

    private static string Bindings(SqlResult result)
        => string.Join(",", result.Bindings.Select(value => value switch
        {
            string text => $"string:{text}",
            decimal number => $"decimal:{number}",
            long number => $"long:{number}",
            int number => $"int:{number}",
            _ => $"{value?.GetType().Name}:{value}",
        }));
}
