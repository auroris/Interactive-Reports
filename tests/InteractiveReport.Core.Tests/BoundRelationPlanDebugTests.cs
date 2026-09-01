using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Planning;
using InteractiveReport.Core.Schema;

namespace InteractiveReport.Core.Tests;

public sealed class BoundRelationPlanDebugTests
{
    [Fact]
    public async Task Compiler_produces_a_stable_golden_bound_plan()
    {
        var definition = new ReportDefinition
        {
            Name = "orders",
            Connection = "unused",
            Dialect = ReportDialect.Sqlite,
            Sql = "SELECT REGION, AMOUNT FROM ORDERS",
        };
        var schema = ReportSchema.Create(
            definition.Name,
            [
                new ColumnModel { Name = "REGION", Label = "Region", ClrType = typeof(string) },
                new ColumnModel { Name = "AMOUNT", Label = "Amount", ClrType = typeof(decimal) },
            ]);
        var document = new ReportState
        {
            ActiveTable = "summary",
            Tables = new Dictionary<string, ReportTable>
            {
                ["summary"] = new()
                {
                    From = "definition",
                    Composables =
                    [
                        new TableComposable
                        {
                            Kind = "filter",
                            Filters = [new FilterRule { Expr = "ir2 > 10" }],
                        },
                        new TableComposable
                        {
                            Kind = "labels",
                            Labels = new Dictionary<string, string> { ["ir2"] = "Net" },
                        },
                        new TableComposable
                        {
                            Kind = "compute",
                            Computed = [new ComputedColumn { Id = "ir2", Expr = "ir1 - 5" }],
                        },
                        new TableComposable
                        {
                            Kind = "group",
                            By = ["REGION"],
                            Values = [new MetricRule { Id = "ir1", Col = "AMOUNT", Fn = AggregateFn.Sum }],
                        },
                    ],
                },
            },
        };
        var compiler = new ComposableTableCompiler(
            definition,
            document,
            schema,
            new DateTime(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc),
            (_, _, _, _, _) => throw new InvalidOperationException("No Pivot expected."));

        var table = await compiler.Compile("summary", default);

        Assert.Equal(
            """
            metadata path=tables.summary output=orders#summary
              REGION:text label=Region lineage=pass:REGION mask=- formatSource=-
              __count:number label=Count lineage=aggregate:Count:* mask=- formatSource=-
              ir1:number label=sum(Amount) lineage=aggregate:Sum:AMOUNT mask=- formatSource=-
              ir2:number label=Net lineage=compute:ir1 mask=- formatSource=-
              filter(1) path=tables.summary.composables[0] output=orders#summary
                REGION:text label=Region lineage=pass:REGION mask=- formatSource=-
                __count:number label=Count lineage=aggregate:Count:* mask=- formatSource=-
                ir1:number label=sum(Amount) lineage=aggregate:Sum:AMOUNT mask=- formatSource=-
                ir2:number label=ir2 lineage=compute:ir1 mask=- formatSource=-
                compute(ir2) path=tables.summary.composables[2] output=orders#summary
                  REGION:text label=Region lineage=pass:REGION mask=- formatSource=-
                  __count:number label=Count lineage=aggregate:Count:* mask=- formatSource=-
                  ir1:number label=sum(Amount) lineage=aggregate:Sum:AMOUNT mask=- formatSource=-
                  ir2:number label=ir2 lineage=compute:ir1 mask=- formatSource=-
                  group(1,1) path=tables.summary.composables[3] output=orders#group
                    REGION:text label=Region lineage=pass:REGION mask=- formatSource=-
                    __count:number label=Count lineage=aggregate:Count:* mask=- formatSource=-
                    ir1:number label=sum(Amount) lineage=aggregate:Sum:AMOUNT mask=- formatSource=-
                    export-ref(definition) path=tables.summary.from output=orders#summary
                      REGION:text label=Region lineage=source:REGION mask=- formatSource=-
                      AMOUNT:number label=Amount lineage=source:AMOUNT mask=- formatSource=-
                      source path=definition output=orders
                        REGION:text label=Region lineage=source:REGION mask=- formatSource=-
                        AMOUNT:number label=Amount lineage=source:AMOUNT mask=- formatSource=-
            """.ReplaceLineEndings("\n"),
            BoundRelationPlanDebug.Render(table.Export.Bound.Relation));
    }
}
