using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using InteractiveReport.Core.Expressions;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Schema;
using InteractiveReport.Core.Validation;

namespace InteractiveReport.Core.Planning;

/// <summary>
/// Defines immutable relational syntax after schema binding. Every node declares its exact
/// public output contract; SQLKata does not appear in this model.
/// </summary>
internal abstract record BoundRelationNode
{
    /// <summary>
    /// Initializes the common source path and public output contract for a bound relation node.
    /// </summary>
    /// <param name="sourcePath">The document path used to locate validation diagnostics.</param>
    /// <param name="output">The exact logical columns exposed by the node.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="sourcePath"/> is blank.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="output"/> is <see langword="null"/>.</exception>
    protected BoundRelationNode(
        string sourcePath,
        BoundOutputContract output)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(output);
        SourcePath = sourcePath;
        Output = output;
    }

    /// <summary>Gets the authored document path responsible for this relation node.</summary>
    public string SourcePath { get; }
    /// <summary>Gets the exact immutable logical columns exposed by this node.</summary>
    public BoundOutputContract Output { get; }
    /// <summary>Gets a detached mutable schema projection of <see cref="Output"/>.</summary>
    public ReportSchema Schema => Output.ToReportSchema();
}

/// <summary>Represents the trusted configured SELECT; its SQL text remains opaque to logical planning.</summary>
internal sealed record BoundOpaqueSqlSource : BoundRelationNode
{
    /// <summary>
    /// Creates a leaf relation for the trusted SQL and discovered output schema of a report definition.
    /// </summary>
    /// <param name="definitionName">The report definition that owns the SQL source.</param>
    /// <param name="sql">The trusted configured SELECT statement.</param>
    /// <param name="dialect">The database dialect whose SQL rules apply.</param>
    /// <param name="output">The discovered logical output contract for the SQL source.</param>
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

    /// <summary>Gets the canonical report definition name.</summary>
    public string DefinitionName { get; }
    /// <summary>Gets the trusted configured SELECT text.</summary>
    public string Sql { get; }
    /// <summary>Gets the resolved dialect of the source connection.</summary>
    public ReportDialect Dialect { get; }
}

/// <summary>
/// Represents the root of a child table. It imports only the named parent's completed export; the
/// parent's local-result package is structurally unreachable from this node.
/// </summary>
internal sealed record BoundExportReference : BoundRelationNode
{
    /// <summary>
    /// Creates a relation that imports another table's completed, child-visible export.
    /// </summary>
    /// <param name="tableId">The case-insensitive identifier of the referenced parent table.</param>
    /// <param name="target">The completed export of the referenced table.</param>
    /// <param name="sourcePath">The document path used to locate validation diagnostics.</param>
    /// <param name="outputName">An optional name for the imported output contract.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="tableId"/> is blank.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="target"/> is <see langword="null"/>.</exception>
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

    /// <summary>Gets the referenced parent table identifier.</summary>
    public string TableId { get; }
    /// <summary>Gets the parent's completed immutable export package.</summary>
    public BoundTableExport Target { get; }
}

/// <summary>Represents one computed projection appended to an input relation.</summary>
internal sealed record BoundComputeRelation : BoundRelationNode
{
    /// <summary>
    /// Creates a relation that appends one bound computed column to its input contract.
    /// </summary>
    /// <param name="input">The relation evaluated before the computation.</param>
    /// <param name="column">The bound computed-column definition.</param>
    /// <param name="output">The logical contract after adding the computed column.</param>
    /// <param name="sourcePath">The document path used to locate validation diagnostics.</param>
    /// <exception cref="ArgumentNullException">Thrown when a required argument is <see langword="null"/>.</exception>
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

    /// <summary>Gets the relation evaluated before the computation.</summary>
    public BoundRelationNode Input { get; }
    /// <summary>Gets the bound expression and synthetic output column.</summary>
    public BoundComputedColumn Column { get; }
}

/// <summary>Represents one or more row predicates that preserve the input column contract.</summary>
internal sealed record BoundFilterRelation : BoundRelationNode
{
    /// <summary>
    /// Creates a relation that applies bound predicates without changing its input columns.
    /// </summary>
    /// <param name="input">The relation whose rows are filtered.</param>
    /// <param name="predicates">The non-empty set of bound filter predicates.</param>
    /// <param name="output">The logical output contract preserved by the filter.</param>
    /// <param name="sourcePath">The document path used to locate validation diagnostics.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="predicates"/> is default or empty.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="input"/> is <see langword="null"/>.</exception>
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

    /// <summary>Gets the relation whose rows are filtered.</summary>
    public BoundRelationNode Input { get; }
    /// <summary>Gets the non-empty bound predicates applied together.</summary>
    public ImmutableArray<BoundRowPredicate> Predicates { get; }
}

/// <summary>Represents a grouped relation containing dimensions, metrics, and an implicit row count.</summary>
internal sealed record BoundGroupRelation : BoundRelationNode
{
    /// <summary>
    /// Creates a relation that groups its input into dimensions, aggregates, and a count column.
    /// </summary>
    /// <param name="input">The relation whose rows are grouped.</param>
    /// <param name="dimensions">The ordered dimensions that identify grouping or pivot rows.</param>
    /// <param name="metrics">The pivot metric definitions to aggregate.</param>
    /// <param name="countColumn">The generated row-count column contract.</param>
    /// <param name="output">The logical output contract of the grouped relation.</param>
    /// <param name="sourcePath">The document path used to locate validation diagnostics.</param>
    /// <exception cref="ArgumentException">Thrown when dimensions or metrics are uninitialized immutable arrays.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="input"/> or <paramref name="countColumn"/> is <see langword="null"/>.</exception>
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

    /// <summary>Gets the relation whose rows are grouped.</summary>
    public BoundRelationNode Input { get; }
    /// <summary>Gets grouping columns in public order.</summary>
    public ImmutableArray<BoundColumnContract> Dimensions { get; }
    /// <summary>Gets explicit aggregate metrics in authored order.</summary>
    public ImmutableArray<BoundMetric> Metrics { get; }
    /// <summary>Gets the stable synthetic <c>COUNT(*)</c> output column.</summary>
    public BoundColumnContract CountColumn { get; }
}

/// <summary>Represents a validated chart relation projected as category and numeric value columns.</summary>
internal sealed record BoundChartRelation : BoundRelationNode
{
    /// <summary>
    /// Creates a terminal chart relation with its validated role projections.
    /// </summary>
    /// <param name="input">The relation supplying chart data.</param>
    /// <param name="chart">The validated chart definition.</param>
    /// <param name="output">The chart's logical output contract.</param>
    /// <param name="sourcePath">The document path used to locate validation diagnostics.</param>
    /// <exception cref="ArgumentNullException">Thrown when a required argument is <see langword="null"/>.</exception>
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

    /// <summary>Gets the relation supplying chart input rows.</summary>
    public BoundRelationNode Input { get; }
    /// <summary>Gets the validated chart roles, aggregation, and ordering.</summary>
    public ValidChart Chart { get; }
}

/// <summary>
/// Represents a pivot whose data-dependent keys and public columns have already been registered.
/// Before registration, orchestration carries a separate pending continuation rather
/// than pretending that this node has a knowable output contract.
/// </summary>
internal sealed record BoundResolvedPivotRelation : BoundRelationNode
{
    /// <summary>
    /// Creates a pivot relation after discovery has resolved its data-dependent keys and output columns.
    /// </summary>
    /// <param name="discovery">The bound relation used to discover distinct pivot keys.</param>
    /// <param name="rowDimensions">The ordered dimensions that identify grouping or pivot rows.</param>
    /// <param name="columnDimensions">The ordered pivot dimensions that identify output columns.</param>
    /// <param name="metrics">The pivot metric definitions to aggregate.</param>
    /// <param name="keys">The canonical pivot keys that identify output cells.</param>
    /// <param name="output">The resolved logical output contract of the pivot.</param>
    /// <param name="sourcePath">The document path used to locate validation diagnostics.</param>
    /// <exception cref="ArgumentException">Thrown when any immutable-array input is uninitialized.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="discovery"/> is <see langword="null"/>.</exception>
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

    /// <summary>Gets the grouped relation used to discover distinct pivot keys.</summary>
    public BoundGroupRelation Discovery { get; }
    /// <summary>Gets columns that remain as leading rows in the wide output.</summary>
    public ImmutableArray<BoundColumnContract> RowDimensions { get; }
    /// <summary>Gets columns whose distinct value tuples identify dynamic output groups.</summary>
    public ImmutableArray<BoundColumnContract> ColumnDimensions { get; }
    /// <summary>Gets aggregate metrics repeated for each dynamic key.</summary>
    public ImmutableArray<BoundMetric> Metrics { get; }
    /// <summary>Gets resolved typed keys and their registered cell columns.</summary>
    public ImmutableArray<BoundResolvedPivotKey> Keys { get; }
}

/// <summary>Represents a contract-only presentation transformation; lowering emits no SQL stage.</summary>
internal sealed record BoundMetadataRelation : BoundRelationNode
{
    /// <summary>
    /// Creates a contract-only relation that changes presentation metadata without adding a SQL stage.
    /// </summary>
    /// <param name="input">The relation whose rows and physical columns are preserved.</param>
    /// <param name="output">The logical output contract after applying metadata.</param>
    /// <param name="sourcePath">The document path used to locate validation diagnostics.</param>
    /// <exception cref="ArgumentNullException">Thrown when a required argument is <see langword="null"/>.</exception>
    public BoundMetadataRelation(
        BoundRelationNode input,
        BoundOutputContract output,
        string sourcePath)
        : base(sourcePath, output)
    {
        ArgumentNullException.ThrowIfNull(input);
        Input = input;
    }

    /// <summary>Gets the relation whose rows and physical projections are unchanged.</summary>
    public BoundRelationNode Input { get; }
}

/// <summary>Represents a request-local toolbar-search restriction over the completed active relation.</summary>
internal sealed record BoundSearchRelation : BoundRelationNode
{
    /// <summary>
    /// Creates a request-local search relation over the completed active table.
    /// </summary>
    /// <param name="input">The relation whose searchable rows are restricted.</param>
    /// <param name="search">The non-empty search text to apply.</param>
    /// <param name="output">The logical output contract preserved by search.</param>
    /// <param name="sourcePath">The document path used to locate validation diagnostics; defaults to <c>"search"</c>.</param>
    /// <exception cref="ArgumentNullException">Thrown when a required argument is <see langword="null"/>.</exception>
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

    /// <summary>Gets the completed active relation to search.</summary>
    public BoundRelationNode Input { get; }
    /// <summary>Gets trimmed, nonblank search text.</summary>
    public string Search { get; }
}

/// <summary>Contains a bound value expression, its synthetic output contract, and authored source path.</summary>
internal sealed record BoundComputedColumn(
    BoundExpression Expression,
    BoundColumnContract Output,
    string SourcePath);

/// <summary>Contains one bound Boolean row predicate and its authored source path.</summary>
internal sealed record BoundRowPredicate(
    BoundExpression Expression,
    string SourcePath);

/// <summary>Contains one stable aggregate id, bound input column, function, and authored source path.</summary>
internal sealed record BoundMetric(
    string Id,
    BoundColumnContract Input,
    AggregateFn Function,
    string SourcePath);

/// <summary>Associates one typed dynamic pivot key with its registered metric cell columns.</summary>
internal sealed record BoundResolvedPivotKey(
    BoundPivotTypedKey Key,
    ImmutableArray<BoundPivotCell> Cells);

/// <summary>Maps a pivot metric's source logical id to one public dynamic output column.</summary>
internal sealed record BoundPivotCell(
    string SourceLogicalId,
    BoundColumnContract Output);

/// <summary>
/// Contains the completed inheritable package for one named table. Output is already reduced
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
    /// <summary>
    /// Builds the child-visible export package for a compiled table relation.
    /// </summary>
    /// <param name="tableId">The owning table's case-insensitive document identifier.</param>
    /// <param name="relation">The completed bound relation exported by the table.</param>
    /// <param name="shapeCount">The number of terminal shape operations owned by the table.</param>
    /// <param name="computedRuleCount">The number of computed-column rules owned by the table.</param>
    /// <param name="filterRuleCount">The number of filter rules owned by the table.</param>
    /// <returns>An export that retains the full relation tree but reduces its output metadata to the child-visible channel.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="tableId"/> is blank.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="relation"/> is <see langword="null"/>.</exception>
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
/// Represents one canonical, type-tagged pivot key. Equality uses the length-framed canonical
/// identity, never provider object identity or a display string.
/// </summary>
internal sealed class BoundPivotTypedKey : IEquatable<BoundPivotTypedKey>
{
    /// <summary>
    /// Creates a typed pivot key from its ordered, canonicalized parts.
    /// </summary>
    /// <param name="parts">The ordered typed values that identify one pivot column.</param>
    private BoundPivotTypedKey(ImmutableArray<BoundPivotKeyPart> parts)
    {
        Parts = parts;
        CanonicalIdentity = BuildIdentity(parts);
    }

    /// <summary>Gets the ordered immutable typed parts.</summary>
    public ImmutableArray<BoundPivotKeyPart> Parts { get; }
    /// <summary>Gets the collision-resistant, length-framed identity used for equality and column registration.</summary>
    public string CanonicalIdentity { get; }

    /// <summary>
    /// Converts provider values into a typed pivot key with stable cross-provider identity.
    /// </summary>
    /// <param name="values">The ordered provider values that form the pivot key.</param>
    /// <returns>A key retaining copied SQL values and stable cross-provider canonical identity.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="values"/> is <see langword="null"/>.</exception>
    public static BoundPivotTypedKey Create(IEnumerable<object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return new BoundPivotTypedKey(values.Select(BoundPivotKeyPart.Create).ToImmutableArray());
    }

    /// <summary>
    /// Returns the original provider values used when binding pivot SQL parameters.
    /// </summary>
    /// <returns>The ordered provider values retained by the key parts.</returns>
    public object?[] SqlValues()
        => Parts.Select(part => part.SqlValue is byte[] bytes ? bytes.ToArray() : part.SqlValue)
            .ToArray();

    /// <summary>
    /// Determines whether another typed key has the same canonical identity.
    /// </summary>
    /// <param name="other">The other typed key to compare.</param>
    /// <returns><see langword="true"/> when the compared values are equivalent; otherwise, <see langword="false"/>.</returns>
    public bool Equals(BoundPivotTypedKey? other)
        => other is not null
            && string.Equals(CanonicalIdentity, other.CanonicalIdentity, StringComparison.Ordinal);

    /// <summary>
    /// Determines whether an object is a typed key with the same canonical identity.
    /// </summary>
    /// <param name="obj">The object to compare with this key.</param>
    /// <returns><see langword="true"/> when the compared values are equivalent; otherwise, <see langword="false"/>.</returns>
    public override bool Equals(object? obj) => obj is BoundPivotTypedKey other && Equals(other);
    /// <summary>
    /// Returns the ordinal hash code of the canonical key identity.
    /// </summary>
    /// <returns>A hash code consistent with typed-key equality.</returns>
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(CanonicalIdentity);
    /// <summary>
    /// Returns the canonical identity used in diagnostics and generated column identifiers.
    /// </summary>
    /// <returns><see cref="CanonicalIdentity"/>.</returns>
    public override string ToString() => CanonicalIdentity;

    /// <summary>
    /// Builds a collision-resistant, length-framed identity from ordered key parts.
    /// </summary>
    /// <param name="parts">The ordered typed key parts to encode.</param>
    /// <returns>The concatenated type/value frames used for ordinal equality.</returns>
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

/// <summary>Contains one pivot-key type tag, canonical value, and retained provider value for SQL binding.</summary>
internal sealed record BoundPivotKeyPart(
    string TypeTag,
    string CanonicalValue,
    object? SqlValue)
{
    /// <summary>
    /// Converts one provider value into a type-tagged canonical pivot-key part.
    /// </summary>
    /// <param name="value">The provider value to canonicalize while retaining for SQL binding.</param>
    /// <returns>A key part with a stable type tag and invariant canonical value; byte arrays are defensively copied.</returns>
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
            Guid guid => new("guid", guid.ToString("D"), guid),
            byte[] bytes => new("binary", Convert.ToBase64String(bytes), bytes.ToArray()),
            _ when IsNumeric(value) => new(
                "number",
                CanonicalNumber(value),
                value),
            IFormattable formattable => new(
                value.GetType().FullName ?? value.GetType().Name,
                formattable.ToString(null, CultureInfo.InvariantCulture) ?? "",
                value),
            _ => new(
                value.GetType().FullName ?? value.GetType().Name,
                value.ToString() ?? "",
                value),
        };

    /// <summary>
    /// Determines whether a provider value uses one of the supported numeric CLR types.
    /// </summary>
    /// <param name="value">The provider value to classify.</param>
    /// <returns><see langword="true"/> for integral, floating-point, or decimal CLR values; otherwise, <see langword="false"/>.</returns>
    private static bool IsNumeric(object value)
        => Type.GetTypeCode(value.GetType()) is
            TypeCode.SByte or TypeCode.Byte
            or TypeCode.Int16 or TypeCode.UInt16
            or TypeCode.Int32 or TypeCode.UInt32
            or TypeCode.Int64 or TypeCode.UInt64
            or TypeCode.Single or TypeCode.Double
            or TypeCode.Decimal;

    /// <summary>
    /// Normalizes numeric values to a coefficient/exponent identity because provider CLR widths are storage details, not pivot-key types.
    /// numeric value to a coefficient/exponent identity so, for example, Int32 1, Int64 1, Decimal 1.0, and
    /// Double 1E0 retain the same public Pivot-cell id. The original value remains in <see cref="SqlValue"/>
    /// for dialect binding.
    /// </summary>
    /// <param name="value">The numeric provider value to normalize.</param>
    /// <returns>The invariant canonical representation of the numeric value.</returns>
    /// <exception cref="InvalidOperationException">Thrown when a value classified as numeric does not implement invariant formatting.</exception>
    private static string CanonicalNumber(object value)
    {
        var text = value switch
        {
            decimal number => number.ToString("G29", CultureInfo.InvariantCulture),
            double number => number.ToString("R", CultureInfo.InvariantCulture),
            float number => number.ToString("R", CultureInfo.InvariantCulture),
            IFormattable number => number.ToString(null, CultureInfo.InvariantCulture) ?? "0",
            _ => throw new InvalidOperationException(
                $"Value '{value}' was classified as numeric but is not formattable."),
        };

        if (string.Equals(text, "NaN", StringComparison.OrdinalIgnoreCase)) return "nan";
        if (string.Equals(text, "Infinity", StringComparison.OrdinalIgnoreCase)) return "+infinity";
        if (string.Equals(text, "+Infinity", StringComparison.OrdinalIgnoreCase)) return "+infinity";
        if (string.Equals(text, "-Infinity", StringComparison.OrdinalIgnoreCase)) return "-infinity";

        var negative = text.StartsWith("-", StringComparison.Ordinal);
        var start = text.StartsWith("+", StringComparison.Ordinal) || negative ? 1 : 0;
        var exponentIndex = text.IndexOfAny(['e', 'E'], start);
        var mantissaEnd = exponentIndex < 0 ? text.Length : exponentIndex;
        var exponent = exponentIndex < 0
            ? 0
            : int.Parse(text.AsSpan(exponentIndex + 1), CultureInfo.InvariantCulture);
        var decimalIndex = text.IndexOf('.', start, mantissaEnd - start);
        var fractionalDigits = decimalIndex < 0 ? 0 : mantissaEnd - decimalIndex - 1;

        var digits = new StringBuilder(mantissaEnd - start);
        for (var index = start; index < mantissaEnd; index++)
            if (text[index] != '.') digits.Append(text[index]);

        var first = 0;
        while (first < digits.Length && digits[first] == '0') first++;
        if (first == digits.Length) return "0";
        if (first > 0) digits.Remove(0, first);

        var scale = exponent - fractionalDigits;
        while (digits.Length > 1 && digits[^1] == '0')
        {
            digits.Length--;
            scale++;
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{(negative ? "-" : "")}{digits}e{scale}");
    }
}
