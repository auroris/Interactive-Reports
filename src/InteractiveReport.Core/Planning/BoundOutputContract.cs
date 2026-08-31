using System.Collections.Immutable;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Schema;

namespace InteractiveReport.Core.Planning;

/// <summary>
/// Represents the immutable, ordered public contract produced by one relational node. Logical
/// identity, display metadata, and scalar-format lineage travel together so binding
/// never has to coordinate parallel mutable dictionaries.
/// </summary>
internal sealed class BoundOutputContract
{
    private readonly ImmutableDictionary<string, int> _ordinals;

    /// <summary>
    /// Validates and initializes one named output contract and its case-insensitive ordinal lookup.
    /// </summary>
    /// <param name="name">The nonblank logical relation name.</param>
    /// <param name="columns">At least one column in public output order.</param>
    /// <exception cref="InvalidOperationException">Thrown for an empty contract, null/unnamed columns, or duplicate logical ids.</exception>
    private BoundOutputContract(
        string name,
        ImmutableArray<BoundColumnContract> columns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (columns.IsDefaultOrEmpty)
            throw new InvalidOperationException(
                $"Bound output contract '{name}' must contain at least one column.");

        var ordinals = ImmutableDictionary.CreateBuilder<string, int>(
            StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < columns.Length; index++)
        {
            var column = columns[index]
                ?? throw new InvalidOperationException(
                    $"Bound output contract '{name}' contains a null column.");
            if (string.IsNullOrWhiteSpace(column.LogicalId))
                throw new InvalidOperationException(
                    $"Bound output contract '{name}' contains an unnamed column.");
            if (!ordinals.TryAdd(column.LogicalId, index))
                throw new InvalidOperationException(
                    $"Bound output contract '{name}' contains duplicate logical id "
                    + $"'{column.LogicalId}' (ids are case-insensitive).");
        }

        Name = name;
        Columns = columns;
        _ordinals = ordinals.ToImmutable();
    }

    /// <summary>Gets the logical relation name associated with this output.</summary>
    public string Name { get; }

    /// <summary>Gets the sole authority for public column order.</summary>
    public ImmutableArray<BoundColumnContract> Columns { get; }

    /// <summary>Gets the number of public output columns.</summary>
    public int Count => Columns.Length;

    /// <summary>Gets the column at a zero-based public output ordinal.</summary>
    public BoundColumnContract this[int index] => Columns[index];

    /// <summary>
    /// Creates an immutable named output contract from ordered logical columns.
    /// </summary>
    /// <param name="name">The logical name of the relation output.</param>
    /// <param name="columns">The columns in public contract order.</param>
    /// <returns>A validated immutable output contract.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="columns"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the materialized columns violate output invariants.</exception>
    public static BoundOutputContract Create(
        string name,
        IEnumerable<BoundColumnContract> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        return new BoundOutputContract(name, columns.ToImmutableArray());
    }

    /// <summary>
    /// Builds an immutable output contract from discovered schema plus label and format overrides.
    /// </summary>
    /// <param name="name">The logical relation name for the new contract.</param>
    /// <param name="schema">The discovered ordered columns and intrinsic labels.</param>
    /// <param name="labels">Optional effective labels keyed case-insensitively by logical column name.</param>
    /// <param name="formats">Optional local formats keyed case-insensitively by logical column name.</param>
    /// <param name="formatSources">Optional logical ids that identify the source of inherited masks.</param>
    /// <returns>A contract whose source lineage begins at the discovered schema.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="schema"/> is <see langword="null"/>.</exception>
    public static BoundOutputContract FromSchema(
        string name,
        ReportSchema schema,
        IReadOnlyDictionary<string, string>? labels = null,
        IReadOnlyDictionary<string, CanonicalColumnFormat>? formats = null,
        IReadOnlyDictionary<string, string?>? formatSources = null)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return Create(name, schema.Columns.Select(column =>
        {
            string? effectiveLabel = null;
            CanonicalColumnFormat? format = null;
            string? formatSource = null;
            labels?.TryGetValue(column.Name, out effectiveLabel);
            formats?.TryGetValue(column.Name, out format);
            formatSources?.TryGetValue(column.Name, out formatSource);
            return BoundColumnContract.FromColumn(
                column,
                new BoundSourceColumnLineage(column.Name),
                effectiveLabel,
                format,
                format?.Mask,
                formatSource);
        }));
    }

    /// <summary>
    /// Attempts to resolve a logical column without throwing when the name is absent.
    /// </summary>
    /// <param name="logicalId">The case-insensitive logical column identifier.</param>
    /// <param name="column">Receives the matching column when found; otherwise receives <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the named column exists and was returned; otherwise, <see langword="false"/>.</returns>
    public bool TryGetValue(string logicalId, out BoundColumnContract column)
    {
        if (_ordinals.TryGetValue(logicalId, out var index))
        {
            column = Columns[index];
            return true;
        }

        column = null!;
        return false;
    }

    /// <summary>
    /// Returns a required logical column or throws when the contract does not expose it.
    /// </summary>
    /// <param name="logicalId">The case-insensitive logical column identifier.</param>
    /// <returns>The bound column contract.</returns>
    /// <exception cref="KeyNotFoundException">Thrown when the requested logical name is absent from the contract.</exception>
    public BoundColumnContract GetRequired(string logicalId)
        => TryGetValue(logicalId, out var column)
            ? column
            : throw new KeyNotFoundException(
                $"Output contract '{Name}' contains no logical column '{logicalId}'.");

    /// <summary>
    /// Appends one column after the existing public output columns.
    /// </summary>
    /// <param name="column">The new immutable column contract.</param>
    /// <returns>A validated contract copy with <paramref name="column"/> last.</returns>
    public BoundOutputContract Append(BoundColumnContract column)
        => Create(Name, Columns.Add(column));

    /// <summary>
    /// Creates a copy of the output contract with one logical column renamed.
    /// </summary>
    /// <param name="name">The replacement logical relation name.</param>
    /// <returns>This instance when the name is ordinally identical; otherwise, a validated copy sharing the immutable columns.</returns>
    public BoundOutputContract Rename(string name)
        => string.Equals(Name, name, StringComparison.Ordinal)
            ? this
            : new BoundOutputContract(name, Columns);

    /// <summary>
    /// Creates an output-contract copy with a replacement ordered column set.
    /// </summary>
    /// <param name="name">The logical relation name for the replacement contract.</param>
    /// <param name="columns">The complete replacement columns in public output order.</param>
    /// <returns>A validated contract independent of this instance.</returns>
    public BoundOutputContract WithColumns(
        string name,
        IEnumerable<BoundColumnContract> columns)
        => Create(name, columns);

    /// <summary>
    /// Applies structural labels and the declaring table's presentation formats. A clear
    /// reverts labels to intrinsic defaults and removes both masks and their lineage. Unknown metadata
    /// remains document data but cannot enter this contract.
    /// </summary>
    /// <param name="metadata">The canonical label/format delta, including explicit inheritance clears.</param>
    /// <returns>A contract copy with metadata applied only to known logical columns.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="metadata"/> is <see langword="null"/>.</exception>
    public BoundOutputContract ApplyMetadata(CanonicalMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var columns = Columns.Select(column =>
        {
            var result = column;
            if (metadata.ClearsInheritedLabels)
                result = result with { EffectiveLabel = result.DefaultLabel };
            if (metadata.Labels.TryGetValue(result.LogicalId, out var label))
                result = result with { EffectiveLabel = label };

            if (metadata.ClearsInheritedFormats)
            {
                result = result with
                {
                    LocalFormat = null,
                    ExportedMask = null,
                    FormatSourceLogicalId = null,
                };
            }
            if (metadata.Formats.TryGetValue(result.LogicalId, out var format))
            {
                result = result with
                {
                    LocalFormat = format,
                    ExportedMask = format.Mask,
                    FormatSourceLogicalId = format.Mask is null
                        ? null
                        : result.LogicalId,
                };
            }
            return result;
        });
        return Create(Name, columns);
    }

    /// <summary>
    /// Creates the contract visible through a <c>from</c> edge. Labels and scalar masks cross the
    /// boundary; styles, renderers, actions, and their dependencies do not. The original contract is never
    /// mutated.
    /// </summary>
    /// <param name="name">An optional child-facing relation name; omission preserves <see cref="Name"/>.</param>
    /// <returns>A contract copy retaining labels, lineage, and exported masks while reducing local formats to mask-only values.</returns>
    public BoundOutputContract ForChild(string? name = null)
        => Create(
            name ?? Name,
            Columns.Select(column => column with
            {
                LocalFormat = column.ExportedMask is null
                    ? null
                    : CanonicalFormats.MaskOnly(column.ExportedMask),
            }));

    /// <summary>
    /// Converts the bound output contract back into the public report-schema model.
    /// </summary>
    /// <returns>A new mutable schema projection in public column order using intrinsic labels.</returns>
    public ReportSchema ToReportSchema()
        => ReportSchema.Create(Name, Columns.Select(column => column.ToColumnModel()));
}

/// <summary>Contains one immutable logical column, public presentation, type, and lineage contract.</summary>
internal sealed record BoundColumnContract(
    string LogicalId,
    string DefaultLabel,
    string EffectiveLabel,
    Type ClrType,
    bool IsNullable,
    bool IsComputed,
    BoundColumnLineage Lineage,
    CanonicalColumnFormat? LocalFormat = null,
    string? ExportedMask = null,
    string? FormatSourceLogicalId = null)
{
    /// <summary>Gets whether provider discovery identified a supported CLR type for the column.</summary>
    internal bool HasKnownType { get; init; } = true;

    /// <summary>Gets the portable column kind derived from <see cref="ClrType"/>.</summary>
    public ColumnKind Kind => ColumnModel.GetKind(ClrType);

    /// <summary>
    /// Creates a bound column contract from discovered schema metadata and optional presentation overrides.
    /// </summary>
    /// <param name="column">The discovered or synthetic column to snapshot.</param>
    /// <param name="lineage">Optional lineage; omission creates source-column lineage from the column name.</param>
    /// <param name="effectiveLabel">Optional inherited or overridden label; omission uses the intrinsic label.</param>
    /// <param name="localFormat">Optional complete format owned by the current table.</param>
    /// <param name="exportedMask">Optional scalar mask allowed to cross a child boundary.</param>
    /// <param name="formatSourceLogicalId">Optional logical column whose scalar value supplied the exported mask.</param>
    /// <returns>An immutable column contract preserving provider type knowledge.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="column"/> is <see langword="null"/>.</exception>
    public static BoundColumnContract FromColumn(
        ColumnModel column,
        BoundColumnLineage? lineage = null,
        string? effectiveLabel = null,
        CanonicalColumnFormat? localFormat = null,
        string? exportedMask = null,
        string? formatSourceLogicalId = null)
    {
        ArgumentNullException.ThrowIfNull(column);
        return new BoundColumnContract(
            column.Name,
            column.Label,
            effectiveLabel ?? column.Label,
            column.ClrType,
            column.IsNullable,
            column.IsComputed,
            lineage ?? new BoundSourceColumnLineage(column.Name),
            localFormat,
            exportedMask,
            formatSourceLogicalId)
        {
            HasKnownType = column.HasKnownType,
        };
    }

    /// <summary>
    /// Creates the mutable <see cref="ColumnModel"/> adapter consumed by expression and SQL emitters.
    /// from immutable values and never exposes report-document state.
    /// </summary>
    /// <param name="useEffectiveLabel">Uses the inherited/overridden label when true; otherwise uses the intrinsic label.</param>
    /// <returns>A detached column model containing logical identity and type flags, but no lineage or formats.</returns>
    public ColumnModel ToColumnModel(bool useEffectiveLabel = false)
        => new()
        {
            Name = LogicalId,
            Label = useEffectiveLabel ? EffectiveLabel : DefaultLabel,
            ClrType = ClrType,
            HasKnownType = HasKnownType,
            IsNullable = IsNullable,
            IsComputed = IsComputed,
        };
}

/// <summary>Identifies how a logical output column derives from earlier relation columns.</summary>
internal abstract record BoundColumnLineage;

/// <summary>Marks a column discovered directly from the configured SQL source.</summary>
internal sealed record BoundSourceColumnLineage(string SourceLogicalId)
    : BoundColumnLineage;

/// <summary>Marks a column forwarded unchanged from one input logical column.</summary>
internal sealed record BoundPassThroughColumnLineage(string InputLogicalId)
    : BoundColumnLineage;

/// <summary>Records every logical input referenced by a computed expression.</summary>
internal sealed record BoundComputedColumnLineage(ImmutableArray<string> InputLogicalIds)
    : BoundColumnLineage;

/// <summary>Records an aggregate function and its optional input; null input represents row count.</summary>
internal sealed record BoundAggregateColumnLineage(
    AggregateFn Function,
    string? InputLogicalId)
    : BoundColumnLineage;

/// <summary>Records whether a chart output is a label or value and how it derives from its input.</summary>
internal sealed record BoundChartColumnLineage(
    string Role,
    string? InputLogicalId,
    AggregateFn? Function)
    : BoundColumnLineage;

/// <summary>Records the owning pivot table, metric, and typed dynamic key for one pivot cell column.</summary>
internal sealed record BoundPivotCellColumnLineage(
    string OwnerTableId,
    string MetricId,
    BoundPivotTypedKey Key)
    : BoundColumnLineage;

/// <summary>Creates immutable format fragments used by output-contract inheritance.</summary>
internal static class CanonicalFormats
{
    /// <summary>
    /// Creates a format containing only the supplied display mask.
    /// </summary>
    /// <param name="mask">The optional display mask to apply.</param>
    /// <returns>A format whose only non-null field is <paramref name="mask"/>.</returns>
    public static CanonicalColumnFormat MaskOnly(string mask)
        => new(
            mask,
            null,
            null,
            null,
            null,
            null,
            [],
            null,
            null,
            null,
            null,
            null);
}
