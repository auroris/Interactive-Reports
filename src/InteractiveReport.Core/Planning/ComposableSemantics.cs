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
/// Natural binding order for composables. The report document may list them in any order;
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

    /// <summary>Exported relation: the operation changes the row relation inherited by a child table.</summary>
    ExportedRelation = 1 << 0,

    /// <summary>Exported schema: the operation changes the public column contract inherited by a child.</summary>
    ExportedSchema = 1 << 1,

    /// <summary>Exported metadata: the operation changes descriptive metadata inherited by a child.</summary>
    ExportedMetadata = 1 << 2,

    /// <summary>Table local: the operation contributes only to the declaring table's result.</summary>
    TableLocal = 1 << 3,
}

/// <summary>Specifies how declarations of the same semantic category combine.</summary>
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

/// <summary>
/// Declares when members of one composable category have observable precedence.
/// Most categories are sets, overlays, or singletons. Highlight rows apply before
/// highlight cells, then ascending sequence determines precedence within each scope.
/// </summary>
internal enum ComposableOrderingHint
{
    None,
    ScopeThenSequenceAscending,
}

/// <summary>Describes the canonical phase, propagation, merge, and ordering behavior of one composable kind.</summary>
/// <param name="Kind">The internal canonical kind.</param>
/// <param name="DocumentKind">The case-insensitive token used by report-state JSON.</param>
/// <param name="Phase">The phase in which the planner binds the operation.</param>
/// <param name="Effect">The relation, schema, metadata, or table-local surfaces changed by the operation.</param>
/// <param name="Merge">How multiple declarations of the same category combine.</param>
/// <param name="OrderingHint">Any observable member precedence within the category.</param>
internal sealed record ComposableSemantics(
    ComposableKind Kind,
    string DocumentKind,
    ComposablePhase Phase,
    ComposableEffect Effect,
    ComposableMerge Merge,
    ComposableOrderingHint OrderingHint = ComposableOrderingHint.None)
{
    /// <summary>Gets whether any part of the operation crosses a child table's <c>from</c> edge.</summary>
    public bool IsInherited => (Effect & (
        ComposableEffect.ExportedRelation
        | ComposableEffect.ExportedSchema
        | ComposableEffect.ExportedMetadata)) != 0;

    /// <summary>Gets whether the operation contributes to the declaring table's terminal result.</summary>
    public bool IsTableLocal => Effect.HasFlag(ComposableEffect.TableLocal);
}

/// <summary>
/// Provides exhaustive semantics for report composables. Adding document syntax requires an
/// entry here, which keeps propagation and ordering decisions out of the DTO.
/// </summary>
internal static class ComposableSemanticsCatalog
{
    private static readonly IReadOnlyDictionary<string, ComposableSemantics> ByDocumentKind =
        Create().ToDictionary(value => value.DocumentKind, StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<ComposableKind, ComposableSemantics> ByKind =
        ByDocumentKind.Values.ToDictionary(value => value.Kind);

    /// <summary>Gets all registered semantics in canonical kind order.</summary>
    public static IReadOnlyCollection<ComposableSemantics> All { get; } = ByKind.Values.ToArray();

    /// <summary>
    /// Attempts to resolve a document kind to its canonical composable semantics.
    /// </summary>
    /// <param name="documentKind">The authored composable token, with surrounding whitespace permitted.</param>
    /// <param name="semantics">Receives the canonical semantics when recognized.</param>
    /// <returns><see langword="true"/> when the document kind names a supported composable and its semantics were returned; otherwise, <see langword="false"/>.</returns>
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

    /// <summary>
    /// Returns the registered semantics for a canonical composable kind.
    /// </summary>
    /// <param name="kind">The canonical composable kind to resolve.</param>
    /// <returns>The composable semantics.</returns>
    public static ComposableSemantics Get(ComposableKind kind) => ByKind[kind];

    /// <summary>
    /// Defines the closed composable vocabulary, phases, effects, and merge rules used by the planner.
    /// </summary>
    /// <returns>All supported composable semantics in protocol order.</returns>
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
            ComposableMerge.PrioritySet,
            ComposableOrderingHint.ScopeThenSequenceAscending),
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
