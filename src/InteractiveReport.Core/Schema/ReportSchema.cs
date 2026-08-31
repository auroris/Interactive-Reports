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

    /// <summary>
    /// Stores an already-validated ordered column set and builds its lookup.
    /// </summary>
    /// <param name="columns">The unique columns in provider order.</param>
    private ReportSchema(IReadOnlyList<ColumnModel> columns)
    {
        _columns = Array.AsReadOnly(columns.ToArray());
        _byName = columns.ToFrozenDictionary(column => column.Name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Gets columns in provider or relation order.</summary>
    public IReadOnlyList<ColumnModel> Columns => _columns;
    /// <summary>Gets the canonical case-insensitive lookup by logical column name.</summary>
    public IReadOnlyDictionary<string, ColumnModel> Lookup => _byName;
    /// <summary>Gets the number of columns.</summary>
    public int Count => _columns.Count;
    /// <summary>Gets the column at the specified schema position.</summary>
    /// <param name="index">The zero-based column index.</param>
    public ColumnModel this[int index] => _columns[index];

    /// <summary>
    /// Creates a report schema and validates that column names are unique case-insensitively.
    /// </summary>
    /// <param name="reportName">The configured report name used to qualify validation errors.</param>
    /// <param name="columns">The discovered columns in provider order.</param>
    /// <returns>The report schema.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="reportName"/> is blank.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="columns"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the schema is empty or contains unnamed or case-insensitively duplicate columns.</exception>
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

    /// <summary>
    /// Returns a new schema with additional columns appended after the current columns.
    /// </summary>
    /// <param name="reportName">The configured report name used to qualify validation errors.</param>
    /// <param name="columns">The columns to append.</param>
    /// <returns>A newly validated schema preserving the current column order.</returns>
    public ReportSchema Extend(string reportName, IEnumerable<ColumnModel> columns)
        => Create(reportName, _columns.Concat(columns));

    /// <summary>
    /// Attempts to resolve a column by its case-insensitive logical name.
    /// </summary>
    /// <param name="key">The case-insensitive logical column name.</param>
    /// <param name="value">Receives the matching column when the lookup succeeds.</param>
    /// <returns><see langword="true"/> when the named column exists and was returned; otherwise, <see langword="false"/>.</returns>
    public bool TryGetValue(string key, out ColumnModel value)
        => _byName.TryGetValue(key, out value!);

    /// <summary>
    /// Returns a generic enumerator over columns in schema order.
    /// </summary>
    /// <returns>An enumerator over <see cref="Columns"/>.</returns>
    public IEnumerator<ColumnModel> GetEnumerator() => _columns.GetEnumerator();
    /// <summary>
    /// Returns a non-generic enumerator over columns in schema order.
    /// </summary>
    /// <returns>An enumerator over <see cref="Columns"/>.</returns>
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

}
