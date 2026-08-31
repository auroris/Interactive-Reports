// SQL planning entrypoint: translates a bound relation plan into SqlKata queries without
// accepting raw client SQL. Each relational phase produces an explicit query boundary so
// provider compilation remains parameterized and portable.

using InteractiveReport.Core.Expressions;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Schema;
using InteractiveReport.Core.Validation;
using SqlKata;

namespace InteractiveReport.Core.Composition;

/// <summary>
/// Contains one composable SQL relation. Public column names remain protocol identities while
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
    /// <summary>
    /// Creates the initial composable SQL relation for a report definition.
    /// </summary>
    /// <param name="definition">Supplies trusted base SQL, canonical report name, and resolved dialect.</param>
    /// <param name="schema">The discovered source columns in public order.</param>
    /// <returns>A relation that projects every database-authored column to a fresh server-owned physical alias.</returns>
    public static ComposableSqlRelation Definition(
        ReportDefinition definition,
        ReportSchema schema)
    {
        var dialect = definition.GetEffectiveDialect();
        var names = new SqlPhysicalNameAllocator(schema.Columns.Select(column => column.Name));
        var physical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var query = new Query().FromRaw(SqlKataSyntax.PreserveRaw(
            $"({definition.Sql}) {SqlKataSyntax.BaseRelationAlias}"));
        foreach (var column in schema.Columns)
        {
            // Database-authored names may contain SqlKata structure such as dots, "
            // as ", or even "*". Quote each exactly once at the definition boundary and expose
            // only generated identifiers to later compositors.
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

/// <summary>Allocates short portable aliases; no caller or database value enters the generated names.</summary>
internal sealed class SqlPhysicalNameAllocator
{
    private readonly HashSet<string> _columns;
    private int _column;
    private int _relation;

    /// <summary>
    /// Initializes a per-relation allocator with physical names that must not be reused.
    /// </summary>
    /// <param name="reservedColumns">Physical column names the allocator must not reuse; defaults to an empty set.</param>
    public SqlPhysicalNameAllocator(IEnumerable<string>? reservedColumns = null)
    {
        _columns = new HashSet<string>(
            reservedColumns ?? [],
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Allocates the next collision-free physical column alias.
    /// </summary>
    /// <returns>A collision-free <c>__ircN</c> alias and reserves it for later allocations.</returns>
    public string Column()
    {
        string candidate;
        do candidate = $"__irc{_column++}";
        while (!_columns.Add(candidate));
        return candidate;
    }

    /// <summary>
    /// Allocates the next collision-free relation alias.
    /// </summary>
    /// <returns>The next <c>ir_rel_N</c> alias from this allocator.</returns>
    public string Relation() => $"ir_rel_{_relation++}";
}

/// <summary>
/// Lowers bound relational stages to provider-neutral SQLKata query trees while maintaining a strict
/// separation between public logical ids and generated physical identifiers.
/// </summary>
internal static class ComposableSqlPlanner
{
    /// <summary>
    /// Adds request-local text search predicates to a composable SQL relation.
    /// </summary>
    /// <param name="source">The completed relation to wrap in a searchable derived table.</param>
    /// <param name="search">Optional toolbar text, trimmed before binding.</param>
    /// <returns>The original relation when search is blank or no text column exists; otherwise, a filtered relation.</returns>
    /// <remarks>Allocates a relation alias from <paramref name="source"/>'s physical-name allocator.</remarks>
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

    /// <summary>
    /// Adds one bound computed-column projection to a composable SQL relation.
    /// </summary>
    /// <param name="source">The completed relation to project into a new computation stage.</param>
    /// <param name="rule">The already-bound value expression and synthetic output column.</param>
    /// <param name="dialect">The database dialect whose SQL rules apply.</param>
    /// <param name="evaluationUtcNow">The fixed UTC timestamp used to evaluate time-sensitive expressions consistently throughout the request.</param>
    /// <returns>A relation exposing every input column plus the computed column.</returns>
    /// <remarks>Allocates one column and two relation aliases from <paramref name="source"/>'s allocator.</remarks>
    public static ComposableSqlRelation ApplyComputed(
        ComposableSqlRelation source,
        CompiledRule<DefineColumnEffect> rule,
        ReportDialect dialect,
        DateTime evaluationUtcNow)
    {
        var current = Addressable(source);
        var physical = new Dictionary<string, string>(
            source.PhysicalColumns,
            StringComparer.OrdinalIgnoreCase);
        current.Select(source.Schema.Columns
            .Select(column => physical[column.Name])
            .ToArray());
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
        var schema = source.Schema.Extend(source.SchemaName, [rule.Effect.Column]);
        return source with
        {
            Query = current,
            Schema = schema,
            PhysicalColumns = physical,
            NestingDepth = source.NestingDepth + 2,
        };
    }

    /// <summary>
    /// Adds bound filter predicates to a composable SQL relation.
    /// </summary>
    /// <param name="source">The completed relation to wrap in a filter stage.</param>
    /// <param name="predicates">Already-bound predicates applied with AND semantics.</param>
    /// <param name="dialect">The database dialect whose SQL rules apply.</param>
    /// <param name="evaluationUtcNow">The fixed UTC timestamp used to evaluate time-sensitive expressions consistently throughout the request.</param>
    /// <returns>The original relation for an empty predicate list; otherwise, a filtered relation with the same schema.</returns>
    /// <remarks>Allocates a relation alias when predicates are present.</remarks>
    public static ComposableSqlRelation ApplyFilters(
        ComposableSqlRelation source,
        IReadOnlyList<CompiledRule<IncludeRowEffect>> predicates,
        ReportDialect dialect,
        DateTime evaluationUtcNow)
    {
        if (predicates.Count == 0) return source;
        var current = Addressable(source);
        foreach (var predicate in predicates)
            ExpressionRuleSqlApplicator.ApplyRowPredicate(
                current,
                predicate,
                dialect,
                evaluationUtcNow,
                source.PhysicalColumns);
        return source with
        {
            Query = current,
            NestingDepth = source.NestingDepth + 1,
        };
    }

    /// <summary>
    /// Composes a grouped SQL relation from dimensions, metrics, and the count column.
    /// </summary>
    /// <param name="source">The completed relation whose rows will be grouped.</param>
    /// <param name="schemaName">The logical name assigned to the grouped output schema.</param>
    /// <param name="dimensions">Grouping columns in public output order.</param>
    /// <param name="metrics">Validated aggregate metrics in public output order.</param>
    /// <param name="dialect">The database dialect whose SQL rules apply.</param>
    /// <param name="countName">The logical output name assigned to the generated count column; defaults to <c>"__count"</c>.</param>
    /// <returns>A grouped relation exposing dimensions, count, then metrics.</returns>
    /// <remarks>Uses a ranked two-stage plan when any metric requests median and advances the shared physical-name allocator.</remarks>
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

    /// <summary>
    /// Composes the projection and aggregation required by a chart terminal.
    /// </summary>
    /// <param name="source">The completed relation supplying chart rows.</param>
    /// <param name="schemaName">The logical name assigned to the chart output schema.</param>
    /// <param name="chart">The validated label, value, and optional aggregate definition.</param>
    /// <param name="dialect">The database dialect whose SQL rules apply.</param>
    /// <returns>A two-column label/value relation, grouped when the chart has an aggregate.</returns>
    /// <remarks>Uses the ranked median plan for median charts and advances the shared physical-name allocator.</remarks>
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

    /// <summary>
    /// Composes the conditional-aggregation query for a resolved wide pivot.
    /// </summary>
    /// <param name="grouped">The grouped query that supplies rows for the pivot projection.</param>
    /// <param name="schemaName">The logical name assigned to the wide output schema.</param>
    /// <param name="rowDimensions">The ordered dimensions that identify grouping or pivot rows.</param>
    /// <param name="columnDimensions">The ordered pivot dimensions that identify output columns.</param>
    /// <param name="metrics">The pivot metric definitions to aggregate.</param>
    /// <param name="keys">The canonical pivot keys that identify output cells.</param>
    /// <param name="dialect">The database dialect whose SQL rules apply.</param>
    /// <returns>A wide relation containing row dimensions followed by all registered dynamic cells.</returns>
    /// <remarks>Embeds only server-authored identifier syntax; typed key values remain positional bindings.</remarks>
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

    /// <summary>
    /// Projects the selected public columns from a freshly aliased wrapper around the source relation.
    /// </summary>
    /// <param name="source">The relation to wrap as a derived table.</param>
    /// <param name="columns">The public columns to project in result ordinal order.</param>
    /// <returns>A fresh SQLKata projection using only mapped physical names.</returns>
    public static Query Project(
        ComposableSqlRelation source,
        IReadOnlyList<ColumnModel> columns)
        => Addressable(source).Select(columns.Select(column => source.PhysicalColumns[column.Name]).ToArray());

    /// <summary>
    /// Wraps a cloned relation query in a freshly allocated derived-table alias.
    /// </summary>
    /// <param name="source">The relation whose physical projections need an addressable scope.</param>
    /// <returns>A new outer SQLKata query.</returns>
    private static Query Addressable(ComposableSqlRelation source)
        => new Query().From(source.Query.Clone().As(source.Names.Relation()));

    /// <summary>
    /// Composes a grouped relation with dialect-specific median calculations.
    /// </summary>
    /// <param name="source">The completed relation whose rows will be ranked and grouped.</param>
    /// <param name="schemaName">The logical name assigned to the grouped output schema.</param>
    /// <param name="dimensions">The ordered dimensions that identify grouping or pivot rows.</param>
    /// <param name="metrics">The pivot metric definitions to aggregate.</param>
    /// <param name="dialect">The database dialect whose SQL rules apply.</param>
    /// <param name="countName">The logical output name assigned to the generated count column.</param>
    /// <returns>A two-stage grouped relation with exact odd/even medians and ordinary aggregates.</returns>
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

    /// <summary>
    /// Composes the chart projection when one or more metrics use median.
    /// </summary>
    /// <param name="source">The completed relation supplying chart rows.</param>
    /// <param name="schemaName">The logical name assigned to the chart output schema.</param>
    /// <param name="chart">The validated median chart definition.</param>
    /// <param name="dialect">The database dialect whose SQL rules apply.</param>
    /// <returns>A two-column label/value relation projected from a median group.</returns>
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

    /// <summary>
    /// Builds the dialect-specific SQL expression for one median row position.
    /// </summary>
    /// <param name="countAlias">The quoted SQL alias of the generated count expression.</param>
    /// <param name="add">The increment applied when calculating the median position.</param>
    /// <param name="dialect">The database dialect whose SQL rules apply.</param>
    /// <returns>The SQL expression for one median row position.</returns>
    private static string HalfPosition(string countAlias, int add, ReportDialect dialect)
        => dialect == ReportDialect.Oracle
            ? $"FLOOR(({Identifier(dialect, countAlias)} + {add}) / 2)"
            : $"(({Identifier(dialect, countAlias)} + {add}) / 2)";

    /// <summary>
    /// Quotes a physical SQL identifier according to the selected dialect.
    /// </summary>
    /// <param name="dialect">The database dialect whose SQL rules apply.</param>
    /// <param name="name">A server-allocated physical column or relation name.</param>
    /// <returns>A dialect-quoted SQL identifier safe for raw SQL fragments.</returns>
    private static string Identifier(ReportDialect dialect, string name)
        => SqlKataSyntax.Identifier(dialect, name);

    /// <summary>
    /// Allocates a case-insensitively unique logical column name.
    /// </summary>
    /// <param name="existing">Logical names already used in the output scope.</param>
    /// <param name="candidate">The preferred generated name.</param>
    /// <returns>The unique logical name text.</returns>
    private static string UniqueLogicalName(IEnumerable<string> existing, string candidate)
    {
        var used = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        while (!used.Add(candidate)) candidate = $"_{candidate}";
        return candidate;
    }

    /// <summary>
    /// Resolves the logical output column associated with a chart or pivot metric.
    /// </summary>
    /// <param name="metric">The validated aggregate id, input column, and function.</param>
    /// <returns>A synthetic column with the public aggregate label and function-dependent CLR type.</returns>
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

    /// <summary>
    /// Converts result-column metadata into a bound output-column contract.
    /// </summary>
    /// <param name="column">The public chart result-column metadata.</param>
    /// <param name="chart">The validated chart used to preserve the original label-column model.</param>
    /// <returns>The original label model or a synthetic typed chart-value model.</returns>
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

/// <summary>Contains one resolved pivot key's SQL values and public cell definitions.</summary>
internal sealed record PivotColumnKey(
    object?[] Values,
    IReadOnlyList<PivotCellColumn> Cells);

/// <summary>Maps a grouped metric source to one registered wide pivot output column.</summary>
internal sealed record PivotCellColumn(string SourceName, ColumnModel Column);
