using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace InteractiveReport.Core.Planning;

/// <summary>
/// Assigns a stable public logical id to a data-derived pivot cell. The identity is
/// derived from semantic inputs rather than discovery order, so adding or removing a
/// different key cannot rename an existing cell. Nothing from this registry is stored
/// in the report document.
/// </summary>
internal sealed class DynamicPivotColumnIdentityRegistry
{
    private readonly HashSet<string> _reserved;
    private readonly Dictionary<DynamicPivotColumnIdentity, string> _assigned = [];
    private readonly Dictionary<string, DynamicPivotColumnIdentity> _owners =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Creates a registry that will not assign any identifier already reserved by the report document.
    /// </summary>
    /// <param name="reservedLogicalIds">The authored and static logical ids that dynamic pivot cells may not use.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="reservedLogicalIds"/> is <see langword="null"/>.</exception>
    public DynamicPivotColumnIdentityRegistry(IEnumerable<string> reservedLogicalIds)
    {
        ArgumentNullException.ThrowIfNull(reservedLogicalIds);
        _reserved = new HashSet<string>(reservedLogicalIds, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Registers a pivot cell from its provider values and returns its deterministic public id.
    /// </summary>
    /// <param name="owningTableId">The identifier of the table that owns the local operation.</param>
    /// <param name="metricId">The stable synthetic identifier of the pivot or chart metric.</param>
    /// <param name="typedPivotKey">The typed pivot key whose canonical identity is being derived.</param>
    /// <returns>The stable logical identifier assigned to the pivot column.</returns>
    public string Register(
        string owningTableId,
        string metricId,
        IReadOnlyList<object?> typedPivotKey)
        => Register(
            owningTableId,
            metricId,
            BoundPivotTypedKey.Create(typedPivotKey));

    /// <summary>
    /// Registers a pivot cell from its canonical typed key and returns its deterministic public id.
    /// </summary>
    /// <param name="owningTableId">The identifier of the table that owns the local operation.</param>
    /// <param name="metricId">The stable synthetic identifier of the pivot or chart metric.</param>
    /// <param name="typedPivotKey">The typed pivot key whose canonical identity is being derived.</param>
    /// <returns>The stable logical identifier assigned to the pivot column.</returns>
    /// <remarks>Reserves newly assigned ids and memoizes repeated semantic identities.</remarks>
    /// <exception cref="ArgumentException">Thrown when <paramref name="owningTableId"/> or <paramref name="metricId"/> is blank.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="typedPivotKey"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when two distinct dynamic identities produce the same digest.</exception>
    public string Register(
        string owningTableId,
        string metricId,
        BoundPivotTypedKey typedPivotKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owningTableId);
        ArgumentException.ThrowIfNullOrWhiteSpace(metricId);
        ArgumentNullException.ThrowIfNull(typedPivotKey);

        var identity = new DynamicPivotColumnIdentity(
            owningTableId,
            metricId,
            typedPivotKey.CanonicalIdentity);
        if (_assigned.TryGetValue(identity, out var existing)) return existing;

        // A positive 63-bit digest remains a compact valid irN identifier while making
        // accidental clashes with authored sequential ids or another semantic Pivot identity
        // negligible. A clash with an authored id is resolved by a deterministic salt; a digest
        // collision between two dynamic identities is rejected rather than made discovery-order
        // dependent.
        for (var salt = 0; ; salt++)
        {
            var candidate = Candidate(identity, salt);
            if (_owners.TryGetValue(candidate, out var owner) && owner != identity)
                throw new InvalidOperationException(
                    $"Dynamic Pivot identities '{owner}' and '{identity}' produced the same public id.");
            if (_reserved.Contains(candidate)) continue;

            _reserved.Add(candidate);
            _owners[candidate] = identity;
            _assigned[identity] = candidate;
            return candidate;
        }
    }

    /// <summary>
    /// Builds a candidate generated-column identifier from a pivot key.
    /// </summary>
    /// <param name="identity">The semantic pivot-cell identity to hash.</param>
    /// <param name="salt">The deterministic salt used to disambiguate a generated identifier.</param>
    /// <returns>A candidate pivot-column identifier derived from the key.</returns>
    private static string Candidate(DynamicPivotColumnIdentity identity, int salt)
    {
        var input = string.Create(
            CultureInfo.InvariantCulture,
            $"{identity.OwningTableId.Length}:{identity.OwningTableId}"
            + $"{identity.MetricId.Length}:{identity.MetricId}"
            + $"{identity.CanonicalTypedKey.Length}:{identity.CanonicalTypedKey}"
            + $"{salt}");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var number = BinaryPrimitives.ReadUInt64BigEndian(hash) & long.MaxValue;
        if (number == 0) number = 1;
        return $"ir{number.ToString(CultureInfo.InvariantCulture)}";
    }

}

/// <summary>Contains the semantic inputs from which a dynamic pivot column id is derived.</summary>
/// <param name="OwningTableId">The table that owns the pivot.</param>
/// <param name="MetricId">The stable pivot metric id.</param>
/// <param name="CanonicalTypedKey">The pivot key's type-sensitive canonical representation.</param>
internal sealed record DynamicPivotColumnIdentity(
    string OwningTableId,
    string MetricId,
    string CanonicalTypedKey);
