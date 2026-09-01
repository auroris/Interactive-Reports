using InteractiveReport.Core.Composition;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Planning;
using InteractiveReport.Core.Schema;
using InteractiveReport.Core.Validation;
using static InteractiveReport.Core.Tests.TestFixtures;

namespace InteractiveReport.Core.Tests;

/// <summary>The row-condition expression vocabulary compiled across every dialect.</summary>
public class OperatorMatrixTests
{
    [Theory]
    [InlineData(ReportDialect.Sqlite, "(\"__irc4\" >= @p0)")]
    [InlineData(ReportDialect.SqlServer, "([__irc4] >= @p0)")]
    [InlineData(ReportDialect.Oracle, "(\"__irc4\" >= :p0)")]
    [InlineData(ReportDialect.Postgres, "(\"__irc4\" >= @p0)")]
    public void Comparisons_are_bound_predicates(ReportDialect dialect, string expected)
    {
        var compiled = Compile(dialect, "AMOUNT >= 1000");

        Assert.Contains(expected, compiled.Sql);
        Assert.Contains(1000m, compiled.NamedBindings.Values);
    }

    [Theory]
    [InlineData(ReportDialect.Sqlite, "\"__irc3\" IN (@p0, @p1)")]
    [InlineData(ReportDialect.SqlServer, "[__irc3] IN (@p0, @p1)")]
    [InlineData(ReportDialect.Oracle, "\"__irc3\" IN (:p0, :p1)")]
    [InlineData(ReportDialect.Postgres, "\"__irc3\" IN (@p0, @p1)")]
    public void In_list_is_a_typed_condition_function(ReportDialect dialect, string expected)
    {
        var compiled = Compile(dialect, "IN_LIST(STATUS, 'NEW', 'SHIPPED')");

        Assert.Contains(expected, compiled.Sql);
        Assert.Contains("NEW", compiled.NamedBindings.Values);
        Assert.Contains("SHIPPED", compiled.NamedBindings.Values);
    }

    [Theory]
    [InlineData(ReportDialect.Sqlite)]
    [InlineData(ReportDialect.SqlServer)]
    [InlineData(ReportDialect.Oracle)]
    [InlineData(ReportDialect.Postgres)]
    public void Text_predicates_are_case_insensitive_and_bind_wildcards(ReportDialect dialect)
    {
        var compiled = Compile(dialect, "CONTAINS(CUSTOMER, 'Acme')");

        Assert.Contains("LOWER", compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LIKE", compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ESCAPE '\\'", compiled.Sql, StringComparison.Ordinal);
        Assert.Equal(["%Acme%"], compiled.NamedBindings.Values.Take(1));
    }

    [Theory]
    [InlineData(ReportDialect.Sqlite, "100%", "%100\\%%")]
    [InlineData(ReportDialect.Postgres, "a_b", "%a\\_b%")]
    [InlineData(ReportDialect.Oracle, "back\\slash", "%back\\\\slash%")]
    [InlineData(ReportDialect.SqlServer, "[urgent]", "%\\[urgent]%")]
    [InlineData(ReportDialect.Postgres, "[urgent]", "%[urgent]%")]
    public void Text_predicates_match_metacharacters_literally(ReportDialect dialect, string search, string expected)
    {
        // The user's text is a substring, never a pattern: %, _, and \ are escaped everywhere, and
        // [ on SQL Server, where it opens a character class even under an ESCAPE clause.
        var compiled = Compile(dialect, $"CONTAINS(CUSTOMER, '{search}')");

        Assert.Equal([expected], compiled.NamedBindings.Values.Take(1));
    }

    [Theory]
    [InlineData(ReportDialect.Sqlite)]
    [InlineData(ReportDialect.SqlServer)]
    [InlineData(ReportDialect.Oracle)]
    [InlineData(ReportDialect.Postgres)]
    public void Text_predicates_over_a_column_pattern_escape_in_sql(ReportDialect dialect)
    {
        // When the search text is a column, escaping has to happen in SQL: a REPLACE chain makes
        // the column's own metacharacters literal before the wildcards are concatenated on.
        var compiled = Compile(dialect, "STARTS_WITH(CUSTOMER, STATUS)");

        Assert.Contains("REPLACE(REPLACE(REPLACE(", compiled.Sql, StringComparison.Ordinal);
        Assert.Contains("ESCAPE '\\'", compiled.Sql, StringComparison.Ordinal);
        var bindings = compiled.NamedBindings.Values.Select(value => (string)value!).ToList();
        Assert.Contains("\\\\", bindings);
        Assert.Contains("\\%", bindings);
        Assert.Contains("\\_", bindings);
        Assert.Equal(dialect == ReportDialect.SqlServer, bindings.Contains("\\["));
        Assert.Equal("%", bindings[^1]);
    }

    [Theory]
    [InlineData(ReportDialect.Sqlite)]
    [InlineData(ReportDialect.SqlServer)]
    [InlineData(ReportDialect.Oracle)]
    [InlineData(ReportDialect.Postgres)]
    public void User_wildcard_match_is_case_insensitive_escaped_and_bound(ReportDialect dialect)
    {
        var compiled = Compile(dialect, @"WILDCARD_MATCH(CUSTOMER, 'Ac*50%_\*\\')");

        Assert.Contains("LOWER", compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LIKE", compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ESCAPE '\\'", compiled.Sql, StringComparison.Ordinal);
        Assert.Contains(@"Ac%50\%\_*\\", compiled.NamedBindings.Values);
    }

    [Theory]
    [InlineData(ReportDialect.Sqlite)]
    [InlineData(ReportDialect.SqlServer)]
    [InlineData(ReportDialect.Oracle)]
    [InlineData(ReportDialect.Postgres)]
    public void Logical_and_null_conditions_compose(ReportDialect dialect)
    {
        var compiled = Compile(dialect, "NOTES IS NULL OR (AMOUNT BETWEEN 100 AND 500 AND STATUS <> 'CANCELLED')");

        Assert.Contains("IS NULL", compiled.Sql);
        Assert.Contains("BETWEEN", compiled.Sql);
        Assert.Contains(" AND ", compiled.Sql);
        Assert.Contains(" OR ", compiled.Sql);
    }

    private static SqlKata.SqlResult Compile(ReportDialect dialect, string expression)
    {
        var definition = OrdersDefinition(dialect);
        var schema = ReportSchema.Create(definition.Name, OrdersSchema);
        var source = new BoundOpaqueSqlSource(
            definition.Name,
            definition.Sql,
            dialect,
            BoundOutputContract.FromSchema(definition.Name, schema));
        var specification = CanonicalTableNormalizer.Normalize(
            new ReportTable
            {
                From = "definition",
                Composables =
                [
                    new TableComposable
                    {
                        Kind = "filter",
                        Filters = [new FilterRule { Expr = expression }],
                    },
                ],
            },
            "tables.source");
        var errors = new List<ValidationError>();
        var ignored = new List<IgnoredItem>();
        var binding = CanonicalRelationBinder.Bind(
            specification,
            $"{definition.Name}#source",
            source.Output,
            ColumnPolicy.Unrestricted,
            inheritedComputedCount: 0,
            inheritedFilterCount: 0,
            errors,
            ignored);

        Assert.Empty(errors);
        Assert.Empty(ignored);
        var relation = binding.ApplyTo(source);
        var lowered = new SqlKataRelationLowerer(
            dialect,
            new DateTime(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc))
            .Lower(relation);
        return DialectSupport.GetCompiler(dialect).Compile(lowered.Query);
    }
}
