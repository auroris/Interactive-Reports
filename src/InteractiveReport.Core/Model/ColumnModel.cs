namespace InteractiveReport.Core.Model;

/// <summary>
/// A column discovered from the base query's result schema. The developer's SELECT plus
/// this discovered set is the entire model — there is no semantic-model layer.
/// </summary>
public sealed class ColumnModel
{
    public required string Name { get; init; }
    public required string Label { get; init; }
    public required Type ClrType { get; init; }
    public bool IsNullable { get; init; } = true;
    public bool IsComputed { get; init; }

    public ColumnKind Kind => GetKind(ClrType);

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

    /// <summary>Client-facing type string used by UIs to pick input widgets.</summary>
    public string KindName => Kind switch
    {
        ColumnKind.Text => "text",
        ColumnKind.Number => "number",
        ColumnKind.Date => "date",
        ColumnKind.Bool => "bool",
        _ => "other",
    };

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

public enum ColumnKind
{
    Text,
    Number,
    Date,
    Bool,
    Other,
}
