namespace InteractiveReport.Core.Planning;

/// <summary>
/// The closed set of operations understood by the report document. These names are
/// syntax; their execution phase and inheritance behavior live in this assembly.
/// </summary>
internal enum ComposableKind
{
    Group,
    Pivot,
    Chart,
    Compute,
    Filter,
    Labels,
    Formats,
    Select,
    Sort,
    Highlight,
    Break,
    Aggregate,
}

/// <summary>
/// Natural binding order. The report document may list composables in any order;
/// lowering consumes the canonical phases in this order instead.
/// </summary>
internal enum ComposablePhase
{
    Shape,
    DerivedColumns,
    RowRestriction,
    Metadata,
    TableLocal,
}

[Flags]
internal enum ComposableEffect
{
    None = 0,

    /// <summary>The operation changes the row relation inherited by a child table.</summary>
    ExportedRelation = 1 << 0,

    /// <summary>The operation changes the public column contract inherited by a child.</summary>
    ExportedSchema = 1 << 1,

    /// <summary>The operation changes descriptive metadata inherited by a child.</summary>
    ExportedMetadata = 1 << 2,

    /// <summary>The operation contributes only to the declaring table's result.</summary>
    TableLocal = 1 << 3,
}

/// <summary>How declarations of the same semantic category combine.</summary>
internal enum ComposableMerge
{
    SingleShape,
    DependencyOrdered,
    Conjunction,
    MetadataOverlay,
    Singleton,
    PrioritySet,
    Set,
}

internal sealed record ComposableSemantics(
    ComposableKind Kind,
    string DocumentKind,
    ComposablePhase Phase,
    ComposableEffect Effect,
    ComposableMerge Merge)
{
    public bool IsInherited => (Effect & (
        ComposableEffect.ExportedRelation
        | ComposableEffect.ExportedSchema
        | ComposableEffect.ExportedMetadata)) != 0;

    public bool IsTableLocal => Effect.HasFlag(ComposableEffect.TableLocal);
}

/// <summary>
/// Exhaustive semantics for report composables. Adding document syntax requires an
/// entry here, which keeps propagation and ordering decisions out of the DTO.
/// </summary>
internal static class ComposableSemanticsCatalog
{
    private static readonly IReadOnlyDictionary<string, ComposableSemantics> ByDocumentKind =
        Create().ToDictionary(value => value.DocumentKind, StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<ComposableKind, ComposableSemantics> ByKind =
        ByDocumentKind.Values.ToDictionary(value => value.Kind);

    public static IReadOnlyCollection<ComposableSemantics> All { get; } = ByKind.Values.ToArray();

    public static bool TryResolve(string? documentKind, out ComposableSemantics semantics)
    {
        var normalized = documentKind?.Trim();
        if (!string.IsNullOrEmpty(normalized)
            && ByDocumentKind.TryGetValue(normalized, out var found))
        {
            semantics = found;
            return true;
        }

        semantics = null!;
        return false;
    }

    public static ComposableSemantics Get(ComposableKind kind) => ByKind[kind];

    private static ComposableSemantics[] Create() =>
    [
        new(
            ComposableKind.Group,
            "group",
            ComposablePhase.Shape,
            ComposableEffect.ExportedRelation | ComposableEffect.ExportedSchema
                | ComposableEffect.TableLocal,
            ComposableMerge.SingleShape),
        new(
            ComposableKind.Pivot,
            "pivot",
            ComposablePhase.Shape,
            ComposableEffect.ExportedRelation | ComposableEffect.ExportedSchema
                | ComposableEffect.TableLocal,
            ComposableMerge.SingleShape),
        new(
            ComposableKind.Chart,
            "chart",
            ComposablePhase.Shape,
            ComposableEffect.ExportedRelation | ComposableEffect.ExportedSchema
                | ComposableEffect.TableLocal,
            ComposableMerge.SingleShape),
        new(
            ComposableKind.Compute,
            "compute",
            ComposablePhase.DerivedColumns,
            ComposableEffect.ExportedRelation | ComposableEffect.ExportedSchema,
            ComposableMerge.DependencyOrdered),
        new(
            ComposableKind.Filter,
            "filter",
            ComposablePhase.RowRestriction,
            ComposableEffect.ExportedRelation,
            ComposableMerge.Conjunction),
        new(
            ComposableKind.Labels,
            "labels",
            ComposablePhase.Metadata,
            ComposableEffect.ExportedMetadata | ComposableEffect.TableLocal,
            ComposableMerge.MetadataOverlay),
        new(
            ComposableKind.Formats,
            "formats",
            ComposablePhase.Metadata,
            ComposableEffect.ExportedMetadata | ComposableEffect.TableLocal,
            ComposableMerge.MetadataOverlay),
        new(
            ComposableKind.Select,
            "select",
            ComposablePhase.TableLocal,
            ComposableEffect.TableLocal,
            ComposableMerge.Singleton),
        new(
            ComposableKind.Sort,
            "sort",
            ComposablePhase.TableLocal,
            ComposableEffect.TableLocal,
            ComposableMerge.Singleton),
        new(
            ComposableKind.Highlight,
            "highlight",
            ComposablePhase.TableLocal,
            ComposableEffect.TableLocal,
            ComposableMerge.PrioritySet),
        new(
            ComposableKind.Break,
            "break",
            ComposablePhase.TableLocal,
            ComposableEffect.TableLocal,
            ComposableMerge.Singleton),
        new(
            ComposableKind.Aggregate,
            "aggregate",
            ComposablePhase.TableLocal,
            ComposableEffect.TableLocal,
            ComposableMerge.Set),
    ];
}
