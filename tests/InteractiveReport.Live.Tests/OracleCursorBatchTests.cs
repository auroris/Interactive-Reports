using System.Data;
using InteractiveReport.Core.Composition;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;
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
        var state = StateValidator.Validate(definition, Doc(source: new StageLayer
        {
            Filters = [Filter("STATUS = 'SHIPPED'")],
            Breaks = ["REGION"],
            Aggregates = [new AggregateRule { Col = "AMOUNT", Fn = AggregateFn.Sum }],
        }), OrdersSchema);
        var composed = QueryComposer.Compose(definition, state);
        var compiler = DialectSupport.GetCompiler(ReportDialect.Oracle);

        using var connection = new OracleConnection();
        using var command = Assert.IsType<OracleCommand>(CommandBuilder.BuildOracleCursorBatch(
            connection,
            [
                compiler.Compile(composed.Count),
                compiler.Compile(composed.Aggregates!),
                compiler.Compile(composed.BreakTotals!),
                compiler.Compile(composed.Page),
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
