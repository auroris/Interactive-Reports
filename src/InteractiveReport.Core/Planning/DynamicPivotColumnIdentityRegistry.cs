using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace InteractiveReport.Core.Planning;

/// <summary>
/// Assigns a stable public logical id to a data-derived Pivot cell. The identity is
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

    public DynamicPivotColumnIdentityRegistry(IEnumerable<string> reservedLogicalIds)
    {
        ArgumentNullException.ThrowIfNull(reservedLogicalIds);
        _reserved = new HashSet<string>(reservedLogicalIds, StringComparer.OrdinalIgnoreCase);
    }

    public string Register(
        string owningTableId,
        string metricId,
        IReadOnlyList<object?> typedPivotKey)
        => Register(
            owningTableId,
            metricId,
            BoundPivotTypedKey.Create(typedPivotKey));

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
        // accidental clashes with authored sequential ids or another semantic Pivot
        // identity negligible. A clash with an authored id is resolved by a
        // deterministic salt; a digest collision between two dynamic identities is
        // rejected rather than made discovery-order dependent.
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

internal sealed record DynamicPivotColumnIdentity(
    string OwningTableId,
    string MetricId,
    string CanonicalTypedKey);
