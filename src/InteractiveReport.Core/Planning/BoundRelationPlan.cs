using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using InteractiveReport.Core.Expressions;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Schema;
using InteractiveReport.Core.Validation;

namespace InteractiveReport.Core.Planning;

/// <summary>
/// Immutable relational syntax after schema binding. Every node declares its exact
/// public output contract; SQLKata does not appear in this model.
/// </summary>
internal abstract record BoundRelationNode
{
    protected BoundRelationNode(
        string sourcePath,
        BoundOutputContract output)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(output);
        SourcePath = sourcePath;
        Output = output;
    }

    public string SourcePath { get; }
    public BoundOutputContract Output { get; }
    public ReportSchema Schema => Output.ToReportSchema();
}

/// <summary>The trusted configured SELECT. Its text remains opaque to planning.</summary>
internal sealed record BoundOpaqueSqlSource : BoundRelationNode
{
    public BoundOpaqueSqlSource(
        string definitionName,
        string sql,
        ReportDialect dialect,
        BoundOutputContract output)
        : base("definition", output)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        DefinitionName = definitionName;
        Sql = sql;
        Dialect = dialect;
    }

    public string DefinitionName { get; }
    public string Sql { get; }
    public ReportDialect Dialect { get; }
}

/// <summary>
/// Root of a child table. It imports only the named parent's completed Export; the
/// parent's local-result package is structurally unreachable from this node.
/// </summary>
internal sealed record BoundExportReference : BoundRelationNode
{
    public BoundExportReference(
        string tableId,
        BoundTableExport target,
        string sourcePath,
        string? outputName = null)
        : base(sourcePath, target.Output.Rename(outputName ?? target.Output.Name))
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableId);
        ArgumentNullException.ThrowIfNull(target);
        TableId = tableId;
        Target = target;
    }

    public string TableId { get; }
    public BoundTableExport Target { get; }
}

internal sealed record BoundComputeRelation : BoundRelationNode
{
    public BoundComputeRelation(
        BoundRelationNode input,
        BoundComputedColumn column,
        BoundOutputContract output,
        string sourcePath)
        : base(sourcePath, output)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(column);
        Input = input;
        Column = column;
    }

    public BoundRelationNode Input { get; }
    public BoundComputedColumn Column { get; }
}

internal sealed record BoundFilterRelation : BoundRelationNode
{
    public BoundFilterRelation(
        BoundRelationNode input,
        ImmutableArray<BoundRowPredicate> predicates,
        BoundOutputContract output,
        string sourcePath)
        : base(sourcePath, output)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (predicates.IsDefaultOrEmpty)
            throw new ArgumentException(
                "A bound filter relation requires at least one predicate.",
                nameof(predicates));
        Input = input;
        Predicates = predicates;
    }

    public BoundRelationNode Input { get; }
    public ImmutableArray<BoundRowPredicate> Predicates { get; }
}

internal sealed record BoundGroupRelation : BoundRelationNode
{
    public BoundGroupRelation(
        BoundRelationNode input,
        ImmutableArray<BoundColumnContract> dimensions,
        ImmutableArray<BoundMetric> metrics,
        BoundColumnContract countColumn,
        BoundOutputContract output,
        string sourcePath)
        : base(sourcePath, output)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(countColumn);
        if (dimensions.IsDefault) throw new ArgumentException("Dimensions are required.", nameof(dimensions));
        if (metrics.IsDefault) throw new ArgumentException("Metrics are required.", nameof(metrics));
        Input = input;
        Dimensions = dimensions;
        Metrics = metrics;
        CountColumn = countColumn;
    }

    public BoundRelationNode Input { get; }
    public ImmutableArray<BoundColumnContract> Dimensions { get; }
    public ImmutableArray<BoundMetric> Metrics { get; }
    public BoundColumnContract CountColumn { get; }
}

internal sealed record BoundChartRelation : BoundRelationNode
{
    public BoundChartRelation(
        BoundRelationNode input,
        ValidChart chart,
        BoundOutputContract output,
        string sourcePath)
        : base(sourcePath, output)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(chart);
        Input = input;
        Chart = chart;
    }

    public BoundRelationNode Input { get; }
    public ValidChart Chart { get; }
}

/// <summary>
/// A Pivot whose data-dependent keys and public columns have already been registered.
/// Before registration, orchestration carries a separate pending continuation rather
/// than pretending that this node has a knowable output contract.
/// </summary>
internal sealed record BoundResolvedPivotRelation : BoundRelationNode
{
    public BoundResolvedPivotRelation(
        BoundGroupRelation discovery,
        ImmutableArray<BoundColumnContract> rowDimensions,
        ImmutableArray<BoundColumnContract> columnDimensions,
        ImmutableArray<BoundMetric> metrics,
        ImmutableArray<BoundResolvedPivotKey> keys,
        BoundOutputContract output,
        string sourcePath)
        : base(sourcePath, output)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        if (rowDimensions.IsDefault) throw new ArgumentException("Row dimensions are required.", nameof(rowDimensions));
        if (columnDimensions.IsDefault) throw new ArgumentException("Column dimensions are required.", nameof(columnDimensions));
        if (metrics.IsDefault) throw new ArgumentException("Metrics are required.", nameof(metrics));
        if (keys.IsDefault) throw new ArgumentException("Pivot keys are required.", nameof(keys));
        Discovery = discovery;
        RowDimensions = rowDimensions;
        ColumnDimensions = columnDimensions;
        Metrics = metrics;
        Keys = keys;
    }

    public BoundGroupRelation Discovery { get; }
    public ImmutableArray<BoundColumnContract> RowDimensions { get; }
    public ImmutableArray<BoundColumnContract> ColumnDimensions { get; }
    public ImmutableArray<BoundMetric> Metrics { get; }
    public ImmutableArray<BoundResolvedPivotKey> Keys { get; }
}

/// <summary>Contract-only relation transformation; lowering emits no SQL stage.</summary>
internal sealed record BoundMetadataRelation : BoundRelationNode
{
    public BoundMetadataRelation(
        BoundRelationNode input,
        BoundOutputContract output,
        string sourcePath)
        : base(sourcePath, output)
    {
        ArgumentNullException.ThrowIfNull(input);
        Input = input;
    }

    public BoundRelationNode Input { get; }
}

/// <summary>Request-local row restriction over the completed active relation.</summary>
internal sealed record BoundSearchRelation : BoundRelationNode
{
    public BoundSearchRelation(
        BoundRelationNode input,
        string search,
        BoundOutputContract output,
        string sourcePath = "search")
        : base(sourcePath, output)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(search);
        Input = input;
        Search = search.Trim();
    }

    public BoundRelationNode Input { get; }
    public string Search { get; }
}

internal sealed record BoundComputedColumn(
    BoundExpression Expression,
    BoundColumnContract Output,
    string SourcePath);

internal sealed record BoundRowPredicate(
    BoundExpression Expression,
    string SourcePath);

internal sealed record BoundMetric(
    string Id,
    BoundColumnContract Input,
    AggregateFn Function,
    string SourcePath);

internal sealed record BoundResolvedPivotKey(
    BoundPivotTypedKey Key,
    ImmutableArray<BoundPivotCell> Cells);

internal sealed record BoundPivotCell(
    string SourceLogicalId,
    BoundColumnContract Output);

/// <summary>
/// The completed inheritable package for one named table. Output is already reduced
/// to the child-visible presentation channel; Relation retains the owner's immutable
/// logical tree for independent lowering.
/// </summary>
internal sealed record BoundTableExport(
    string TableId,
    BoundRelationNode Relation,
    BoundOutputContract Output,
    int ShapeCount,
    int ComputedRuleCount,
    int FilterRuleCount)
{
    public static BoundTableExport Create(
        string tableId,
        BoundRelationNode relation,
        int shapeCount,
        int computedRuleCount,
        int filterRuleCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableId);
        ArgumentNullException.ThrowIfNull(relation);
        return new BoundTableExport(
            tableId,
            relation,
            relation.Output.ForChild(),
            shapeCount,
            computedRuleCount,
            filterRuleCount);
    }
}

/// <summary>
/// One canonical, type-tagged Pivot key. Equality uses the length-framed canonical
/// identity, never provider object identity or a display string.
/// </summary>
internal sealed class BoundPivotTypedKey : IEquatable<BoundPivotTypedKey>
{
    private BoundPivotTypedKey(ImmutableArray<BoundPivotKeyPart> parts)
    {
        Parts = parts;
        CanonicalIdentity = BuildIdentity(parts);
    }

    public ImmutableArray<BoundPivotKeyPart> Parts { get; }
    public string CanonicalIdentity { get; }

    public static BoundPivotTypedKey Create(IEnumerable<object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return new BoundPivotTypedKey(values.Select(BoundPivotKeyPart.Create).ToImmutableArray());
    }

    public object?[] SqlValues()
        => Parts.Select(part => part.SqlValue is byte[] bytes ? bytes.ToArray() : part.SqlValue)
            .ToArray();

    public bool Equals(BoundPivotTypedKey? other)
        => other is not null
            && string.Equals(CanonicalIdentity, other.CanonicalIdentity, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is BoundPivotTypedKey other && Equals(other);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(CanonicalIdentity);
    public override string ToString() => CanonicalIdentity;

    private static string BuildIdentity(ImmutableArray<BoundPivotKeyPart> parts)
    {
        var result = new StringBuilder();
        foreach (var part in parts)
        {
            result.Append(part.TypeTag.Length)
                .Append(':')
                .Append(part.TypeTag)
                .Append(part.CanonicalValue.Length)
                .Append(':')
                .Append(part.CanonicalValue);
        }
        return result.ToString();
    }
}

internal sealed record BoundPivotKeyPart(
    string TypeTag,
    string CanonicalValue,
    object? SqlValue)
{
    public static BoundPivotKeyPart Create(object? value)
        => value switch
        {
            null => new("null", "", null),
            string text => new("text", text, text),
            bool flag => new("bool", flag ? "1" : "0", flag),
            DateTime date => new(
                "datetime",
                date.ToString("O", CultureInfo.InvariantCulture),
                date),
            DateTimeOffset offset => new(
                "datetime-offset",
                offset.ToString("O", CultureInfo.InvariantCulture),
                offset),
            DateOnly date => new(
                "date",
                date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                date),
            TimeOnly time => new(
                "time",
                time.ToString("O", CultureInfo.InvariantCulture),
                time),
            decimal number => new(
                "decimal",
                number.ToString("G29", CultureInfo.InvariantCulture),
                number),
            double number => new(
                "double",
                number.ToString("R", CultureInfo.InvariantCulture),
                number),
            float number => new(
                "single",
                number.ToString("R", CultureInfo.InvariantCulture),
                number),
            Guid guid => new("guid", guid.ToString("D"), guid),
            byte[] bytes => new("binary", Convert.ToBase64String(bytes), bytes.ToArray()),
            IFormattable formattable => new(
                value.GetType().FullName ?? value.GetType().Name,
                formattable.ToString(null, CultureInfo.InvariantCulture) ?? "",
                value),
            _ => new(
                value.GetType().FullName ?? value.GetType().Name,
                value.ToString() ?? "",
                value),
        };
}
