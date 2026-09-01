using System.Collections.Immutable;
using System.Text.RegularExpressions;
using InteractiveReport.Core.Composition;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Planning;
using InteractiveReport.Core.Schema;
using InteractiveReport.Core.Validation;

namespace InteractiveReport.Core.Tests;

public sealed class SqlKataRelationLowererTests
{
    private static readonly DateTime EvaluationUtcNow =
        new(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc);

    private static readonly ReportSchema Schema = ReportSchema.Create(
        "orders",
        [
            Column("CUSTOMER", "Customer", typeof(string), nullable: false),
            Column("STATUS", "Status", typeof(string), nullable: false),
            Column("AMOUNT", "Amount", typeof(decimal)),
        ]);

    [Theory]
    [InlineData(ReportDialect.Sqlite)]
    [InlineData(ReportDialect.SqlServer)]
    [InlineData(ReportDialect.Postgres)]
    [InlineData(ReportDialect.Oracle)]
    public void Source_compute_filter_metadata_and_search_lower_with_one_exact_contract(
        ReportDialect dialect)
    {
        var source = Source(dialect);
        var specification = CanonicalTableNormalizer.Normalize(
            new ReportTable
            {
                Composables =
                [
                    new TableComposable
                    {
                        Kind = "filter",
                        Filters = [new FilterRule { Expr = "ir1 >= 10" }],
                    },
                    new TableComposable
                    {
                        Kind = "compute",
                        Computed = [new ComputedColumn { Id = "ir1", Expr = "AMOUNT * 2" }],
                    },
                ],
            },
            "tables.result");
        var errors = new List<ValidationError>();
        var ignored = new List<IgnoredItem>();
        var binding = CanonicalRelationBinder.Bind(
            specification,
            "orders#result",
            source.Output,
            ColumnPolicy.Unrestricted,
            0,
            0,
            errors,
            ignored);
        var relation = binding.ApplyTo(source);
        var metadataOutput = relation.Output.ApplyMetadata(new CanonicalMetadata(
            false,
            ImmutableDictionary.CreateRange(
                StringComparer.OrdinalIgnoreCase,
                [new KeyValuePair<string, string>("ir1", "Double amount")]),
            false,
            ImmutableDictionary.CreateRange(
                StringComparer.OrdinalIgnoreCase,
                [new KeyValuePair<string, CanonicalColumnFormat>(
                    "ir1",
                    Format(mask: "decimal-2", bold: true))])));
        var metadata = new BoundMetadataRelation(
            relation,
            metadataOutput,
            "tables.result.composables[2]");
        var search = new BoundSearchRelation(
            metadata,
            "Acme",
            metadata.Output);
        var lowerer = new SqlKataRelationLowerer(dialect, EvaluationUtcNow);

        var lowered = lowerer.Lower(search);
        var repeated = lowerer.Lower(search);
        var compiler = DialectSupport.GetCompiler(dialect);
        var sql = compiler.Compile(lowered.Query);
        var repeatedSql = compiler.Compile(repeated.Query);

        Assert.Empty(errors);
        Assert.Empty(ignored);
        Assert.Collection(
            binding.Mutations,
            mutation => Assert.IsType<BoundCanonicalComputeMutation>(mutation),
            mutation => Assert.IsType<BoundCanonicalFilterMutation>(mutation));
        Assert.IsType<BoundMetadataRelation>(metadata);
        Assert.Equal(sql.Sql, repeatedSql.Sql);
        Assert.Equal(sql.Bindings, repeatedSql.Bindings);
        Assert.Equal(
            dialect is ReportDialect.Postgres
                ? [2m, 10m, "%Acme%", "%Acme%"]
                : [2m, 10m, "%acme%", "%acme%"],
            sql.Bindings);
        Assert.Equal(ExpectedPipelineSql, CanonicalizeSql(sql.Sql));
        Assert.Equal(4, lowered.StageCount);
        Assert.Equal(
            ["CUSTOMER", "STATUS", "AMOUNT", "ir1"],
            lowered.Output.Columns.Select(column => column.LogicalId));
        Assert.Equal(
            lowered.Output.Columns.Select(column => column.LogicalId).Order(),
            lowered.PhysicalColumns.Keys.Order());
        Assert.Equal(
            ["__irc0", "__irc1", "__irc2", "__irc3"],
            PhysicalInContractOrder(lowered));
        var computed = lowered.Output.GetRequired("ir1");
        Assert.Equal("Double amount", computed.EffectiveLabel);
        Assert.Equal("decimal-2", computed.ExportedMask);
        Assert.Equal("ir1", computed.FormatSourceLogicalId);
        Assert.True(computed.LocalFormat!.Bold);
        Assert.Equal(
            ["AMOUNT"],
            Assert.IsType<BoundComputedColumnLineage>(computed.Lineage).InputLogicalIds.ToArray());
        Assert.Equal(
            ["search", "metadata", "filter(1)", "compute(ir1)", "source"],
            DebugOperators(search));
        Assert.Equal(
            ExpectedPipelinePlan.ReplaceLineEndings("\n"),
            BoundRelationPlanDebug.Render(search));
    }

    [Theory]
    [InlineData(ReportDialect.Sqlite)]
    [InlineData(ReportDialect.SqlServer)]
    [InlineData(ReportDialect.Postgres)]
    [InlineData(ReportDialect.Oracle)]
    public void Group_chart_and_resolved_pivot_nodes_lower_across_all_dialects(
        ReportDialect dialect)
    {
        var source = Source(dialect);
        var customer = source.Output.GetRequired("CUSTOMER");
        var status = source.Output.GetRequired("STATUS");
        var amount = source.Output.GetRequired("AMOUNT");
        var metric = new BoundMetric(
            "ir1",
            amount,
            AggregateFn.Sum,
            "tables.pivot.composables[0].values[0]");
        var count = AggregateColumn("__count", "Count", AggregateFn.Count, null, typeof(long));
        var metricOutput = AggregateColumn(
            "ir1",
            "sum(Amount)",
            AggregateFn.Sum,
            "AMOUNT",
            typeof(decimal));
        var discoveryOutput = BoundOutputContract.Create(
            "orders#pivot-discovery",
            [customer, status, count, metricOutput]);
        var discovery = new BoundGroupRelation(
            source,
            [customer, status],
            [metric],
            count,
            discoveryOutput,
            "tables.pivot.composables[0]");
        var typedKey = BoundPivotTypedKey.Create(["SHIPPED"]);
        var cellId = new DynamicPivotColumnIdentityRegistry(["ir1"])
            .Register("pivot", "ir1", typedKey);
        var cell = new BoundColumnContract(
            cellId,
            "SHIPPED",
            "SHIPPED",
            typeof(decimal),
            true,
            false,
            new BoundPivotCellColumnLineage("pivot", "ir1", typedKey),
            FormatSourceLogicalId: "AMOUNT");
        var pivotOutput = BoundOutputContract.Create(
            "orders#pivot",
            [customer, cell]);
        var pivot = new BoundResolvedPivotRelation(
            discovery,
            [customer],
            [status],
            [metric],
            [new BoundResolvedPivotKey(
                typedKey,
                [new BoundPivotCell("ir1", cell)])],
            pivotOutput,
            "tables.pivot.composables[0]");

        var chart = new ValidChart(
            ChartType.Bar,
            status.ToColumnModel(),
            amount.ToColumnModel(),
            AggregateFn.Sum,
            ChartOrientation.Vertical,
            ChartSortBy.Label,
            SortDir.Asc,
            null,
            null);
        var chartMetric = new BoundColumnContract(
            "v0",
            "sum(Amount)",
            "sum(Amount)",
            typeof(decimal),
            true,
            false,
            new BoundChartColumnLineage("value", "AMOUNT", AggregateFn.Sum),
            FormatSourceLogicalId: "AMOUNT");
        var chartOutput = BoundOutputContract.Create(
            "orders#chart",
            [status, chartMetric]);
        var chartNode = new BoundChartRelation(
            source,
            chart,
            chartOutput,
            "tables.chart.composables[0]");

        var lowerer = new SqlKataRelationLowerer(dialect, EvaluationUtcNow);
        var grouped = lowerer.Lower(discovery);
        var wide = lowerer.Lower(pivot);
        var charted = lowerer.Lower(chartNode);
        var chartedAgain = lowerer.Lower(chartNode);
        var wideAgain = lowerer.Lower(pivot);
        var groupedAgain = lowerer.Lower(discovery);
        var compiler = DialectSupport.GetCompiler(dialect);
        var groupSql = compiler.Compile(grouped.Query);
        var groupSqlAgain = compiler.Compile(groupedAgain.Query);
        var pivotSql = compiler.Compile(wide.Query);
        var pivotSqlAgain = compiler.Compile(wideAgain.Query);
        var chartSql = compiler.Compile(charted.Query);
        var chartSqlAgain = compiler.Compile(chartedAgain.Query);

        Assert.Equal(groupSql.Sql, groupSqlAgain.Sql);
        Assert.Equal(groupSql.Bindings, groupSqlAgain.Bindings);
        Assert.Equal(pivotSql.Sql, pivotSqlAgain.Sql);
        Assert.Equal(pivotSql.Bindings, pivotSqlAgain.Bindings);
        Assert.Equal(chartSql.Sql, chartSqlAgain.Sql);
        Assert.Equal(chartSql.Bindings, chartSqlAgain.Bindings);
        Assert.Equal(
            PhysicalInContractOrder(grouped),
            PhysicalInContractOrder(groupedAgain));
        Assert.Equal(
            PhysicalInContractOrder(wide),
            PhysicalInContractOrder(wideAgain));
        Assert.Equal(
            PhysicalInContractOrder(charted),
            PhysicalInContractOrder(chartedAgain));
        Assert.Empty(groupSql.Bindings);
        Assert.Equal(["SHIPPED"], pivotSql.Bindings);
        Assert.Empty(chartSql.Bindings);
        Assert.Contains("COUNT", groupSql.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GROUP BY", groupSql.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CASE WHEN", pivotSql.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GROUP BY", pivotSql.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SUM", chartSql.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GROUP BY", chartSql.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, grouped.StageCount);
        Assert.Equal(2, wide.StageCount);
        Assert.Equal(1, charted.StageCount);
        Assert.Equal(
            ["CUSTOMER", "STATUS", "__count", "ir1"],
            grouped.Output.Columns.Select(column => column.LogicalId));
        Assert.Equal(["CUSTOMER", cellId],
            wide.Output.Columns.Select(column => column.LogicalId));
        Assert.Equal(["STATUS", "v0"],
            charted.Output.Columns.Select(column => column.LogicalId));
        Assert.Equal(
            ["__irc3", "__irc4", "__irc5", "__irc6"],
            PhysicalInContractOrder(grouped));
        Assert.Equal(
            ["__irc7", "__irc8"],
            PhysicalInContractOrder(wide));
        Assert.Equal(
            ["__irc3", "__irc4"],
            PhysicalInContractOrder(charted));
        AssertPhysicalContract(grouped);
        AssertPhysicalContract(wide);
        AssertPhysicalContract(charted);

        var groupedCount = Assert.IsType<BoundAggregateColumnLineage>(
            grouped.Output.GetRequired("__count").Lineage);
        Assert.Equal(AggregateFn.Count, groupedCount.Function);
        Assert.Null(groupedCount.InputLogicalId);
        var groupedMetric = Assert.IsType<BoundAggregateColumnLineage>(
            grouped.Output.GetRequired("ir1").Lineage);
        Assert.Equal(AggregateFn.Sum, groupedMetric.Function);
        Assert.Equal("AMOUNT", groupedMetric.InputLogicalId);
        Assert.Equal("AMOUNT", grouped.Output.GetRequired("ir1").FormatSourceLogicalId);

        var pivotCell = wide.Output.GetRequired(cellId);
        var pivotLineage = Assert.IsType<BoundPivotCellColumnLineage>(pivotCell.Lineage);
        Assert.Equal("pivot", pivotLineage.OwnerTableId);
        Assert.Equal("ir1", pivotLineage.MetricId);
        Assert.Equal(typedKey.CanonicalIdentity, pivotLineage.Key.CanonicalIdentity);
        Assert.Equal("AMOUNT", pivotCell.FormatSourceLogicalId);

        var chartLineage = Assert.IsType<BoundChartColumnLineage>(
            charted.Output.GetRequired("v0").Lineage);
        Assert.Equal("value", chartLineage.Role);
        Assert.Equal("AMOUNT", chartLineage.InputLogicalId);
        Assert.Equal(AggregateFn.Sum, chartLineage.Function);
        Assert.Equal("AMOUNT", charted.Output.GetRequired("v0").FormatSourceLogicalId);
        Assert.Equal(["group(2,1)", "source"], DebugOperators(discovery));
        Assert.Equal(["pivot(1)", "group(2,1)", "source"], DebugOperators(pivot));
        Assert.Equal(["chart", "source"], DebugOperators(chartNode));
    }

    [Fact]
    public void Export_references_lower_independently_of_sibling_order()
    {
        var source = Source(ReportDialect.Sqlite);
        var parentOutput = source.Output.ApplyMetadata(new CanonicalMetadata(
            false,
            ImmutableDictionary.CreateRange(
                StringComparer.OrdinalIgnoreCase,
                [new KeyValuePair<string, string>("AMOUNT", "Gross amount")]),
            false,
            ImmutableDictionary.CreateRange(
                StringComparer.OrdinalIgnoreCase,
                [new KeyValuePair<string, CanonicalColumnFormat>(
                    "AMOUNT",
                    Format(mask: "currency", bold: true))])));
        var parent = new BoundMetadataRelation(
            source,
            parentOutput,
            "tables.parent.composables[0]");
        var export = BoundTableExport.Create("parent", parent, 0, 0, 0);
        var first = new BoundSearchRelation(
            new BoundExportReference("parent", export, "tables.first.from", "orders#first"),
            "Acme",
            export.Output.Rename("orders#first"));
        var second = new BoundSearchRelation(
            new BoundExportReference("parent", export, "tables.second.from", "orders#second"),
            "Open",
            export.Output.Rename("orders#second"));
        var lowerer = new SqlKataRelationLowerer(ReportDialect.Sqlite, EvaluationUtcNow);
        var compiler = DialectSupport.GetCompiler(ReportDialect.Sqlite);

        var firstLoweredBefore = lowerer.Lower(first);
        var firstBefore = compiler.Compile(firstLoweredBefore.Query);
        var secondLoweredAfter = lowerer.Lower(second);
        var secondAfter = compiler.Compile(secondLoweredAfter.Query);
        var secondLoweredBefore = lowerer.Lower(second);
        var secondBefore = compiler.Compile(secondLoweredBefore.Query);
        var firstLoweredAfter = lowerer.Lower(first);
        var firstAfter = compiler.Compile(firstLoweredAfter.Query);

        Assert.Equal(firstBefore.Sql, firstAfter.Sql);
        Assert.Equal(firstBefore.Bindings, firstAfter.Bindings);
        Assert.Equal(secondBefore.Sql, secondAfter.Sql);
        Assert.Equal(secondBefore.Bindings, secondAfter.Bindings);
        Assert.Equal(firstBefore.Sql, secondBefore.Sql);
        Assert.Equal(
            PhysicalInContractOrder(firstLoweredBefore),
            PhysicalInContractOrder(firstLoweredAfter));
        Assert.Equal(
            PhysicalInContractOrder(secondLoweredBefore),
            PhysicalInContractOrder(secondLoweredAfter));
        Assert.Equal(["%acme%", "%acme%"], firstBefore.Bindings);
        Assert.Equal(["%open%", "%open%"], secondBefore.Bindings);
        Assert.Equal(1, firstLoweredBefore.StageCount);
        Assert.Equal(1, secondLoweredBefore.StageCount);
        Assert.Equal(
            ["CUSTOMER", "STATUS", "AMOUNT"],
            firstLoweredBefore.Output.Columns.Select(column => column.LogicalId));
        Assert.Equal(
            ["__irc0", "__irc1", "__irc2"],
            PhysicalInContractOrder(firstLoweredBefore));
        AssertPhysicalContract(firstLoweredBefore);
        AssertPhysicalContract(secondLoweredBefore);
        Assert.Equal("orders#first", firstLoweredBefore.Output.Name);
        Assert.Equal("orders#second", secondLoweredBefore.Output.Name);

        var parentAmount = parent.Output.GetRequired("AMOUNT");
        var childAmount = first.Output.GetRequired("AMOUNT");
        Assert.Equal("Gross amount", childAmount.EffectiveLabel);
        Assert.Equal("currency", childAmount.ExportedMask);
        Assert.Equal("AMOUNT", childAmount.FormatSourceLogicalId);
        Assert.True(parentAmount.LocalFormat!.Bold);
        Assert.Null(childAmount.LocalFormat!.Bold);
        Assert.Equal(
            ["search", "export-ref(parent)", "metadata", "source"],
            DebugOperators(first));
        Assert.Equal(
            ["search", "export-ref(parent)", "metadata", "source"],
            DebugOperators(second));
    }

    private static BoundOpaqueSqlSource Source(ReportDialect dialect)
        => new(
            "orders",
            "SELECT CUSTOMER, STATUS, AMOUNT FROM ORDERS",
            dialect,
            BoundOutputContract.FromSchema("orders", Schema));

    private static BoundColumnContract AggregateColumn(
        string id,
        string label,
        AggregateFn function,
        string? input,
        Type type)
        => new(
            id,
            label,
            label,
            type,
            function is not AggregateFn.Count,
            false,
            new BoundAggregateColumnLineage(function, input),
            FormatSourceLogicalId: input);

    private static ColumnModel Column(
        string name,
        string label,
        Type type,
        bool nullable = true)
        => new()
        {
            Name = name,
            Label = label,
            ClrType = type,
            IsNullable = nullable,
        };

    private static CanonicalColumnFormat Format(
        string? mask,
        bool? bold = null)
        => new(
            mask,
            null,
            bold,
            null,
            null,
            null,
            [],
            null,
            null,
            null,
            null,
            null);

    private static string[] PhysicalInContractOrder(LoweredRelation relation)
        => relation.Output.Columns
            .Select(column => relation.PhysicalColumns[column.LogicalId])
            .ToArray();

    private static void AssertPhysicalContract(LoweredRelation relation)
        => Assert.Equal(
            relation.Output.Columns
                .Select(column => column.LogicalId)
                .Order(StringComparer.OrdinalIgnoreCase),
            relation.PhysicalColumns.Keys.Order(StringComparer.OrdinalIgnoreCase));

    private static string[] DebugOperators(BoundRelationNode relation)
        => BoundRelationPlanDebug.Render(relation)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => Regex.IsMatch(
                line,
                @"^(?:source|export-ref|compute|filter|group|chart|pivot|metadata|search)(?:\(|\s)"))
            .Select(line => line[..line.IndexOf(" path=", StringComparison.Ordinal)])
            .ToArray();

    private static string NormalizeSql(string sql)
        => Regex.Replace(sql, @"\s+", " ").Trim();

    private static string CanonicalizeSql(string sql)
    {
        var result = NormalizeSql(sql);
        result = Regex.Replace(
            result,
            "\\\"([^\\\"]+)\\\"|\\[([^\\]]+)\\]",
            match => match.Groups[1].Success
                ? match.Groups[1].Value
                : match.Groups[2].Value);
        result = Regex.Replace(
            result,
            @"\bAS (ir_rel_\d+)\b",
            "$1",
            RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"(?:@|:)p\d+", "?");
        result = Regex.Replace(
            result,
            @"(__irc\d+) ilike \?",
            "LOWER($1) LIKE ?",
            RegexOptions.IgnoreCase);
        return Regex.Replace(result, @"\blike\b", "LIKE", RegexOptions.IgnoreCase);
    }

    private const string ExpectedPipelineSql =
        "SELECT * FROM (SELECT * FROM (SELECT * FROM "
        + "(SELECT __irc0, __irc1, __irc2, (__irc2 * ?) AS __irc3 FROM "
        + "(SELECT CUSTOMER AS __irc0, STATUS AS __irc1, AMOUNT AS __irc2 FROM "
        + "(SELECT CUSTOMER, STATUS, AMOUNT FROM ORDERS) ir_base) ir_rel_0) ir_rel_1) "
        + "ir_rel_2 WHERE (__irc3 >= ?)) ir_rel_3 WHERE "
        + "(LOWER(__irc0) LIKE ? OR LOWER(__irc1) LIKE ?)";

    private const string ExpectedPipelinePlan =
        """
        search path=search output=orders#result
          CUSTOMER:text label=Customer lineage=source:CUSTOMER mask=- formatSource=-
          STATUS:text label=Status lineage=source:STATUS mask=- formatSource=-
          AMOUNT:number label=Amount lineage=source:AMOUNT mask=- formatSource=-
          ir1:number label=Double amount lineage=compute:AMOUNT mask=decimal-2 formatSource=ir1
          metadata path=tables.result.composables[2] output=orders#result
            CUSTOMER:text label=Customer lineage=source:CUSTOMER mask=- formatSource=-
            STATUS:text label=Status lineage=source:STATUS mask=- formatSource=-
            AMOUNT:number label=Amount lineage=source:AMOUNT mask=- formatSource=-
            ir1:number label=Double amount lineage=compute:AMOUNT mask=decimal-2 formatSource=ir1
            filter(1) path=tables.result.composables[0] output=orders#result
              CUSTOMER:text label=Customer lineage=source:CUSTOMER mask=- formatSource=-
              STATUS:text label=Status lineage=source:STATUS mask=- formatSource=-
              AMOUNT:number label=Amount lineage=source:AMOUNT mask=- formatSource=-
              ir1:number label=ir1 lineage=compute:AMOUNT mask=- formatSource=-
              compute(ir1) path=tables.result.composables[1] output=orders#result
                CUSTOMER:text label=Customer lineage=source:CUSTOMER mask=- formatSource=-
                STATUS:text label=Status lineage=source:STATUS mask=- formatSource=-
                AMOUNT:number label=Amount lineage=source:AMOUNT mask=- formatSource=-
                ir1:number label=ir1 lineage=compute:AMOUNT mask=- formatSource=-
                source path=definition output=orders
                  CUSTOMER:text label=Customer lineage=source:CUSTOMER mask=- formatSource=-
                  STATUS:text label=Status lineage=source:STATUS mask=- formatSource=-
                  AMOUNT:number label=Amount lineage=source:AMOUNT mask=- formatSource=-
        """;
}
