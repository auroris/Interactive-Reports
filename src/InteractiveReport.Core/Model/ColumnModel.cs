namespace InteractiveReport.Core.Model;

/// <summary>
/// A column discovered from the base query's result schema. The developer's SELECT plus
/// this discovered set is the entire model — there is no semantic-model layer.
/// </summary>
public sealed class ColumnModel
{
    /// <summary>Gets the case-insensitive column name exposed by the current relation stage.</summary>
    public required string Name { get; init; }
    /// <summary>Gets the neutral server-derived display label.</summary>
    public required string Label { get; init; }
    /// <summary>Gets the provider CLR type reported during schema discovery or inferred for a synthetic column.</summary>
    public required Type ClrType { get; init; }
    /// <summary>Gets whether the provider permits null values.</summary>
    public bool IsNullable { get; init; } = true;
    /// <summary>Gets whether the column was introduced by an authored expression.</summary>
    public bool IsComputed { get; init; }

    /// <summary>
    /// Gets whether schema discovery established a concrete provider type. Some ADO.NET
    /// providers cannot describe expression columns during a zero-row probe. This
    /// is distinct from a concrete but non-portable <see cref="ColumnKind.Other"/>
    /// type such as a byte array or provider-specific value.
    /// </summary>
    internal bool HasKnownType { get; init; } = true;

    /// <summary>Gets the portable kind derived from <see cref="ClrType"/>.</summary>
    public ColumnKind Kind => GetKind(ClrType);

    /// <summary>
    /// Classifies a CLR type into the portable column kind used by the report protocol model.
    /// </summary>
    /// <param name="t">The CLR type to classify into a portable column kind.</param>
    /// <returns>The column kind.</returns>
    public static ColumnKind GetKind(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        if (t == typeof(string) || t == typeof(char)) return ColumnKind.Text;
        if (t == typeof(bool)) return ColumnKind.Bool;
        if (t == typeof(DateTime) || t == typeof(DateTimeOffset) || t == typeof(DateOnly)) return ColumnKind.Date;
        if (t == typeof(byte) || t == typeof(sbyte) || t == typeof(short) || t == typeof(ushort)
            || t == typeof(int) || t == typeof(uint) || t == typeof(long) || t == typeof(ulong)
            || t == typeof(float) || t == typeof(double) || t == typeof(decimal))
            return ColumnKind.Number;
        return ColumnKind.Other;
    }

    /// <summary>Gets the client-facing type string used by UIs to choose input widgets.</summary>
    public string KindName => Kind switch
    {
        ColumnKind.Text => "text",
        ColumnKind.Number => "number",
        ColumnKind.Date => "date",
        ColumnKind.Bool => "bool",
        _ => "other",
    };

    /// <summary>
    /// Converts a technical identifier into a readable label for the report protocol model.
    /// </summary>
    /// <param name="name">The technical column identifier, commonly uppercase or underscore-separated.</param>
    /// <returns>A human-readable label derived from the column name.</returns>
    public static string Prettify(string name)
    {
        var words = name.Replace('_', ' ').Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
        {
            var w = words[i];
            words[i] = w.Length switch
            {
                0 => w,
                1 => w.ToUpperInvariant(),
                _ => char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant(),
            };
        }
        return string.Join(' ', words);
    }
}

/// <summary>Classifies provider types into the portable type families understood by report features.</summary>
public enum ColumnKind
{
    /// <summary>Text or character data.</summary>
    Text,
    /// <summary>Integral, floating-point, or decimal numeric data.</summary>
    Number,
    /// <summary>Date or timestamp data.</summary>
    Date,
    /// <summary>Boolean data.</summary>
    Bool,
    /// <summary>A concrete provider type outside the portable report vocabulary.</summary>
    Other,
}
