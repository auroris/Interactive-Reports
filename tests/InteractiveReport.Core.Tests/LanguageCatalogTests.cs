using InteractiveReport.Core.Expressions;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Validation;

namespace InteractiveReport.Core.Tests;

public sealed class LanguageCatalogTests
{
    [Fact]
    public void Expression_catalog_is_derived_from_the_function_registry()
    {
        Assert.Contains("CONTAINS", ExpressionLanguageCatalog.Functions);
        Assert.Contains("DATE_TRUNC", ExpressionLanguageCatalog.Functions);
        Assert.Equal(ExpressionLanguageCatalog.Functions.Count, ExpressionLanguageCatalog.Functions.Distinct().Count());
    }

    [Fact]
    public void Aggregate_catalog_matches_validator_compatibility()
    {
        Assert.Equal(
            ["sum", "avg", "median", "min", "max", "count", "countDistinct"],
            AggregateCatalog.FunctionsByColumnType["number"]);
        Assert.Equal(
            ["count", "countDistinct"],
            AggregateCatalog.FunctionsByColumnType["bool"]);
        Assert.False(AggregateCatalog.IsCompatible(ColumnKind.Text, AggregateFn.Sum));
        Assert.False(AggregateCatalog.IsCompatible(ColumnKind.Text, AggregateFn.Median));
    }
}
