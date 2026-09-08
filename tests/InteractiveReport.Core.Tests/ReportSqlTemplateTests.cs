using InteractiveReport.Core.Composition;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Schema;

namespace InteractiveReport.Core.Tests;

public sealed class ReportSqlTemplateTests
{
    [Theory]
    [InlineData(ReportDialect.Sqlite, "@")]
    [InlineData(ReportDialect.SqlServer, "@")]
    [InlineData(ReportDialect.Postgres, "@")]
    [InlineData(ReportDialect.Oracle, ":")]
    [InlineData(ReportDialect.Oracle11g, ":")]
    public void Restriction_is_bound_inside_configured_grouping_for_every_dialect(ReportDialect dialect, string prefix)
    {
        var definition = Definition("SELECT p.ID FROM PRODUCTS p WHERE {{RowRestriction}} GROUP BY p.ID", dialect);
        var (restricted, parameters) = ReportSqlTemplate.Bind(definition, "p.CG = ? OR p.OWNER = ?", [0, "alice' OR 1=1 --"], new Dictionary<string, object?>());
        var schema = ReportSchema.Create("products", [TestFixtures.Col("ID", typeof(long))]);
        var compiled = DialectSupport.GetCompiler(dialect).Compile(ComposableSqlRelation.Definition(restricted, schema).Query);

        Assert.Contains($"p.CG = {prefix}ir_row_0 OR p.OWNER = {prefix}ir_row_1", compiled.Sql);
        Assert.True(compiled.Sql.IndexOf("p.CG", StringComparison.Ordinal) < compiled.Sql.IndexOf("GROUP BY", StringComparison.Ordinal));
        Assert.DoesNotContain("alice", compiled.Sql);
        Assert.Equal("alice' OR 1=1 --", parameters["ir_row_1"]);
        Assert.Equal(0, parameters["ir_row_0"]);
        Assert.Contains("{{RowRestriction}}", definition.Sql);
        Assert.False(definition.RowRestrictionApplied);
        Assert.True(restricted.RowRestrictionApplied);
    }

    [Theory]
    [InlineData("SELECT '{{RowRestriction}}' AS TEXT")]
    [InlineData("SELECT 1 -- {{RowRestriction}}")]
    [InlineData("SELECT 1 /* nested /* {{RowRestriction}} */ still a comment */")]
    [InlineData("SELECT \"{{RowRestriction}}\" FROM X")]
    [InlineData("SELECT [{{RowRestriction}}] FROM X")]
    [InlineData("SELECT `{{RowRestriction}}` FROM X")]
    [InlineData("SELECT q'[it's {{RowRestriction}}]' FROM DUAL")]
    [InlineData("SELECT q'!it's {{RowRestriction}}!' FROM DUAL")]
    [InlineData("SELECT NQ'[it's {{RowRestriction}}]' FROM DUAL")]
    [InlineData("SELECT $tag$it's {{RowRestriction}}$tag$")]
    [InlineData("SELECT $$it's {{RowRestriction}}$$")]
    [InlineData("SELECT E'it\\'s {{RowRestriction}}'")]
    public void Literals_identifiers_and_comments_do_not_opt_in(string sql)
        => Assert.False(ReportSqlTemplate.RequiresRowRestriction(sql));

    [Fact]
    public void Only_executable_slots_are_replaced_and_repeated_slots_share_the_same_values()
    {
        var definition = Definition("SELECT '{{RowRestriction}}' FROM X p WHERE {{RowRestriction}} UNION ALL SELECT 'x' FROM Y p WHERE {{RowRestriction}} -- {{RowRestriction}}");
        var (restricted, parameters) = ReportSqlTemplate.Bind(definition, "p.CG = ? -- restriction", [0], new Dictionary<string, object?>());
        Assert.Contains("'{{RowRestriction}}'", restricted.Sql);
        Assert.EndsWith("-- {{RowRestriction}}", restricted.Sql);
        Assert.Equal(2, restricted.Sql.Split("p.CG").Length - 1);
        Assert.Contains("-- restriction\n)", restricted.Sql);
        Assert.Single(parameters);
        Assert.False(ReportSqlTemplate.RequiresRowRestriction(restricted.Sql));
    }

    [Fact]
    public void Literal_question_marks_and_quoted_braces_survive_the_composer()
    {
        var definition = Definition("SELECT p.ID FROM X p WHERE {{RowRestriction}}");
        var (restricted, parameters) = ReportSqlTemplate.Bind(definition,
            "p.JSON ?? 'key' AND p.TEXT = '?' AND p.ID = ? /* ? */ AND p.OTHER = '{literal}'", [17], new Dictionary<string, object?>());
        var schema = ReportSchema.Create("products", [TestFixtures.Col("ID", typeof(long))]);
        var sql = DialectSupport.GetCompiler(ReportDialect.Sqlite).Compile(ComposableSqlRelation.Definition(restricted, schema).Query).Sql;
        Assert.Contains("p.JSON ? 'key'", sql);
        Assert.Contains("p.TEXT = '?'", sql);
        Assert.Contains("'{literal}'", sql);
        Assert.Single(parameters);
        Assert.Equal(17, parameters["ir_row_0"]);
    }

    [Fact]
    public void Generated_bindings_cannot_override_context_or_existing_sql_names()
    {
        var definition = Definition("SELECT p.ID FROM X p WHERE p.TENANT = @ir_row_0 AND {{RowRestriction}}");
        var context = new Dictionary<string, object?> { ["ir_row_0"] = "tenant", ["IR_ROW_1"] = "reserved" };
        var (restricted, parameters) = ReportSqlTemplate.Bind(definition, "p.CG = ? AND 'ir_row_2' <> ''", [0], context);
        Assert.Contains("p.CG = @ir_row_3", restricted.Sql);
        Assert.Equal("tenant", parameters["ir_row_0"]);
        Assert.Equal("reserved", parameters["IR_ROW_1"]);
        Assert.Equal(2, context.Count);
    }

    [Theory]
    [InlineData("p.CG = ?", 0)]
    [InlineData("p.CG = 0", 1)]
    [InlineData("p.CG = ? AND p.ID = ?", 1)]
    public void Missing_or_extra_values_are_configuration_errors(string expression, int count)
        => Assert.Throws<InvalidOperationException>(() => ReportSqlTemplate.Bind(
            Definition("SELECT p.ID FROM X p WHERE {{RowRestriction}}"), expression,
            Enumerable.Repeat<object?>(0, count).ToArray(), new Dictionary<string, object?>()));

    [Theory]
    [InlineData("SELECT * FROM X WHERE {{RowRestrictions}}")]
    [InlineData("SELECT * FROM X WHERE {{rowrestriction}}")]
    [InlineData("SELECT * FROM X WHERE {{RowRestriction")]
    public void Misspelled_or_unclosed_slots_fail_explicitly(string sql)
        => Assert.Throws<InvalidOperationException>(() => ReportSqlTemplate.RequiresRowRestriction(sql));

    [Fact]
    public void Core_composition_rejects_an_unresolved_slot()
    {
        var definition = Definition("SELECT p.ID FROM X p WHERE {{RowRestriction}}");
        var schema = ReportSchema.Create("products", [TestFixtures.Col("ID", typeof(long))]);
        Assert.Throws<InvalidOperationException>(() => ComposableSqlRelation.Definition(definition, schema));
    }

    private static ReportDefinition Definition(string sql, ReportDialect dialect = ReportDialect.Sqlite)
        => new() { Name = "products", Connection = "data", Dialect = dialect, Sql = sql };
}
