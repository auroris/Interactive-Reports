using System.Collections.Immutable;
using InteractiveReport.Core.Composition;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Expressions;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Planning;
using InteractiveReport.Core.Schema;
using InteractiveReport.Core.Validation;
using SqlKata;

namespace InteractiveReport.Core.Tests;

public sealed class TerminalExecutionBundleBuilderTests
{
    private static readonly DateTime EvaluationUtcNow =
        new(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc);
    private static readonly ReportDialect[] SupportedDialects =
    [
        ReportDialect.Sqlite,
        ReportDialect.SqlServer,
        ReportDialect.Postgres,
        ReportDialect.Oracle,
    ];

    public static IEnumerable<object[]> DeliveryRoleCases()
    {
        foreach (var dialect in SupportedDialects)
        foreach (var pageAll in new[] { false, true })
        foreach (var chartTerminal in new[] { false, true })
            yield return [dialect, pageAll, chartTerminal];
    }

    public static IEnumerable<object[]> DialectCases()
    {
        foreach (var dialect in SupportedDialects)
            yield return [dialect];
    }

    [Fact]
    public void Bundle_scopes_selection_and_highlights_to_their_terminal_statements()
    {
        var (definition, relation, schema) = Source();
        var customer = schema.Lookup["CUSTOMER"];
        var status = schema.Lookup["STATUS"];
        var amount = schema.Lookup["AMOUNT"];
        var aggregate = new ValidAggregate(amount, AggregateFn.Sum);

        var baseline = Terminal(
            schema,
            selected: [customer, amount],
            projection: [customer, amount, status],
            aggregates: [aggregate],
            breaks: [status]);
        var highlighted = baseline with
        {
            Decorations = [Highlight(schema)],
        };
        var amountOnly = baseline with
        {
            SelectColumns = [amount],
            ProjectionColumns = [amount, status],
        };

        var plainBundle = Build(definition, relation, baseline);
        var highlightedBundle = Build(definition, relation, highlighted);
        var selectedBundle = Build(definition, relation, amountOnly);

        Assert.NotNull(plainBundle.FooterAggregates);
        Assert.NotNull(plainBundle.BreakTotals);
        Assert.Equal(["CUSTOMER", "AMOUNT", "STATUS"], plainBundle.MainRows.PublicNames);
        Assert.Equal(plainBundle.MainRows.PublicNames, plainBundle.Export.PublicNames);
        Assert.Equal(
            ["CUSTOMER", "AMOUNT", "STATUS", "__ir_highlight_0"],
            highlightedBundle.MainRows.PublicNames);
        Assert.Equal(plainBundle.Export.PublicNames, highlightedBundle.Export.PublicNames);

        AssertQueryEqual(plainBundle.Count, highlightedBundle.Count);
        AssertQueryEqual(plainBundle.FooterAggregates!.Query, highlightedBundle.FooterAggregates!.Query);
        AssertQueryEqual(plainBundle.BreakTotals!.Query, highlightedBundle.BreakTotals!.Query);
        AssertQueryEqual(plainBundle.Export.Query, highlightedBundle.Export.Query);
        AssertQueryNotEqual(plainBundle.MainRows.Query, highlightedBundle.MainRows.Query);

        Assert.Equal(["AMOUNT", "STATUS"], selectedBundle.MainRows.PublicNames);
        Assert.Equal(["AMOUNT", "STATUS"], selectedBundle.Export.PublicNames);
        AssertQueryEqual(plainBundle.Count, selectedBundle.Count);
        AssertQueryEqual(plainBundle.FooterAggregates.Query, selectedBundle.FooterAggregates!.Query);
        AssertQueryEqual(plainBundle.BreakTotals.Query, selectedBundle.BreakTotals!.Query);
        AssertQueryNotEqual(plainBundle.MainRows.Query, selectedBundle.MainRows.Query);
        AssertQueryNotEqual(plainBundle.Export.Query, selectedBundle.Export.Query);
    }

    [Theory]
    [MemberData(nameof(DeliveryRoleCases))]
    public void Delivery_role_matrix_compiles_and_confines_request_windows_to_main_rows(
        ReportDialect dialect,
        bool pageAll,
        bool chartTerminal)
    {
        var (definition, relation, schema) = Source(dialect);
        var status = schema.Lookup["STATUS"];
        var amount = schema.Lookup["AMOUNT"];
        var terminal = Terminal(
            schema,
            selected: [amount, status],
            projection: [amount, status],
            sorts: [new ValidSort(amount, SortDir.Desc)],
            aggregates: [new ValidAggregate(amount, AggregateFn.Sum)],
            breaks: [status]) with
        {
            Decorations = [Highlight(schema)],
        };

        var bundle = Build(
            definition,
            relation,
            terminal,
            pageAll: pageAll,
            chartTerminal: chartTerminal);

        var footer = Assert.IsType<TerminalAggregateQuery>(bundle.FooterAggregates);
        var breakTotals = Assert.IsType<TerminalBreakQuery>(bundle.BreakTotals);
        Assert.Equal(["AMOUNT", "STATUS", "__ir_highlight_0"], bundle.MainRows.PublicNames);
        Assert.Equal(["AMOUNT", "STATUS"], bundle.Export.PublicNames);

        var expectedOrder = new[]
        {
            (relation.PhysicalColumns[status.Name], true),
            (relation.PhysicalColumns[amount.Name], false),
        };
        Assert.Equal(expectedOrder, SimpleOrder(bundle.MainRows.Query));
        Assert.Equal(expectedOrder, SimpleOrder(bundle.Export.Query));

        var expectedMainLimit = chartTerminal
            ? definition.MaxChartPoints + 1
            : pageAll
                ? definition.MaxRows
                : 11;
        long? expectedMainOffset = chartTerminal || pageAll ? null : 10L;
        AssertWindow(bundle.MainRows.Query, expectedMainLimit, expectedMainOffset);

        AssertUnpaged(bundle.Count);
        AssertUnpaged(footer.Query);
        AssertUnpaged(breakTotals.Query);

        var expectedExportLimit = chartTerminal
            ? definition.MaxChartPoints + 1
            : definition.MaxRows + 1;
        AssertWindow(bundle.Export.Query, expectedExportLimit, expectedOffset: null);

        var compiled = BundleSnapshot(bundle, dialect);
        Assert.Equal(5, compiled.Count);
        Assert.All(compiled.Values, statement => Assert.False(string.IsNullOrWhiteSpace(statement.Sql)));
        Assert.Contains("__ir_highlight_0", compiled["main"].Sql, StringComparison.Ordinal);
        foreach (var role in new[] { "count", "footer", "break", "export" })
            Assert.DoesNotContain(
                "__ir_highlight_0",
                compiled[role].Sql,
                StringComparison.Ordinal);
    }

    [Fact]
    public void Main_and_export_share_effective_shape_and_break_ordering()
    {
        var (definition, relation, schema) = Source();
        var customer = schema.Lookup["CUSTOMER"];
        var status = schema.Lookup["STATUS"];
        var amount = schema.Lookup["AMOUNT"];
        var terminal = Terminal(
            schema,
            selected: [customer, status, amount],
            projection: [customer, status, amount],
            sorts: [new ValidSort(amount, SortDir.Desc)],
            breaks: [status]);
        var shape = new CompiledShape(
            ShapeKind.Group,
            "tables.result.composables[0]",
            Dimensions: [customer, status]);

        var bundle = Build(definition, relation, terminal, shape);
        var mainOrder = OrderClause(Compile(bundle.MainRows.Query).Sql);
        var exportOrder = OrderClause(Compile(bundle.Export.Query).Sql);

        Assert.Equal(mainOrder, exportOrder);
        Assert.Contains("__irc1", mainOrder, StringComparison.Ordinal);
        Assert.Contains("__irc2", mainOrder, StringComparison.Ordinal);
        Assert.Contains("__irc0", mainOrder, StringComparison.Ordinal);
        Assert.True(
            mainOrder.IndexOf("__irc1", StringComparison.Ordinal)
            < mainOrder.IndexOf("__irc2", StringComparison.Ordinal));
    }

    [Fact]
    public void Repeated_builds_are_deterministic_and_do_not_advance_the_source_allocator()
    {
        var (definition, relation, schema) = Source();
        var terminal = Terminal(
            schema,
            selected: schema.Columns,
            projection: schema.Columns,
            aggregates: [new ValidAggregate(schema.Lookup["AMOUNT"], AggregateFn.Median)],
            breaks: [schema.Lookup["STATUS"]]);
        var sourceBefore = Compile(relation.Query);

        var first = Build(definition, relation, terminal);
        var second = Build(definition, relation, terminal);

        AssertBundleEqual(first, second);
        AssertCompiledEqual(sourceBefore, Compile(relation.Query));
        Assert.Equal("ir_rel_0", relation.Names.Relation());
    }

    [Theory]
    [MemberData(nameof(DialectCases))]
    public void Adding_footer_aggregates_does_not_rename_other_terminal_roles(
        ReportDialect dialect)
    {
        var (definition, relation, schema) = Source(dialect);
        var status = schema.Lookup["STATUS"];
        var amount = schema.Lookup["AMOUNT"];
        var baseline = Terminal(
            schema,
            selected: schema.Columns,
            projection: schema.Columns,
            sorts: [new ValidSort(amount, SortDir.Desc)],
            breaks: [status]);
        var withFooter = baseline with
        {
            Aggregates = [new ValidAggregate(amount, AggregateFn.Sum)],
        };

        var before = Build(definition, relation, baseline);
        var after = Build(definition, relation, withFooter);

        Assert.Null(before.FooterAggregates);
        Assert.NotNull(after.FooterAggregates);
        AssertCompiledEqual(
            Compile(before.MainRows.Query, dialect),
            Compile(after.MainRows.Query, dialect));
        AssertCompiledEqual(
            Compile(before.Count, dialect),
            Compile(after.Count, dialect));
        AssertCompiledEqual(
            Compile(before.Export.Query, dialect),
            Compile(after.Export.Query, dialect));
        Assert.Equal(before.MainRows.PublicNames, after.MainRows.PublicNames);
        Assert.Equal(before.Export.PublicNames, after.Export.PublicNames);
    }

    [Theory]
    [MemberData(nameof(DialectCases))]
    public void Sort_break_and_delivery_deltas_are_confined_to_their_roles(
        ReportDialect dialect)
    {
        var (definition, relation, schema) = Source(dialect);
        var status = schema.Lookup["STATUS"];
        var amount = schema.Lookup["AMOUNT"];
        var aggregate = new ValidAggregate(amount, AggregateFn.Sum);
        var baseline = Terminal(
            schema,
            selected: schema.Columns,
            projection: schema.Columns,
            aggregates: [aggregate]);

        var plain = Build(definition, relation, baseline);
        var sorted = Build(
            definition,
            relation,
            baseline with { Sorts = [new ValidSort(amount, SortDir.Desc)] });
        AssertQueryNotEqualForDialect(plain.MainRows.Query, sorted.MainRows.Query, dialect);
        AssertQueryNotEqualForDialect(plain.Export.Query, sorted.Export.Query, dialect);
        AssertCompiledEqual(Compile(plain.Count, dialect), Compile(sorted.Count, dialect));
        AssertCompiledEqual(
            Compile(plain.FooterAggregates!.Query, dialect),
            Compile(sorted.FooterAggregates!.Query, dialect));

        var broken = Build(
            definition,
            relation,
            baseline with { Breaks = [status] });
        Assert.NotNull(broken.BreakTotals);
        AssertQueryNotEqualForDialect(plain.MainRows.Query, broken.MainRows.Query, dialect);
        AssertQueryNotEqualForDialect(plain.Export.Query, broken.Export.Query, dialect);
        AssertCompiledEqual(Compile(plain.Count, dialect), Compile(broken.Count, dialect));
        AssertCompiledEqual(
            Compile(plain.FooterAggregates.Query, dialect),
            Compile(broken.FooterAggregates!.Query, dialect));

        var allRows = Build(definition, relation, baseline, pageAll: true);
        AssertQueryNotEqualForDialect(plain.MainRows.Query, allRows.MainRows.Query, dialect);
        AssertCompiledEqual(Compile(plain.Count, dialect), Compile(allRows.Count, dialect));
        AssertCompiledEqual(
            Compile(plain.FooterAggregates.Query, dialect),
            Compile(allRows.FooterAggregates!.Query, dialect));
        AssertCompiledEqual(Compile(plain.Export.Query, dialect), Compile(allRows.Export.Query, dialect));
    }

    [Theory]
    [MemberData(nameof(DialectCases))]
    public void Pivot_totals_slot_carries_the_registered_cell_layout(
        ReportDialect dialect)
    {
        var (definition, relation, schema) = Source(dialect);
        var status = schema.Lookup["STATUS"];
        var amount = schema.Lookup["AMOUNT"];
        var metric = new ValidMetric("ir1", amount, AggregateFn.Sum);
        var totalsRelation = ComposableSqlPlanner.Group(
            relation,
            "pivot-totals",
            [status],
            [metric],
            definition.GetEffectiveDialect(),
            "__count");
        var cellId = new DynamicPivotColumnIdentityRegistry([])
            .Register("result", "ir1", ["SHIPPED"]);
        var cell = new PivotCellColumn(
            "ir1",
            new ColumnModel
            {
                Name = cellId,
                Label = "Shipped",
                ClrType = typeof(decimal),
            });
        var key = new PivotColumnKey(["SHIPPED"], [cell]);
        var shape = new CompiledShape(
            ShapeKind.Pivot,
            "tables.result.composables[0]",
            Dimensions: [schema.Lookup["CUSTOMER"]],
            Metrics: [metric],
            PivotColumns: [status],
            PivotTotals: true,
            PivotTotalsRelation: totalsRelation,
            PivotKeys: [key]);
        var bundle = Build(
            definition,
            relation,
            Terminal(
                schema,
                schema.Columns,
                schema.Columns,
                aggregates: [new ValidAggregate(amount, AggregateFn.Sum)],
                breaks: [status]),
            shape);

        var totals = Assert.IsType<PivotTotalsQuery>(bundle.PivotTotals);
        Assert.Equal(0, totals.Query.RowDimensionCount);
        Assert.Equal(1, totals.Query.ColumnDimensionCount);
        Assert.Equal(1, totals.Query.ValueCount);
        Assert.Equal(metric, Assert.Single(totals.Metrics));
        Assert.Equal(key, Assert.Single(totals.Keys));

        var compiled = BundleSnapshot(bundle, dialect);
        Assert.Equal(6, compiled.Count);
        Assert.All(compiled.Values, statement => Assert.False(string.IsNullOrWhiteSpace(statement.Sql)));
    }

    private static TerminalExecutionBundle Build(
        ReportDefinition definition,
        ComposableSqlRelation relation,
        BoundLocalResult terminal,
        CompiledShape? shape = null,
        bool pageAll = false,
        bool chartTerminal = false)
        => TerminalExecutionBundleBuilder.Build(
            definition,
            relation,
            terminal,
            EvaluationUtcNow,
            new BoundRequestOverlay(
                Search: null,
                PageIndex: 2,
                PageSize: pageAll ? 0 : 10,
                PageAll: pageAll),
            terminalShape: shape,
            chartTerminal);

    private static BoundLocalResult Terminal(
        ReportSchema schema,
        IReadOnlyList<ColumnModel> selected,
        IReadOnlyList<ColumnModel> projection,
        IReadOnlyList<ValidSort>? sorts = null,
        IReadOnlyList<ValidAggregate>? aggregates = null,
        IReadOnlyList<ColumnModel>? breaks = null)
        => new(
            schema,
            Decorations: [],
            Sorts: (sorts ?? []).ToImmutableArray(),
            SelectColumns: selected.ToImmutableArray(),
            ProjectionColumns: projection.ToImmutableArray(),
            Aggregates: (aggregates ?? []).ToImmutableArray(),
            Breaks: (breaks ?? []).ToImmutableArray(),
            Labels: ImmutableDictionary.Create<string, string>(StringComparer.OrdinalIgnoreCase),
            Formats: ImmutableDictionary.Create<string, ColumnFormat>(StringComparer.OrdinalIgnoreCase));

    private static CompiledRule<HighlightEffect> Highlight(ReportSchema schema)
    {
        var (ast, error) = ExprParser.ParseCondition("AMOUNT > 100", schema.Lookup);
        Assert.Null(error);
        return new CompiledRule<HighlightEffect>(
            new BoundExpression(ast!),
            new HighlightEffect(
                "high",
                "High amount",
                1,
                HighlightScope.Row,
                null,
                "__ir_highlight_0"));
    }

    private static (ReportDefinition Definition, ComposableSqlRelation Relation, ReportSchema Schema) Source(
        ReportDialect dialect = ReportDialect.Sqlite)
    {
        var definition = new ReportDefinition
        {
            Name = "terminal-bundle",
            Connection = "unused",
            Dialect = dialect,
            Sql = "SELECT CUSTOMER, STATUS, AMOUNT FROM ORDERS",
            MaxRows = 500,
            MaxChartPoints = 100,
        };
        var schema = ReportSchema.Create(
            definition.Name,
            [
                new ColumnModel { Name = "CUSTOMER", Label = "Customer", ClrType = typeof(string) },
                new ColumnModel { Name = "STATUS", Label = "Status", ClrType = typeof(string) },
                new ColumnModel { Name = "AMOUNT", Label = "Amount", ClrType = typeof(decimal) },
            ]);
        return (definition, ComposableSqlRelation.Definition(definition, schema), schema);
    }

    private static string OrderClause(string sql)
    {
        var start = sql.IndexOf("ORDER BY", StringComparison.OrdinalIgnoreCase);
        Assert.True(start >= 0, $"Expected ORDER BY in: {sql}");
        var end = sql.IndexOf(" LIMIT ", start, StringComparison.OrdinalIgnoreCase);
        if (end < 0) end = sql.IndexOf(" OFFSET ", start, StringComparison.OrdinalIgnoreCase);
        return end < 0 ? sql[start..] : sql[start..end];
    }

    private static (string Column, bool Ascending)[] SimpleOrder(Query query)
        => query.GetComponents<AbstractOrderBy>("order")
            .Select(component =>
            {
                var order = Assert.IsType<SqlKata.OrderBy>(component);
                return (order.Column, order.Ascending);
            })
            .ToArray();

    private static void AssertWindow(Query query, int expectedLimit, long? expectedOffset)
    {
        Assert.True(query.HasLimit());
        var limit = Assert.IsType<LimitClause>(query.GetOneComponent<LimitClause>("limit"));
        Assert.Equal(expectedLimit, limit.Limit);

        if (expectedOffset is null)
        {
            Assert.False(query.HasOffset());
            return;
        }

        Assert.True(query.HasOffset());
        var offset = Assert.IsType<OffsetClause>(query.GetOneComponent<OffsetClause>("offset"));
        Assert.Equal(expectedOffset.Value, offset.Offset);
    }

    private static void AssertUnpaged(Query query)
    {
        Assert.False(query.HasLimit());
        Assert.False(query.HasOffset());
    }

    private static IReadOnlyDictionary<string, CompiledQuery> BundleSnapshot(
        TerminalExecutionBundle bundle,
        ReportDialect dialect = ReportDialect.Sqlite)
    {
        var result = new Dictionary<string, CompiledQuery>
        {
            ["main"] = Compile(bundle.MainRows.Query, dialect),
            ["count"] = Compile(bundle.Count, dialect),
            ["export"] = Compile(bundle.Export.Query, dialect),
        };
        if (bundle.FooterAggregates is not null)
            result["footer"] = Compile(bundle.FooterAggregates.Query, dialect);
        if (bundle.BreakTotals is not null)
            result["break"] = Compile(bundle.BreakTotals.Query, dialect);
        if (bundle.PivotTotals is not null)
            result["pivot-totals"] = Compile(bundle.PivotTotals.Query.Query, dialect);
        return result;
    }

    private static void AssertBundleEqual(
        TerminalExecutionBundle expected,
        TerminalExecutionBundle actual)
    {
        var expectedQueries = BundleSnapshot(expected);
        var actualQueries = BundleSnapshot(actual);
        Assert.Equal(expectedQueries.Keys, actualQueries.Keys);
        foreach (var role in expectedQueries.Keys)
            AssertCompiledEqual(expectedQueries[role], actualQueries[role]);
        Assert.Equal(expected.MainRows.PublicNames, actual.MainRows.PublicNames);
        Assert.Equal(expected.Export.PublicNames, actual.Export.PublicNames);
    }

    private static void AssertQueryEqual(Query expected, Query actual)
        => AssertCompiledEqual(Compile(expected), Compile(actual));

    private static void AssertQueryNotEqual(Query expected, Query actual)
    {
        var left = Compile(expected);
        var right = Compile(actual);
        Assert.False(
            left.Sql == right.Sql && left.Bindings.SequenceEqual(right.Bindings),
            "Expected the compiled queries to differ.");
    }

    private static void AssertQueryNotEqualForDialect(
        Query expected,
        Query actual,
        ReportDialect dialect)
    {
        var left = Compile(expected, dialect);
        var right = Compile(actual, dialect);
        Assert.False(
            left.Sql == right.Sql && left.Bindings.SequenceEqual(right.Bindings),
            "Expected the compiled queries to differ.");
    }

    private static void AssertCompiledEqual(CompiledQuery expected, CompiledQuery actual)
    {
        Assert.Equal(expected.Sql, actual.Sql);
        Assert.Equal(expected.Bindings, actual.Bindings);
    }

    private static CompiledQuery Compile(
        Query query,
        ReportDialect dialect = ReportDialect.Sqlite)
    {
        var compiled = DialectSupport.GetCompiler(dialect).Compile(query);
        return new CompiledQuery(compiled.Sql, compiled.Bindings.ToArray());
    }

    private sealed record CompiledQuery(string Sql, object?[] Bindings);
}
