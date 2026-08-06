using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Expressions;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Validation;

namespace InteractiveReport.Core.Tests;

public class HighlightEvaluatorTests
{
    [Fact]
    public void Projection_markers_accept_provider_boolean_and_numeric_values()
    {
        var rules = new[]
        {
            Rule("bool", HighlightScope.Row, "__h0"),
            Rule("number", HighlightScope.Cell, "__h1", "AMOUNT"),
        };
        var rows = new IReadOnlyDictionary<string, object?>[]
        {
            new Dictionary<string, object?> { ["__h0"] = true, ["__h1"] = 1L },
            new Dictionary<string, object?> { ["__h0"] = false, ["__h1"] = 0m },
        };

        var hits = HighlightEvaluator.Evaluate(rules, rows);

        Assert.Equal(2, hits.Count);
        Assert.Contains(hits, hit => hit is { Row: 0, Id: "bool", Col: null });
        Assert.Contains(hits, hit => hit is { Row: 0, Id: "number", Col: "AMOUNT" });
    }

    [Fact]
    public void Row_hits_are_emitted_before_cell_hits_regardless_of_rule_order()
    {
        var rules = new[]
        {
            Rule("cell", HighlightScope.Cell, "__cell", "AMOUNT"),
            Rule("row", HighlightScope.Row, "__row"),
        };
        var rows = new IReadOnlyDictionary<string, object?>[]
        {
            new Dictionary<string, object?> { ["__cell"] = 1, ["__row"] = 1 },
        };

        var hits = HighlightEvaluator.Evaluate(rules, rows);

        Assert.Equal(["row", "cell"], hits.Select(hit => hit.Id));
    }

    private static CompiledRule<HighlightEffect> Rule(
        string id,
        HighlightScope scope,
        string projection,
        string? column = null)
    {
        var (ast, error) = ExprParser.ParseCondition("1 = 1", new Dictionary<string, ColumnModel>());
        Assert.Null(error);
        return new CompiledRule<HighlightEffect>(
            new BoundExpression(ast!),
            new HighlightEffect(
                id,
                scope,
                column is null ? null : TestFixtures.Col(column, typeof(decimal)),
                projection));
    }
}
