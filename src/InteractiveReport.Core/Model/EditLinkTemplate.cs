namespace InteractiveReport.Core.Model;

/// <summary>
/// The edit link's {COLUMN} placeholder syntax, shared by definition validation,
/// schema delivery, and query-time projection so all three read one grammar.
/// Deliberately minimal: no escape syntax — a literal brace has no place in an
/// edit URL, and rejecting it keeps every template unambiguous.
/// </summary>
public static class EditLinkTemplate
{
    /// <summary>
    /// Extracts placeholder names in order of first appearance (deduplicated
    /// case-insensitively). Returns null and an error message for unmatched,
    /// empty, or nested braces.
    /// </summary>
    public static IReadOnlyList<string>? Parse(string template, out string? error)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var start = -1;
        for (var i = 0; i < template.Length; i++)
        {
            switch (template[i])
            {
                case '{' when start >= 0:
                    error = $"nested '{{' at position {i}";
                    return null;
                case '{':
                    start = i;
                    break;
                case '}' when start < 0:
                    error = $"'}}' without a matching '{{' at position {i}";
                    return null;
                case '}':
                    var name = template[(start + 1)..i];
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        error = $"empty placeholder at position {start}";
                        return null;
                    }
                    if (seen.Add(name)) names.Add(name);
                    start = -1;
                    break;
            }
        }
        if (start >= 0)
        {
            error = $"'{{' without a matching '}}' at position {start}";
            return null;
        }
        error = null;
        return names;
    }

    /// <summary>
    /// Rewrites each placeholder through <paramref name="map"/> (e.g. to the schema's
    /// canonical column casing). Call only on templates <see cref="Parse"/> accepted.
    /// </summary>
    public static string Rewrite(string template, Func<string, string> map)
    {
        var result = new System.Text.StringBuilder(template.Length);
        var start = -1;
        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];
            if (c == '{') start = i;
            else if (c == '}' && start >= 0)
            {
                result.Append('{').Append(map(template[(start + 1)..i])).Append('}');
                start = -1;
            }
            else if (start < 0) result.Append(c);
        }
        return result.ToString();
    }
}
