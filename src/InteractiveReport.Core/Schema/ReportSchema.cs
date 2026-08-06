using System.Collections;
using System.Collections.Frozen;
using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Schema;

/// <summary>
/// An ordered report schema with one canonical, case-insensitive column lookup.
/// Construction rejects ambiguous aliases before state validation or SQL composition.
/// </summary>
public sealed class ReportSchema : IReadOnlyList<ColumnModel>
{
    private readonly IReadOnlyList<ColumnModel> _columns;
    private readonly IReadOnlyDictionary<string, ColumnModel> _byName;

    private ReportSchema(IReadOnlyList<ColumnModel> columns)
    {
        _columns = Array.AsReadOnly(columns.ToArray());
        _byName = columns.ToFrozenDictionary(column => column.Name, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<ColumnModel> Columns => _columns;
    public IReadOnlyDictionary<string, ColumnModel> Lookup => _byName;
    public int Count => _columns.Count;
    public ColumnModel this[int index] => _columns[index];

    public static ReportSchema Create(string reportName, IEnumerable<ColumnModel> columns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportName);
        ArgumentNullException.ThrowIfNull(columns);

        var ordered = columns.ToArray();
        if (ordered.Length == 0)
            throw new InvalidOperationException($"Report '{reportName}': base query returned no columns.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in ordered)
        {
            if (string.IsNullOrWhiteSpace(column.Name))
                throw new InvalidOperationException($"Report '{reportName}': schema contains an unnamed column.");
            if (!seen.Add(column.Name))
                throw new InvalidOperationException(
                    $"Report '{reportName}': base query returns duplicate column alias '{column.Name}' (aliases are case-insensitive).");
        }

        return new ReportSchema(ordered);
    }

    /// <summary>Returns a new schema with additional columns, preserving base-column order.</summary>
    public ReportSchema Extend(string reportName, IEnumerable<ColumnModel> columns)
        => Create(reportName, _columns.Concat(columns));

    public bool TryGetValue(string key, out ColumnModel value)
        => _byName.TryGetValue(key, out value!);

    public IEnumerator<ColumnModel> GetEnumerator() => _columns.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

}
