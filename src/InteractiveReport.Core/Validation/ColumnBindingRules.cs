using InteractiveReport.Core.Expressions;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Schema;

namespace InteractiveReport.Core.Validation;

/// <summary>
/// Shared scalar binding rules used while completing a canonical table. These helpers
/// validate names against a bound schema; they do not interpret report composables or
/// construct an alternate execution plan.
/// </summary>
internal static class ColumnBindingRules
{
    private static readonly IReadOnlyDictionary<string, string> NoLabels =
        new Dictionary<string, string>();

    internal static IReadOnlyDictionary<string, string> ResolveLabels(
        Dictionary<string, string>? labels)
    {
        if (labels is not { Count: > 0 }) return NoLabels;

        var resolved = new Dictionary<string, string>(labels.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, label) in labels)
        {
            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(label))
                resolved[name] = label.Trim();
        }
        return resolved;
    }

    /// <summary>
    /// Drops filter rules that reference a non-filterable base column. Runs after
    /// expression binding so malformed expressions still surface as precise errors.
    /// A filter on a computed column stays even when that computation reads restricted
    /// input columns.
    /// </summary>
    internal static List<CompiledRule<IncludeRowEffect>> StripRestrictedFilters(
        List<CompiledRule<IncludeRowEffect>> filters,
        ColumnPolicy policy,
        ReportSchema schema,
        List<IgnoredItem> ignored)
    {
        if (filters.Count == 0 || !policy.HasFilterRestrictions) return filters;

        var kept = new List<CompiledRule<IncludeRowEffect>>(filters.Count);
        foreach (var rule in filters)
        {
            var blocked = ExprColumns.Collect(rule.Expression.Ast)
                .FirstOrDefault(name => schema.TryGetValue(name, out var column)
                    && !policy.IsFilterable(column));
            if (blocked is null)
            {
                kept.Add(rule);
                continue;
            }

            ignored.Add(new IgnoredItem(
                "filter",
                $"filter references non-filterable column '{blocked}'"));
        }
        return kept;
    }

    /// <summary>
    /// Appends definition-authored edit-link dependencies to the private row
    /// projection. The displayed output contract remains unchanged.
    /// </summary>
    internal static void AddEditLinkColumns(
        ReportEditLink editLink,
        List<ColumnModel> projection,
        ReportSchema baseSchema,
        List<IgnoredItem> ignored)
    {
        var placeholders = EditLinkTemplate.Parse(editLink.UrlTemplate, out var error);
        if (placeholders is null)
        {
            ignored.Add(new IgnoredItem("editLink", $"invalid urlTemplate — {error}"));
            return;
        }

        var seen = new HashSet<string>(
            projection.Select(column => column.Name),
            StringComparer.OrdinalIgnoreCase);
        foreach (var name in placeholders)
        {
            if (!baseSchema.TryGetValue(name, out var column))
            {
                ignored.Add(new IgnoredItem(
                    "editLink",
                    $"references unknown column '{name}'"));
                continue;
            }
            if (seen.Add(column.Name)) projection.Add(column);
        }
    }

    /// <summary>
    /// Adds schema-bound renderer dependencies to the private row projection. Link,
    /// image, and action sources never become visible result columns by implication.
    /// </summary>
    internal static List<ColumnModel> ResolveRendererColumns(
        IReadOnlyDictionary<string, ColumnFormat> formats,
        IReadOnlyList<ColumnModel> displayed,
        ReportSchema schema,
        List<IgnoredItem> ignored)
    {
        var result = displayed.ToList();
        if (formats.Count == 0) return result;
        var seen = new HashSet<string>(
            result.Select(column => column.Name),
            StringComparer.OrdinalIgnoreCase);

        foreach (var column in displayed)
        {
            if (!formats.TryGetValue(column.Name, out var format)) continue;

            var renderer = format.DisplayAs?.Trim();
            if (string.Equals(renderer, "action", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(format.KeyColumn))
                    Add(format.KeyColumn, "key", column);
                continue;
            }
            if (!string.Equals(renderer, "link", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(renderer, "image", StringComparison.OrdinalIgnoreCase))
                continue;

            Add(format.UrlColumn, "URL", column);
            if (string.Equals(renderer, "link", StringComparison.OrdinalIgnoreCase))
                Add(format.TextColumn, "text", column);
        }

        return result;

        void Add(string? requested, string role, ColumnModel fallback)
        {
            if (string.IsNullOrWhiteSpace(requested))
            {
                if (seen.Add(fallback.Name)) result.Add(fallback);
                return;
            }

            if (!schema.TryGetValue(requested, out var source))
            {
                ignored.Add(new IgnoredItem(
                    "format",
                    $"renderer for '{fallback.Name}' references unknown {role} column '{requested}'"));
                return;
            }

            if (seen.Add(source.Name)) result.Add(source);
        }
    }
}
