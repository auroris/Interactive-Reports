using System.Data;
using InteractiveReport.Core.Composition;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Planning;
using InteractiveReport.Core.Schema;
using InteractiveReport.Core.Validation;
using Oracle.ManagedDataAccess.Client;
using static InteractiveReport.Core.Tests.TestFixtures;

namespace InteractiveReport.Core.Tests;

/// <summary>
/// Provider-shape verification that does not require a live Oracle server. It locks
/// down the reflective ODP.NET configuration used by the provider-free Core package;
/// LiveDialectTests covers execution when IR_TEST_ORACLE is available.
/// </summary>
public sealed class OracleCursorBatchTests
{
    [Fact]
    public void Batch_uses_ordered_ref_cursor_outputs_and_deduplicated_named_inputs()
    {
        var definition = OrdersDefinition(ReportDialect.Oracle);
        var table = new ReportTable
        {
            From = "definition",
            Composables = Composables(new StageLayer
            {
                Filters = [Filter("STATUS = 'SHIPPED'")],
                Breaks = ["REGION"],
                Aggregates = [new AggregateRule { Col = "AMOUNT", Fn = AggregateFn.Sum }],
            }),
        };
        var specification = CanonicalTableNormalizer.Normalize(table, "tables.result");
        var schema = ReportSchema.Create(definition.Name, OrdersSchema);
        var source = new BoundOpaqueSqlSource(
            definition.Name,
            definition.Sql,
            ReportDialect.Oracle,
            BoundOutputContract.FromSchema(definition.Name, schema));
        var errors = new List<ValidationError>();
        var ignored = new List<IgnoredItem>();
        var relationBinding = CanonicalRelationBinder.Bind(
            specification,
            $"{definition.Name}#result",
            source.Output,
            ColumnPolicy.Unrestricted,
            inheritedComputedCount: 0,
            inheritedFilterCount: 0,
            errors,
            ignored);
        var relationNode = relationBinding.ApplyTo(source);
        var terminal = CanonicalLocalResultBinder.Bind(
            specification.Local,
            relationNode.Schema,
            ColumnPolicy.Unrestricted,
            errors,
            ignored);

        Assert.Empty(errors);
        Assert.Empty(ignored);
        var evaluationUtcNow = new DateTime(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc);
        var relation = new SqlKataRelationLowerer(
            ReportDialect.Oracle,
            evaluationUtcNow)
            .Lower(relationNode)
            .AsComposable();
        var bundle = TerminalExecutionBundleBuilder.Build(
            definition,
            relation,
            terminal,
            evaluationUtcNow,
            new BoundRequestOverlay(
                Search: null,
                PageIndex: 1,
                PageSize: definition.DefaultPageSize,
                PageAll: false),
            terminalShape: null);
        var compiler = DialectSupport.GetCompiler(ReportDialect.Oracle);

        using var connection = new OracleConnection();
        using var command = Assert.IsType<OracleCommand>(CommandBuilder.BuildOracleCursorBatch(
            connection,
            [
                compiler.Compile(bundle.Count),
                compiler.Compile(bundle.FooterAggregates!.Query),
                compiler.Compile(bundle.BreakTotals!.Query),
                compiler.Compile(bundle.MainRows.Query),
            ],
            new Dictionary<string, object?>(),
            definition));

        Assert.True(command.BindByName);
        Assert.Contains("OPEN :irResult0 FOR", command.CommandText);
        Assert.Contains("OPEN :irResult3 FOR", command.CommandText);
        Assert.Equal(4, command.Parameters.Cast<OracleParameter>()
            .Count(parameter => parameter.Direction == ParameterDirection.Output));
        Assert.All(command.Parameters.Cast<OracleParameter>().Take(4), parameter =>
        {
            Assert.Equal(ParameterDirection.Output, parameter.Direction);
            Assert.Equal(OracleDbType.RefCursor, parameter.OracleDbType);
        });
        Assert.Single(command.Parameters.Cast<OracleParameter>(), parameter =>
            string.Equals(parameter.ParameterName, "p0", StringComparison.OrdinalIgnoreCase));
    }
}
