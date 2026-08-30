using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Planning;
using InteractiveReport.Core.Schema;

namespace InteractiveReport.Core.Tests;

public sealed class PivotContinuationTests
{
    private static readonly ReportDefinition Definition = new()
    {
        Name = "pivot-continuation",
        Connection = "unused",
        Dialect = ReportDialect.Sqlite,
        Sql = "SELECT CUSTOMER, STATUS, AMOUNT FROM ORDERS",
    };

    private static readonly ReportSchema Schema = ReportSchema.Create(
        Definition.Name,
        [
            TestFixtures.Col("CUSTOMER", typeof(string)),
            TestFixtures.Col("STATUS", typeof(string)),
            TestFixtures.Col("AMOUNT", typeof(decimal)),
        ]);

    [Fact]
    public async Task Discovery_is_memoized_and_descendants_bind_the_registered_contract()
    {
        var shipped = TestFixtures.PivotCellId("pivot", "ir1", "SHIPPED");
        var document = Document(
            active: "child",
            child: new ReportTable
            {
                From = "pivot",
                Composables =
                [
                    new TableComposable
                    {
                        Kind = "compute",
                        Computed = [new ComputedColumn { Id = "ir2", Expr = $"`{shipped}` + 1" }],
                    },
                ],
            });
        var discoveries = 0;
        var compiler = Compiler(
            document,
            () =>
            {
                discoveries++;
                return [new PivotGroup(["Acme"], ["SHIPPED"], 2, [12000m])];
            });

        var child = await compiler.Compile("child", default);
        _ = await compiler.Compile("pivot", default);
        _ = await compiler.Compile("child", default);

        Assert.Equal(1, discoveries);
        Assert.True(child.Export.Bound.Relation.Output.TryGetValue(shipped, out _));
        Assert.True(child.Export.Bound.Relation.Output.TryGetValue("ir2", out _));
        Assert.Contains("compute(ir2)", BoundRelationPlanDebug.Render(child.Export.Bound.Relation));
    }

    [Fact]
    public async Task Changing_the_discovered_key_set_does_not_rename_existing_cells()
    {
        var firstDocument = Document(active: "pivot");
        var first = await Compiler(
            firstDocument,
            () => [new PivotGroup(["Acme"], ["B"], 1, [10m])])
            .Compile("pivot", default);
        var firstB = CellIds(first)["B"];

        var changedDocument = Document(active: "pivot");
        changedDocument.Tables!["pivot"].Schema =
        [
            new ColumnInfo(firstB, "B", "number", false),
        ];
        var changed = await Compiler(
            changedDocument,
            () =>
            [
                new PivotGroup(["Acme"], ["A"], 1, [5m]),
                new PivotGroup(["Acme"], ["B"], 1, [10m]),
            ])
            .Compile("pivot", default);
        var changedIds = CellIds(changed);

        Assert.Equal(firstB, changedIds["B"]);
        Assert.NotEqual(changedIds["A"], changedIds["B"]);
        Assert.Equal(3, changed.Export.Bound.Relation.Output.Count);
    }

    private static Dictionary<string, string> CellIds(CompiledComposableTable table)
        => table.Export.Bound.Relation.Output.Columns
            .Where(column => column.Lineage is BoundPivotCellColumnLineage)
            .ToDictionary(
                column => column.EffectiveLabel,
                column => column.LogicalId,
                StringComparer.Ordinal);

    private static ReportState Document(string active, ReportTable? child = null)
    {
        var tables = new Dictionary<string, ReportTable>
        {
            ["pivot"] = new()
            {
                From = "definition",
                Composables =
                [
                    new TableComposable
                    {
                        Kind = "pivot",
                        Rows = ["CUSTOMER"],
                        Cols = ["STATUS"],
                        Values = [new MetricRule { Id = "ir1", Col = "AMOUNT", Fn = AggregateFn.Sum }],
                    },
                ],
            },
        };
        if (child is not null) tables["child"] = child;
        return new ReportState { ActiveTable = active, Tables = tables };
    }

    private static ComposableTableCompiler Compiler(
        ReportState document,
        Func<List<PivotGroup>> groups)
        => new(
            Definition,
            document,
            Schema,
            new DateTime(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc),
            (_, _, _, _, _) => Task.FromResult(groups()));
}
