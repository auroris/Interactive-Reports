using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;
using static InteractiveReport.Core.Tests.TestFixtures;

namespace InteractiveReport.Core.Tests;

/// <summary>
/// One identifier scenario shared by SQLite and the gated live-provider suite. The
/// configured query deliberately returns names which SQL builders commonly mistake
/// for qualification, aliasing, wildcards, delimiters, placeholders, or SQL syntax.
/// </summary>
internal static class IdentifierTortureCorpus
{
    internal const string DotName = "MiX.ed Name";
    internal const string RawMarkerName = "raw[]{}?$#`";
    internal const string AmountName = "cash$";
    internal const string OrderIdName = "order#";

    private const string EmbeddedQuoteName = "quote\"name";
    private const string SentinelName = "sent\uE000\uE001inel";

    private static readonly IReadOnlyList<TortureColumn> PortableColumns =
    [
        new("ORDER_ID", "SeLeCt"),
        new("CUSTOMER", DotName),
        new("CUSTOMER", "looks as alias"),
        new("STATUS", "*"),
        new("STATUS", RawMarkerName),
        new("NOTES", "apostrophe'name"),
        new("NOTES", "semi;--/*x*/"),
        new("CUSTOMER", "slash\\[x]"),
        new("'?'", "literal?"),
        new("AMOUNT", AmountName),
        new("ORDER_ID", OrderIdName),
    ];

    private static readonly IReadOnlyList<TortureColumn> SqliteOnlyColumns =
    [
        new("STATUS", EmbeddedQuoteName),
        new("CUSTOMER", "naïve"),
        new("STATUS", SentinelName),
        new("'\uE000?'", "codec literal?"),
    ];

    internal static IReadOnlyList<string> NamesForRuntime(ReportDialect dialect)
        => ColumnsForRuntime(dialect).Select(column => column.Name).ToArray();

    internal static IReadOnlyList<string> NamesForCompiler(ReportDialect dialect)
    {
        var names = PortableColumns.Select(column => column.Name)
            .Concat(SqliteOnlyColumns.Select(column => column.Name));
        // Oracle does not permit an embedded double quote in an identifier. Its
        // compiler output is therefore not presented as executable coverage.
        if (dialect == ReportDialect.Oracle)
            names = names.Where(name => !string.Equals(name, EmbeddedQuoteName, StringComparison.Ordinal));
        return names.ToArray();
    }

    internal static string DefinitionSql(ReportDialect dialect, string tableName)
    {
        var select = ColumnsForRuntime(dialect)
            .Select(column => $"{column.SqlExpression} AS {QuoteSqlIdentifier(dialect, column.Name)}");
        return $"SELECT {string.Join(", ", select)} FROM {tableName}";
    }

    internal static string QuoteSqlIdentifier(ReportDialect dialect, string name)
        => dialect == ReportDialect.SqlServer
            ? $"[{name.Replace("]", "]]", StringComparison.Ordinal)}]"
            : $"\"{name.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    internal static async Task AssertRoundTrip(
        ReportExecutor executor,
        ReportDefinition definition)
    {
        var expectedNames = NamesForRuntime(definition.GetEffectiveDialect());
        var schema = await executor.GetSchema(definition, NoParams);
        Assert.Equal(expectedNames, schema.Select(column => column.Name));
        if (definition.GetEffectiveDialect() == ReportDialect.Sqlite)
        {
            var literal = schema.Single(column => column.Name == "literal?");
            Assert.False(
                literal.HasKnownType,
                $"SQLite literal schema type should be unknown, got {literal.ClrType.FullName}");
        }

        var source = Doc(
            source: new StageLayer
            {
                Computed =
                [
                    new ComputedColumn
                    {
                        Id = "ir1",
                        Expr = $"{ExpressionIdentifier(AmountName)} + {ExpressionIdentifier(OrderIdName)}",
                    },
                ],
                Filters =
                [
                    Filter(
                        $"{ExpressionIdentifier(RawMarkerName)} = 'SHIPPED' "
                        + $"AND {ExpressionIdentifier(AmountName)} >= 5000 "
                        + "AND `literal?` = '?'"),
                ],
                Sorts = [new SortRule { Col = DotName, Dir = SortDir.Asc }],
                Columns = [.. expectedNames, "ir1"],
            },
            search: "ship");

        var query = await executor.Query(definition, source, NoParams);
        var export = await executor.Download(definition, source, NoParams);
        var projectedNames = expectedNames.Concat(["ir1"]).ToArray();

        Assert.Equal(projectedNames, query.Columns.Select(column => column.Name));
        Assert.Equal(3, query.TotalRows);
        Assert.Equal(["Acme Corp", "Globex", "Initech"],
            query.Rows.Select(row => row[DotName]));
        Assert.Equal([9001m, 7502m, 5003m],
            query.Rows.Select(row => Convert.ToDecimal(row["ir1"])));
        Assert.All(query.Rows, row => AssertExactKeys(row, projectedNames));

        Assert.Equal(projectedNames, export.Columns.Select(column => column.Name));
        Assert.Equal(3, export.Rows.Count);
        Assert.All(export.Rows, row => AssertExactKeys(row, projectedNames));

        var groupedState = Doc(tail:
        [
            Group(
                by: [RawMarkerName],
                values: [Metric("ir1", AmountName, AggregateFn.Sum)]),
        ]);
        var grouped = await executor.Query(definition, groupedState, NoParams);
        var groupedExport = await executor.Download(definition, groupedState, NoParams);
        string[] groupedNames = [RawMarkerName, "__count", "ir1"];

        Assert.Equal(groupedNames, grouped.Columns.Select(column => column.Name));
        Assert.Equal(4, grouped.TotalRows);
        Assert.All(grouped.Rows, row => AssertExactKeys(row, groupedNames));
        Assert.Equal(groupedNames, groupedExport.Columns.Select(column => column.Name));
        Assert.Equal(4, groupedExport.Rows.Count);
        Assert.All(groupedExport.Rows, row => AssertExactKeys(row, groupedNames));
    }

    private static IReadOnlyList<TortureColumn> ColumnsForRuntime(ReportDialect dialect)
    {
        IEnumerable<TortureColumn> columns = PortableColumns;
        if (dialect is ReportDialect.SqlServer or ReportDialect.Postgres)
            columns = columns.Append(new TortureColumn("STATUS", EmbeddedQuoteName));
        if (dialect == ReportDialect.Sqlite)
            columns = columns.Concat(SqliteOnlyColumns);
        return columns.ToArray();
    }

    private static string ExpressionIdentifier(string name)
        => $"`{name.Replace("`", "``", StringComparison.Ordinal)}`";

    private static void AssertExactKeys(
        IReadOnlyDictionary<string, object?> row,
        IReadOnlyList<string> expected)
    {
        Assert.Equal(expected.Count, row.Count);
        foreach (var name in expected)
            Assert.Contains(row.Keys, key => string.Equals(key, name, StringComparison.Ordinal));
    }

    private static readonly IReadOnlyDictionary<string, object?> NoParams =
        new Dictionary<string, object?>();

    private sealed record TortureColumn(string SqlExpression, string Name);
}
