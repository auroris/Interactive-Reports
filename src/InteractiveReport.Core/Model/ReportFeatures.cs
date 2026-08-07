namespace InteractiveReport.Core.Model;

/// <summary>
/// The canonical end-user feature tokens a definition's <see cref="ReportDefinition.Features"/>
/// whitelist may name. Tokens are matched case-insensitively in configuration but always
/// travel to the client in this canonical casing.
/// </summary>
public static class ReportFeatures
{
    /// <summary>The toolbar search bar (unscoped and column-scoped search).</summary>
    public const string Search = "search";

    /// <summary>The Columns… dialog and the header-menu Hide Column entry.</summary>
    public const string Columns = "columns";

    /// <summary>The header-menu Rename… entry (client-side display labels).</summary>
    public const string Rename = "rename";

    /// <summary>
    /// The Column Settings… dialog (per-column mask, alignment, and styling — the
    /// state document's formats map). Its visibility checkbox additionally needs
    /// <see cref="Columns"/>, whose visible-columns list it writes.
    /// </summary>
    public const string ColumnSettings = "columnSettings";

    public const string Filter = "filter";
    public const string Sort = "sort";
    public const string Pagination = "pagination";
    public const string ControlBreak = "controlBreak";
    public const string Highlight = "highlight";
    public const string Aggregate = "aggregate";
    public const string Compute = "compute";
    public const string GroupBy = "groupBy";
    public const string Pivot = "pivot";
    public const string Chart = "chart";

    /// <summary>
    /// End-user saved-report management: Save/Save As…/Delete… and the saved-report
    /// select. Server-enforced at creation (POST saved); existing saved reports remain
    /// governed by the ownership matrix so leftovers stay manageable after a config change.
    /// </summary>
    public const string SavedReports = "savedReports";

    /// <summary>The Download menu (CSV). Server-enforced at the export endpoint.</summary>
    public const string Download = "download";

    public static readonly IReadOnlyList<string> All =
    [
        Search, Columns, Rename, ColumnSettings, Filter, Sort, Pagination, ControlBreak, Highlight,
        Aggregate, Compute, GroupBy, Pivot, Chart, SavedReports, Download,
    ];

    /// <summary>The canonical casing of a configured token, or null if it names no known feature.</summary>
    public static string? Canonical(string? feature)
        => All.FirstOrDefault(known => string.Equals(known, feature, StringComparison.OrdinalIgnoreCase));

    /// <summary>A null whitelist enables everything; otherwise membership, case-insensitively.</summary>
    public static bool IsEnabled(ReportDefinition definition, string feature)
        => definition.Features is null
            || definition.Features.Any(entry => string.Equals(entry, feature, StringComparison.OrdinalIgnoreCase));

    /// <summary>The definition's effective feature set, in canonical casing and order.</summary>
    public static IReadOnlyList<string> Resolve(ReportDefinition definition)
        => definition.Features is null ? All : All.Where(feature => IsEnabled(definition, feature)).ToArray();
}
