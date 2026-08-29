using InteractiveReport.Core.Expressions;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Schema;
using InteractiveReport.Core.Validation;
using SqlKata;

namespace InteractiveReport.Core.Composition;

/// <summary>
/// One composable SQL relation. Public column names remain protocol identities while
/// PhysicalColumns names the bounded, server-authored aliases used inside SQL. Keeping
/// those namespaces separate is essential for Pivot cells, whose public names contain
/// data-derived JSON and must never be interpolated as SQL identifiers.
/// </summary>
internal sealed record ComposableSqlRelation(
    Query Query,
    ReportSchema Schema,
    IReadOnlyDictionary<string, string> PhysicalColumns,
    SqlPhysicalNameAllocator Names,
    string SchemaName,
    int NestingDepth)
{
    public static ComposableSqlRelation Definition(
        ReportDefinition definition,
        ReportSchema schema)
    {
        var dialect = definition.GetEffectiveDialect();
        var names = new SqlPhysicalNameAllocator(schema.Columns.Select(column => column.Name));
        var physical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var query = new Query().FromRaw(SqlKataSyntax.PreserveRaw(
            $"({definition.Sql}) {QueryComposer.BaseAlias}"));
        foreach (var column in schema.Columns)
        {
            // Database-authored names may contain SqlKata structure such as dots,
            // " as ", or even "*". Quote each exactly once at the definition
            // boundary and expose only generated identifiers to later compositors.
            var alias = names.Column();
            query.SelectRaw(
                $"{SqlKataSyntax.Identifier(dialect, column.Name)} AS {SqlKataSyntax.Identifier(dialect, alias)}");
            physical[column.Name] = alias;
        }
        return new ComposableSqlRelation(
            query,
            schema,
            physical,
            names,
            definition.Name,
            0);
    }
}

/// <summary>Short portable aliases. No caller or database value enters these names.</summary>
internal sealed class SqlPhysicalNameAllocator
{
    private readonly HashSet<string> _columns;
    private int _column;
    private int _relation;

    public SqlPhysicalNameAllocator(IEnumerable<string>? reservedColumns = null)
    {
        _columns = new HashSet<string>(
            reservedColumns ?? [],
            StringComparer.OrdinalIgnoreCase);
    }

    public string Column()
    {
        string candidate;
        do candidate = $"__irc{_column++}";
        while (!_columns.Add(candidate));
        return candidate;
    }

    public string Relation() => $"ir_rel_{_relation++}";
}

internal static class ComposableSqlPlanner
{
    public static ComposableSqlRelation ApplySearch(
        ComposableSqlRelation source,
        string? search)
    {
        if (string.IsNullOrWhiteSpace(search)) return source;
        var textColumns = source.Schema.Columns
            .Where(column => column.Kind == ColumnKind.Text)
            .ToList();
        if (textColumns.Count == 0) return source;

        var query = Addressable(source);
        query.Where(nested =>
        {
            foreach (var column in textColumns)
                nested.OrWhereContains(
                    source.PhysicalColumns[column.Name],
                    search.Trim(),
                    caseSensitive: false);
            return nested;
        });
        return source with { Query = query, NestingDepth = source.NestingDepth + 1 };
    }

    public static ComposableSqlRelation ApplyOperations(
        ComposableSqlRelation source,
        IReadOnlyList<ValidTableOperation> operations,
        ReportDialect dialect,
        DateTime evaluationUtcNow)
    {
        if (operations.Count == 0) return source;
        var current = Addressable(source);
        var physical = new Dictionary<string, string>(
            source.PhysicalColumns,
            StringComparer.OrdinalIgnoreCase);

        foreach (var operation in operations)
        {
            if (operation.Definitions.Count > 0)
            {
                foreach (var rule in operation.Definitions)
                {
                    current.Select(physical.Values.ToArray());
                    var alias = source.Names.Column();
                    ExpressionRuleSqlApplicator.ApplyDefinition(
                        current,
                        rule,
                        dialect,
                        evaluationUtcNow,
                        physical,
                        alias);
                    physical[rule.Effect.Column.Name] = alias;
                    current = new Query().From(current.As(source.Names.Relation()));
                }
            }

            foreach (var rule in operation.Predicates)
                ExpressionRuleSqlApplicator.ApplyRowPredicate(
                    current,
                    rule,
                    dialect,
                    evaluationUtcNow,
                    physical);
        }

        var schema = source.Schema.Extend(
            source.SchemaName,
            operations.SelectMany(operation => operation.Definitions)
                .Select(rule => rule.Effect.Column));
        var definitionCount = operations.Sum(operation => operation.Definitions.Count);
        return source with
        {
            Query = current,
            Schema = schema,
            PhysicalColumns = physical,
            NestingDepth = source.NestingDepth + 1 + definitionCount,
        };
    }

    public static ComposableSqlRelation Group(
        ComposableSqlRelation source,
        string schemaName,
        IReadOnlyList<ColumnModel> dimensions,
        IReadOnlyList<ValidMetric> metrics,
        ReportDialect dialect,
        string countName = "__count")
    {
        if (metrics.Any(metric => metric.Fn == AggregateFn.Median))
            return GroupWithMedian(source, schemaName, dimensions, metrics, dialect, countName);

        var query = Addressable(source);
        var physical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var output = new List<ColumnModel>();
        var groupBy = new List<string>();

        foreach (var dimension in dimensions)
        {
            var input = source.PhysicalColumns[dimension.Name];
            var alias = source.Names.Column();
            query.SelectRaw($"{Identifier(dialect, input)} AS {Identifier(dialect, alias)}");
            groupBy.Add(input);
            physical[dimension.Name] = alias;
            output.Add(dimension);
        }

        var countAlias = source.Names.Column();
        query.SelectRaw($"COUNT(*) AS {Identifier(dialect, countAlias)}");
        physical[countName] = countAlias;
        output.Add(new ColumnModel
        {
            Name = countName,
            Label = "Count",
            ClrType = typeof(long),
            IsNullable = false,
        });

        foreach (var metric in metrics)
        {
            var input = source.PhysicalColumns[metric.Column.Name];
            var alias = source.Names.Column();
            query.SelectRaw(
                $"{DialectSupport.AggregateExpression(dialect, metric.Fn, Identifier(dialect, input))} AS {Identifier(dialect, alias)}");
            physical[metric.Id] = alias;
            output.Add(MetricColumn(metric));
        }

        if (groupBy.Count > 0) query.GroupBy(groupBy.ToArray());
        return new ComposableSqlRelation(
            query,
            ReportSchema.Create(schemaName, output),
            physical,
            source.Names,
            schemaName,
            source.NestingDepth + 1);
    }

    public static ComposableSqlRelation Chart(
        ComposableSqlRelation source,
        string schemaName,
        ValidChart chart,
        ReportDialect dialect)
    {
        var labelAlias = source.Names.Column();
        var metricAlias = source.Names.Column();
        var labelInput = source.PhysicalColumns[chart.Label.Name];
        if (chart.Fn == AggregateFn.Median)
            return MedianChart(source, schemaName, chart, dialect);

        var query = Addressable(source);
        var columns = ReportResultColumns.ForChart(chart);

        query.SelectRaw(
            $"{Identifier(dialect, labelInput)} AS {Identifier(dialect, labelAlias)}");
        if (chart.Fn is { } function)
        {
            if (chart.Value is null)
                query.SelectRaw($"COUNT(*) AS {Identifier(dialect, metricAlias)}");
            else
            {
                var input = source.PhysicalColumns[chart.Value.Name];
                query.SelectRaw(
                    $"{DialectSupport.AggregateExpression(dialect, function, Identifier(dialect, input))} AS {Identifier(dialect, metricAlias)}");
            }
            query.GroupBy(labelInput);
        }
        else
        {
            var input = source.PhysicalColumns[chart.Value!.Name];
            query.SelectRaw(
                $"{Identifier(dialect, input)} AS {Identifier(dialect, metricAlias)}");
        }

        var schema = ReportSchema.Create(
            schemaName,
            columns.Select(column => ColumnFromInfo(column, chart)));
        var physical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [columns[0].Name] = labelAlias,
            [columns[1].Name] = metricAlias,
        };
        return new ComposableSqlRelation(
            query,
            schema,
            physical,
            source.Names,
            schemaName,
            source.NestingDepth + 1);
    }

    public static ComposableSqlRelation PivotWide(
        ComposableSqlRelation grouped,
        string schemaName,
        IReadOnlyList<ColumnModel> rowDimensions,
        IReadOnlyList<ColumnModel> columnDimensions,
        IReadOnlyList<ValidMetric> metrics,
        IReadOnlyList<PivotColumnKey> keys,
        ReportDialect dialect)
    {
        var relationAlias = grouped.Names.Relation();
        var query = new Query().From(grouped.Query.As(relationAlias));
        var output = new List<ColumnModel>();
        var physical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var groupBy = new List<string>();

        foreach (var dimension in rowDimensions)
        {
            var input = grouped.PhysicalColumns[dimension.Name];
            var alias = grouped.Names.Column();
            query.SelectRaw($"{Identifier(dialect, input)} AS {Identifier(dialect, alias)}");
            groupBy.Add(input);
            physical[dimension.Name] = alias;
            output.Add(dimension);
        }

        foreach (var key in keys)
        {
            var condition = new List<string>(columnDimensions.Count);
            var bindings = new List<object>();
            for (var index = 0; index < columnDimensions.Count; index++)
            {
                var input = grouped.PhysicalColumns[columnDimensions[index].Name];
                if (key.Values[index] is null)
                    condition.Add($"{Identifier(dialect, input)} IS NULL");
                else
                {
                    condition.Add($"{Identifier(dialect, input)} = ?");
                    bindings.Add(key.Values[index]!);
                }
            }

            var predicate = string.Join(" AND ", condition);
            foreach (var cell in key.Cells)
            {
                var value = grouped.PhysicalColumns[cell.SourceName];
                var alias = grouped.Names.Column();
                query.SelectRaw(
                    $"MAX(CASE WHEN {predicate} THEN {Identifier(dialect, value)} END) AS {Identifier(dialect, alias)}",
                    bindings.ToArray());
                physical[cell.Column.Name] = alias;
                output.Add(cell.Column);
            }
        }

        if (groupBy.Count > 0) query.GroupBy(groupBy.ToArray());
        return new ComposableSqlRelation(
            query,
            ReportSchema.Create(schemaName, output),
            physical,
            grouped.Names,
            schemaName,
            grouped.NestingDepth + 1);
    }

    public static Query Project(
        ComposableSqlRelation source,
        IReadOnlyList<ColumnModel> columns)
        => Addressable(source).Select(columns.Select(column => source.PhysicalColumns[column.Name]).ToArray());

    private static Query Addressable(ComposableSqlRelation source)
        => new Query().From(source.Query.Clone().As(source.Names.Relation()));

    private static ComposableSqlRelation GroupWithMedian(
        ComposableSqlRelation source,
        string schemaName,
        IReadOnlyList<ColumnModel> dimensions,
        IReadOnlyList<ValidMetric> metrics,
        ReportDialect dialect,
        string countName)
    {
        var dimensionInputs = dimensions
            .Select(dimension => source.PhysicalColumns[dimension.Name])
            .ToArray();
        var metricInputs = metrics
            .Select(metric => source.PhysicalColumns[metric.Column.Name])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var ranked = Addressable(source)
            .Select(dimensionInputs
                .Concat(metricInputs)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray());
        var partition = dimensionInputs.Length == 0
            ? ""
            : $"PARTITION BY {string.Join(", ", dimensionInputs.Select(name => Identifier(dialect, name)))} ";
        var medianAliases = new Dictionary<int, (string Rank, string Count)>();
        for (var index = 0; index < metrics.Count; index++)
        {
            if (metrics[index].Fn != AggregateFn.Median) continue;
            var input = source.PhysicalColumns[metrics[index].Column.Name];
            var rank = source.Names.Column();
            var count = source.Names.Column();
            var quotedInput = Identifier(dialect, input);
            ranked.SelectRaw(
                $"ROW_NUMBER() OVER ({partition}ORDER BY CASE WHEN {quotedInput} IS NULL THEN 1 ELSE 0 END, {quotedInput}) AS {Identifier(dialect, rank)}");
            ranked.SelectRaw(
                $"COUNT({quotedInput}) OVER ({partition.TrimEnd()}) AS {Identifier(dialect, count)}");
            medianAliases[index] = (rank, count);
        }

        var query = new Query().From(ranked.As(source.Names.Relation()));
        var physical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var output = new List<ColumnModel>();
        foreach (var dimension in dimensions)
        {
            var input = source.PhysicalColumns[dimension.Name];
            var alias = source.Names.Column();
            query.SelectRaw($"{Identifier(dialect, input)} AS {Identifier(dialect, alias)}");
            physical[dimension.Name] = alias;
            output.Add(dimension);
        }
        var countAlias = source.Names.Column();
        query.SelectRaw($"COUNT(*) AS {Identifier(dialect, countAlias)}");
        physical[countName] = countAlias;
        output.Add(new ColumnModel
        {
            Name = countName,
            Label = "Count",
            ClrType = typeof(long),
            IsNullable = false,
        });
        for (var index = 0; index < metrics.Count; index++)
        {
            var metric = metrics[index];
            var input = source.PhysicalColumns[metric.Column.Name];
            var alias = source.Names.Column();
            if (metric.Fn != AggregateFn.Median)
                query.SelectRaw(
                    $"{DialectSupport.AggregateExpression(dialect, metric.Fn, Identifier(dialect, input))} AS {Identifier(dialect, alias)}");
            else
            {
                var median = medianAliases[index];
                var lower = HalfPosition(median.Count, 1, dialect);
                var upper = HalfPosition(median.Count, 2, dialect);
                var candidate =
                    $"CASE WHEN {Identifier(dialect, median.Rank)} IN ({lower}, {upper}) THEN {Identifier(dialect, input)} END";
                query.SelectRaw(dialect == ReportDialect.SqlServer
                    ? $"AVG(CAST({candidate} AS FLOAT)) AS {Identifier(dialect, alias)}"
                    : $"AVG({candidate}) AS {Identifier(dialect, alias)}");
            }
            physical[metric.Id] = alias;
            output.Add(MetricColumn(metric));
        }
        if (dimensionInputs.Length > 0) query.GroupBy(dimensionInputs);
        return new ComposableSqlRelation(
            query,
            ReportSchema.Create(schemaName, output),
            physical,
            source.Names,
            schemaName,
            source.NestingDepth + 2);
    }

    private static ComposableSqlRelation MedianChart(
        ComposableSqlRelation source,
        string schemaName,
        ValidChart chart,
        ReportDialect dialect)
    {
        var metricName = "__ir_chart_value";
        while (source.Schema.Lookup.ContainsKey(metricName)
               || string.Equals(metricName, chart.Label.Name, StringComparison.OrdinalIgnoreCase))
            metricName = $"_{metricName}";
        var countName = UniqueLogicalName(
            source.Schema.Columns.Select(column => column.Name)
                .Concat([chart.Label.Name, metricName]),
            "__ir_chart_count");
        var grouped = GroupWithMedian(
            source,
            $"{schemaName}#median",
            [chart.Label],
            [new ValidMetric(metricName, chart.Value!, AggregateFn.Median)],
            dialect,
            countName);
        var columns = ReportResultColumns.ForChart(chart);
        var query = Addressable(grouped);
        var labelAlias = source.Names.Column();
        var metricAlias = source.Names.Column();
        query.SelectRaw(
            $"{Identifier(dialect, grouped.PhysicalColumns[chart.Label.Name])} AS {Identifier(dialect, labelAlias)}");
        query.SelectRaw(
            $"{Identifier(dialect, grouped.PhysicalColumns[metricName])} AS {Identifier(dialect, metricAlias)}");
        var schema = ReportSchema.Create(
            schemaName,
            columns.Select(column => ColumnFromInfo(column, chart)));
        return new ComposableSqlRelation(
            query,
            schema,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [columns[0].Name] = labelAlias,
                [columns[1].Name] = metricAlias,
            },
            source.Names,
            schemaName,
            grouped.NestingDepth + 1);
    }

    private static string HalfPosition(string countAlias, int add, ReportDialect dialect)
        => dialect == ReportDialect.Oracle
            ? $"FLOOR(({Identifier(dialect, countAlias)} + {add}) / 2)"
            : $"(({Identifier(dialect, countAlias)} + {add}) / 2)";

    private static string Identifier(ReportDialect dialect, string name)
        => SqlKataSyntax.Identifier(dialect, name);

    private static string UniqueLogicalName(IEnumerable<string> existing, string candidate)
    {
        var used = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        while (!used.Add(candidate)) candidate = $"_{candidate}";
        return candidate;
    }

    private static ColumnModel MetricColumn(ValidMetric metric)
        => new()
        {
            Name = metric.Id,
            Label = ReportResultColumns.AggregateLabel(metric.ToAggregate()),
            ClrType = metric.Fn switch
            {
                AggregateFn.Min or AggregateFn.Max => metric.Column.ClrType,
                AggregateFn.Count or AggregateFn.CountDistinct => typeof(long),
                _ => typeof(decimal),
            },
        };

    private static ColumnModel ColumnFromInfo(ColumnInfo column, ValidChart chart)
    {
        if (string.Equals(column.Name, chart.Label.Name, StringComparison.OrdinalIgnoreCase))
            return chart.Label;
        return new ColumnModel
        {
            Name = column.Name,
            Label = column.Label,
            ClrType = column.Type switch
            {
                "number" => typeof(decimal),
                "date" => typeof(DateTime),
                "bool" => typeof(bool),
                "text" => typeof(string),
                _ => typeof(object),
            },
            IsComputed = column.Computed,
        };
    }
}

internal sealed record PivotColumnKey(
    object?[] Values,
    IReadOnlyList<PivotCellColumn> Cells);

internal sealed record PivotCellColumn(string SourceName, ColumnModel Column);
