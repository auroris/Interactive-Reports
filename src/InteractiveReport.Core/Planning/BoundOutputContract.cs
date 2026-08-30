using System.Collections.Immutable;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Schema;

namespace InteractiveReport.Core.Planning;

/// <summary>
/// The immutable, ordered public contract produced by one relational node. Logical
/// identity, display metadata, and scalar-format lineage travel together so binding
/// never has to coordinate parallel mutable dictionaries.
/// </summary>
internal sealed class BoundOutputContract
{
    private readonly ImmutableDictionary<string, int> _ordinals;

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

    public string Name { get; }

    /// <summary>The sole authority for public column order.</summary>
    public ImmutableArray<BoundColumnContract> Columns { get; }

    public int Count => Columns.Length;

    public BoundColumnContract this[int index] => Columns[index];

    public static BoundOutputContract Create(
        string name,
        IEnumerable<BoundColumnContract> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        return new BoundOutputContract(name, columns.ToImmutableArray());
    }

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

    public BoundColumnContract GetRequired(string logicalId)
        => TryGetValue(logicalId, out var column)
            ? column
            : throw new KeyNotFoundException(
                $"Output contract '{Name}' contains no logical column '{logicalId}'.");

    public BoundOutputContract Append(BoundColumnContract column)
        => Create(Name, Columns.Add(column));

    public BoundOutputContract Rename(string name)
        => string.Equals(Name, name, StringComparison.Ordinal)
            ? this
            : new BoundOutputContract(name, Columns);

    public BoundOutputContract WithColumns(
        string name,
        IEnumerable<BoundColumnContract> columns)
        => Create(name, columns);

    /// <summary>
    /// Applies structural labels and the declaring table's presentation formats. A
    /// clear reverts labels to intrinsic defaults and removes both masks and their
    /// lineage. Unknown metadata remains document data but cannot enter this contract.
    /// </summary>
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
    /// Creates the contract visible through a <c>from</c> edge. Labels and scalar
    /// masks cross the boundary; styles, renderers, actions, and their dependencies do
    /// not. The original contract is never mutated.
    /// </summary>
    public BoundOutputContract ForChild(string? name = null)
        => Create(
            name ?? Name,
            Columns.Select(column => column with
            {
                LocalFormat = column.ExportedMask is null
                    ? null
                    : CanonicalFormats.MaskOnly(column.ExportedMask),
            }));

    public ReportSchema ToReportSchema()
        => ReportSchema.Create(Name, Columns.Select(column => column.ToColumnModel()));
}

/// <summary>One immutable logical column and its public presentation contract.</summary>
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
    internal bool HasKnownType { get; init; } = true;

    public ColumnKind Kind => ColumnModel.GetKind(ClrType);

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
    /// Existing expression and SQL emitters consume ColumnModel. The adapter is
    /// created from immutable values and never exposes report-document state.
    /// </summary>
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

internal abstract record BoundColumnLineage;

internal sealed record BoundSourceColumnLineage(string SourceLogicalId)
    : BoundColumnLineage;

internal sealed record BoundPassThroughColumnLineage(string InputLogicalId)
    : BoundColumnLineage;

internal sealed record BoundComputedColumnLineage(ImmutableArray<string> InputLogicalIds)
    : BoundColumnLineage;

internal sealed record BoundAggregateColumnLineage(
    AggregateFn Function,
    string? InputLogicalId)
    : BoundColumnLineage;

internal sealed record BoundChartColumnLineage(
    string Role,
    string? InputLogicalId,
    AggregateFn? Function)
    : BoundColumnLineage;

internal sealed record BoundPivotCellColumnLineage(
    string OwnerTableId,
    string MetricId,
    BoundPivotTypedKey Key)
    : BoundColumnLineage;

internal static class CanonicalFormats
{
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
