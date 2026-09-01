namespace InteractiveReport.Core.Model;

/// <summary>
/// The canonical end-user feature tokens a definition's <see cref="ReportDefinition.Features"/>
/// control suggestion may name. Tokens are matched case-insensitively in configuration but always
/// travel to the client in this canonical casing. A browser client may override presentation;
/// independently enforced endpoint policies remain server-owned.
/// </summary>
public static class ReportFeatures
{
    /// <summary>Enables the toolbar search bar, including unscoped and column-scoped search.</summary>
    public const string Search = "search";

    /// <summary>Enables the Columns dialog and the header-menu Hide Column entry.</summary>
    public const string Columns = "columns";

    /// <summary>Enables the header-menu Rename entry for client-side display labels.</summary>
    public const string Rename = "rename";

    /// <summary>
    /// Enables the Column Settings dialog for per-column masks, alignment, and styling. The
    /// settings are stored in the state document's formats map. Its visibility checkbox additionally needs
    /// <see cref="Columns"/>, whose visible-columns list it writes.
    /// </summary>
    public const string ColumnSettings = "columnSettings";

    /// <summary>Enables filter rules.</summary>
    public const string Filter = "filter";
    /// <summary>Enables sort rules.</summary>
    public const string Sort = "sort";
    /// <summary>Enables paged result delivery.</summary>
    public const string Pagination = "pagination";
    /// <summary>Enables control-break grouping and subtotals.</summary>
    public const string ControlBreak = "controlBreak";
    /// <summary>Enables conditional row and cell highlighting.</summary>
    public const string Highlight = "highlight";
    /// <summary>Enables aggregate footers.</summary>
    public const string Aggregate = "aggregate";
    /// <summary>Enables computed columns.</summary>
    public const string Compute = "compute";
    /// <summary>Enables grouped result shapes.</summary>
    public const string GroupBy = "groupBy";
    /// <summary>Enables pivot result shapes.</summary>
    public const string Pivot = "pivot";
    /// <summary>Enables chart result shapes.</summary>
    public const string Chart = "chart";

    /// <summary>
    /// Enables end-user saved-report management: Save, Save As, Delete, and the saved-report
    /// select. Server-enforced at creation (POST saved); existing saved reports remain
    /// governed by the ownership matrix so leftovers stay manageable after a config change.
    /// </summary>
    public const string SavedReports = "savedReports";

    /// <summary>Enables the Download menu. The export endpoint enforces this feature server-side.</summary>
    public const string Download = "download";

    /// <summary>Gets every known feature token in stable client display order.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        Search, Columns, Rename, ColumnSettings, Filter, Sort, Pagination, ControlBreak, Highlight,
        Aggregate, Compute, GroupBy, Pivot, Chart, SavedReports, Download,
    ];

    /// <summary>
    /// Returns the canonical casing of a configured token.
    /// </summary>
    /// <param name="feature">The report feature whose effective setting is being resolved.</param>
    /// <returns>The feature token in canonical casing, or <see langword="null"/> when unknown.</returns>
    public static string? Canonical(string? feature)
        => All.FirstOrDefault(known => string.Equals(known, feature, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Determines whether a feature is configured. A null list includes everything; otherwise membership is case-insensitive.
    /// </summary>
    /// <param name="definition">The report definition containing the optional feature list.</param>
    /// <param name="feature">The report feature whose effective setting is being resolved.</param>
    /// <returns><see langword="true"/> when the feature is included by the definition; otherwise, <see langword="false"/>.</returns>
    public static bool IsEnabled(ReportDefinition definition, string feature)
        => definition.Features is null
            || definition.Features.Any(entry => string.Equals(entry, feature, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Resolves the definition's feature suggestion in canonical casing and stable order.
    /// </summary>
    /// <param name="definition">The report definition that supplies the data source, dialect, policy, and execution limits.</param>
    /// <returns>The enabled feature tokens in <see cref="All"/> order.</returns>
    public static IReadOnlyList<string> Resolve(ReportDefinition definition)
        => definition.Features is null ? All : All.Where(feature => IsEnabled(definition, feature)).ToArray();
}
