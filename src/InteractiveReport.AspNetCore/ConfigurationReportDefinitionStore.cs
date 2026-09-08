using System.Text.Json;
using System.Text.RegularExpressions;
using InteractiveReport.Core.Definitions;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Schema;
using Microsoft.Extensions.Options;

namespace InteractiveReport.AspNetCore;

/// <summary>
/// Resolves report definitions from monitored application configuration. Each lookup returns a detached,
/// validated snapshot with its connection and dialect resolved. When saved-report storage is enabled,
/// the persistence-backed administrative definition is exposed without changing saved-report state.
/// Configuration reloads clear discovered schemas so subsequent requests cannot reuse stale metadata.
/// </summary>
public sealed partial class ConfigurationReportDefinitionStore :
    IReportDefinitionStore,
    IReportDefinitionAuthorizationStore,
    IDisposable
{
    private readonly IOptionsMonitor<InteractiveReportOptions> _options;
    private readonly ReportConnectionRegistry _registry;
    private readonly bool _listingEnabled;
    private readonly IDisposable? _reloadSubscription;

    /// <summary>
    /// Initializes a definition store that exposes configured reports only, without the built-in
    /// saved-reports listing.
    /// </summary>
    /// <param name="options">The monitored Interactive Reports configuration source.</param>
    /// <param name="schemaCache">The cache used to reuse discovered schemas across requests.</param>
    /// <param name="registry">Resolves connection names and SQL dialects for definitions.</param>
    /// <remarks>Subscribes to option reloads and clears <paramref name="schemaCache"/> after each reload.</remarks>
    internal ConfigurationReportDefinitionStore(
        IOptionsMonitor<InteractiveReportOptions> options,
        SchemaCache schemaCache,
        ReportConnectionRegistry registry)
        : this(options, schemaCache, registry, listingEnabled: false)
    {
    }

    /// <summary>
    /// Initializes a definition store, optionally exposing the built-in saved-reports listing.
    /// </summary>
    /// <param name="options">The monitored Interactive Reports configuration source.</param>
    /// <param name="schemaCache">The cache used to reuse discovered schemas across requests.</param>
    /// <param name="registry">Resolves connection names and SQL dialects for definitions.</param>
    /// <param name="listingEnabled">Whether the reserved saved-reports listing resolves to its administration definition.</param>
    /// <remarks>Subscribes to option reloads and clears <paramref name="schemaCache"/> after each reload.</remarks>
    internal ConfigurationReportDefinitionStore(
        IOptionsMonitor<InteractiveReportOptions> options,
        SchemaCache schemaCache,
        ReportConnectionRegistry registry,
        bool listingEnabled)
    {
        _options = options;
        _registry = registry;
        _listingEnabled = listingEnabled;
        _reloadSubscription = options.OnChange(_ => schemaCache.Clear());
    }

    /// <summary>
    /// Resolves a detached report definition, including the synthetic saved-reports listing when configured.
    /// </summary>
    /// <param name="name">The case-insensitive report name.</param>
    /// <param name="ct">Cancels the definition lookup before configuration is read.</param>
    /// <returns>The validated definition snapshot, or <see langword="null"/> when the name is unknown or the internal constructor disables the built-in listing.</returns>
    /// <exception cref="InvalidOperationException">Thrown when configuration is invalid.</exception>
    /// <remarks>Does not reconcile report documents; the family-list endpoints own that boundary.</remarks>
    public ValueTask<ReportDefinition?> Find(string name, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (SavedReportsListingDefinition.Matches(name))
        {
            // Configuration cannot declare or shadow the reserved built-in report name.
            if (_options.CurrentValue.Reports.ContainsKey(name))
                throw new InvalidOperationException(
                    $"Report '{name}': this name is reserved for the built-in saved-reports listing.");
            // A store built without the listing does not expose the built-in persistence-backed
            // definition at all.
            if (!_listingEnabled)
                return ValueTask.FromResult<ReportDefinition?>(null);
            // The built-in report is an administration feature. Resolving its target here
            // produces the normal sanitized configuration error without making persistence a
            // prerequisite for ordinary report definitions.
            var savedConfig = _registry.ResolveStoreConfig(_options.CurrentValue.SavedReports);
            return ValueTask.FromResult<ReportDefinition?>(
                SavedReportsListingDefinition.Create(savedConfig));
        }

        var reports = _options.CurrentValue.Reports;
        if (!reports.TryGetValue(name, out var def))
            return ValueTask.FromResult<ReportDefinition?>(null);

        // Invariant: the lookup accepts any casing, but the configured key is the canonical
        // name: it becomes REPORT_NAME in saved-report rows and the filter that finds them
        // again, so it must be a single spelling on case-sensitive databases.
        var configuredName = reports.Keys.FirstOrDefault(key => string.Equals(key, name, StringComparison.Ordinal))
            ?? reports.Keys.First(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));
        var snapshot = Snapshot(configuredName, def);
        Validate(snapshot);
        ResolveConnection(snapshot, _registry);
        return ValueTask.FromResult<ReportDefinition?>(snapshot);
    }

    /// <summary>
    /// Loads the lightweight authorization envelope for a report definition.
    /// </summary>
    /// <param name="name">The case-insensitive configured or built-in report name.</param>
    /// <param name="ct">Cancels the lookup before configuration is read.</param>
    /// <returns>A completed value task containing the canonical report name and detached authorization settings, or <see langword="null"/> when unknown.</returns>
    /// <exception cref="InvalidOperationException">Thrown when configuration shadows the reserved built-in report name.</exception>
    public ValueTask<ReportDefinitionAuthorization?> FindAuthorization(
        string name,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (SavedReportsListingDefinition.Matches(name))
        {
            if (_options.CurrentValue.Reports.ContainsKey(name))
                throw new InvalidOperationException(
                    $"Report '{name}': this name is reserved for the built-in saved-reports listing.");
            if (!_listingEnabled)
                return ValueTask.FromResult<ReportDefinitionAuthorization?>(null);

            // The listing carries no configured block: the authorization service recognizes the
            // reserved name and admits administrators only.
            return ValueTask.FromResult<ReportDefinitionAuthorization?>(new(
                SavedReportsListingDefinition.Name,
                null));
        }

        var reports = _options.CurrentValue.Reports;
        if (!reports.TryGetValue(name, out var definition))
            return ValueTask.FromResult<ReportDefinitionAuthorization?>(null);

        var configuredName = reports.Keys.FirstOrDefault(key => string.Equals(key, name, StringComparison.Ordinal))
            ?? reports.Keys.First(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));
        return ValueTask.FromResult<ReportDefinitionAuthorization?>(new(
            configuredName,
            SnapshotAuthorization(definition.Authorization)));
    }

    /// <summary>
    /// Copies mutable authorization configuration into a request-stable value.
    /// </summary>
    /// <param name="source">The mutable configuration object to copy into an immutable runtime model.</param>
    /// <returns>A detached authorization object, or <see langword="null"/> when no authorization block is configured.</returns>
    private static ReportAuthorization? SnapshotAuthorization(ReportAuthorization? source)
        => source is null
            ? null
            : new ReportAuthorization
            {
                Policy = source.Policy,
                AllowAnonymous = source.AllowAnonymous,
            };

    /// <summary>
    /// Stamps the resolved connection name and dialect onto the detached snapshot before
    /// anything downstream sees it. The dialect assignment is unconditional: dialect is a property of the
    /// connection, so a configured value (a leftover from before it was derived) is simply superseded, never
    /// validated. Shared with the startup validator, which runs the same pipeline without Find's
    /// saved-report side effects.
    /// </summary>
    /// <param name="def">The detached definition whose connection fields will be resolved in place.</param>
    /// <param name="registry">Resolves registered or data-source-backed connections and dialects.</param>
    /// <remarks>Mutates <paramref name="def"/>. A data source replaces its connection name with a synthesized name; every path overwrites the dialect.</remarks>
    internal static void ResolveConnection(ReportDefinition def, ReportConnectionRegistry registry)
    {
        if (!string.IsNullOrWhiteSpace(def.DataSource))
        {
            var (connectionName, dialect) = registry.ResolveDataSource(
                $"Report '{def.Name}'", def.DataSource, def.Provider);
            def.Connection = connectionName;
            def.Dialect = dialect;
            return;
        }
        def.Dialect = registry.ResolveDialect(def.Connection);
    }

    /// <summary>
    /// Deep-copies a mutable options-bound definition and assigns its canonical configured name.
    /// </summary>
    /// <param name="name">The canonical key from the report configuration dictionary.</param>
    /// <param name="source">The mutable options-bound definition to copy.</param>
    /// <returns>A detached definition that request processing may safely mutate.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the definition cannot be round-tripped through the protocol serializer.</exception>
    internal static ReportDefinition Snapshot(string name, ReportDefinition source)
    {
        // The options monitor owns and may replace its object graph. Returning a
        // detached snapshot prevents request code from mutating configuration or observing a
        // half-reloaded nested definition.
        var snapshot = JsonSerializer.Deserialize<ReportDefinition>(
            JsonSerializer.Serialize(source, IrJson.Options),
            IrJson.Options) ?? throw new InvalidOperationException($"Report '{name}': definition could not be copied.");
        snapshot.Name = name;
        return snapshot;
    }

    /// <summary>
    /// Validates definition-level authorization, data source, limits, SQL, presentation, and default-state configuration.
    /// </summary>
    /// <param name="def">The detached definition to validate before connection resolution or execution.</param>
    /// <exception cref="InvalidOperationException">Thrown with the report name and offending setting when configuration is inconsistent or unsafe.</exception>
    internal static void Validate(ReportDefinition def)
    {
        if (string.IsNullOrWhiteSpace(def.Name))
            throw new InvalidOperationException("Report name is required.");
        if (def.Name.StartsWith("__", StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Report '{def.Name}': names beginning with '__' are reserved for built-in reports.");
        if (ReservedRouteNames.Contains(def.Name, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Report '{def.Name}': this name is shadowed by a built-in endpoint route and would be "
                + $"unreachable — reserved names are {string.Join(", ", ReservedRouteNames)}.");
        if (def.Authorization?.Policy is not null
            && string.IsNullOrWhiteSpace(def.Authorization.Policy))
            throw new InvalidOperationException(
                $"Report '{def.Name}': authorization policy must be non-empty when specified.");
        if (def.Authorization is { AllowAnonymous: true, Policy: not null })
            throw new InvalidOperationException(
                $"Report '{def.Name}': authorization policy cannot be combined with allowAnonymous.");
        if (string.IsNullOrWhiteSpace(def.Sql))
            throw new InvalidOperationException($"Report '{def.Name}': sql is required.");

        var hasDataSource = !string.IsNullOrWhiteSpace(def.DataSource);
        var hasConnection = !string.IsNullOrWhiteSpace(def.Connection);
        if (hasDataSource && hasConnection)
            throw new InvalidOperationException(
                $"Report '{def.Name}': set dataSource or connection, not both.");
        if (!hasDataSource && !hasConnection)
            throw new InvalidOperationException(
                $"Report '{def.Name}': a data source is required — set dataSource (a ConnectionStrings name or a "
                + "literal connection string) or connection (a name registered with AddConnection).");
        if (!hasDataSource && !string.IsNullOrWhiteSpace(def.Provider))
            throw new InvalidOperationException(
                $"Report '{def.Name}': provider applies to dataSource — remove it, or replace connection with dataSource.");
        if (hasConnection && def.Connection.StartsWith("__ir:", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Report '{def.Name}': connection names beginning with '__ir:' are reserved.");

        if (def.MaxPageSize < 1)
            throw new InvalidOperationException($"Report '{def.Name}': maxPageSize must be at least 1.");
        if (def.DefaultPageSize < 1 || def.DefaultPageSize > def.MaxPageSize)
            throw new InvalidOperationException(
                $"Report '{def.Name}': defaultPageSize must be between 1 and maxPageSize ({def.MaxPageSize}).");
        if (def.MaxRows > 0 && def.MaxPageSize > def.MaxRows)
            throw new InvalidOperationException(
                $"Report '{def.Name}': maxPageSize ({def.MaxPageSize}) must not exceed maxRows ({def.MaxRows}).");
        if (def.MaxPivotColumns < 1 || def.MaxPivotColumns > ReportExecutor.MaxPivotGroups)
            throw new InvalidOperationException(
                $"Report '{def.Name}': maxPivotColumns must be between 1 and {ReportExecutor.MaxPivotGroups}.");
        if (def.MaxChartPoints < 1 || def.MaxChartPoints > ReportExecutor.MaxChartPointsCeiling)
            throw new InvalidOperationException(
                $"Report '{def.Name}': maxChartPoints must be between 1 and {ReportExecutor.MaxChartPointsCeiling}.");
        if (def.CommandTimeoutSeconds < 1)
            throw new InvalidOperationException(
                $"Report '{def.Name}': commandTimeoutSeconds must be at least 1.");
        if (!Enum.IsDefined(def.Consistency))
            throw new InvalidOperationException(
                $"Report '{def.Name}': unknown consistency strategy '{def.Consistency}' (known: none, snapshot).");
        // The base SELECT becomes a derived table; a trailing ORDER BY
        // breaks that on SQL Server (APEX imposes the same rule). The scanner is comment-,
        // string-, and quoted-identifier-aware, so 'order by' as data or documentation never
        // trips this — only the real clause at parenthesis depth 0 does.
        if (SqlTopLevelScanner.HasTopLevelOrderBy(def.Sql))
            throw new InvalidOperationException(
                $"Report '{def.Name}': base query must not end with ORDER BY — sorting belongs to report state.");

        if (def.ContextParams is not null)
        {
            foreach (var name in def.ContextParams.Keys)
            {
                if (ReservedParamPattern().IsMatch(name))
                    throw new InvalidOperationException(
                        $"Report '{def.Name}': context parameter name '{name}' is reserved for composer bindings (p0, p1, ...).");
            }
        }

        if (def.Features is not null)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var feature in def.Features)
            {
                if (string.IsNullOrWhiteSpace(feature))
                    throw new InvalidOperationException(
                        $"Report '{def.Name}': features contains a blank entry.");
                if (ReportFeatures.Canonical(feature) is not { } canonical)
                    throw new InvalidOperationException(
                        $"Report '{def.Name}': unknown feature '{feature}' (known: {string.Join(", ", ReportFeatures.All)}).");
                if (!seen.Add(canonical))
                    throw new InvalidOperationException(
                        $"Report '{def.Name}': features contains duplicate entry '{canonical}'.");
            }
        }

        if (def.ColumnLabels is not null)
        {
            // Unknown column names stay tolerated (schema drift), but a blank or case-colliding
            // entry is a config mistake worth failing fast on.
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, label) in def.ColumnLabels)
            {
                if (string.IsNullOrWhiteSpace(name))
                    throw new InvalidOperationException(
                        $"Report '{def.Name}': columnLabels contains a blank column name.");
                if (string.IsNullOrWhiteSpace(label))
                    throw new InvalidOperationException(
                        $"Report '{def.Name}': columnLabels['{name}'] must not be blank.");
                if (!names.Add(name))
                    throw new InvalidOperationException(
                        $"Report '{def.Name}': columnLabels contains duplicate column '{name}' (names are case-insensitive).");
            }
        }

        if (def.DocumentFiles is not null)
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in def.DocumentFiles)
            {
                if (string.IsNullOrWhiteSpace(path))
                    throw new InvalidOperationException(
                        $"Report '{def.Name}': documentFiles contains a blank path.");
                if (!paths.Add(path.Trim()))
                    throw new InvalidOperationException(
                        $"Report '{def.Name}': documentFiles contains duplicate path '{path}'.");
            }
        }

        ValidateEditLink(def);
        ValidateCreateLink(def);
        ValidateColumnOverrides(def);
    }

    /// <summary>
    /// Validates a definition's per-row edit-link template and presentation options.
    /// </summary>
    /// <param name="def">The definition whose optional edit link is being validated.</param>
    /// <exception cref="InvalidOperationException">Thrown when the template, label, or mode violates the public contract.</exception>
    private static void ValidateEditLink(ReportDefinition def)
    {
        if (def.EditLink is not { } editLink) return;

        if (string.IsNullOrWhiteSpace(editLink.UrlTemplate))
            throw new InvalidOperationException(
                $"Report '{def.Name}': editLink.urlTemplate is required.");
        if (editLink.UrlTemplate.Length > 2048)
            throw new InvalidOperationException(
                $"Report '{def.Name}': editLink.urlTemplate must be at most 2048 characters.");
        var placeholders = EditLinkTemplate.Parse(editLink.UrlTemplate, out var templateError);
        if (placeholders is null)
            throw new InvalidOperationException(
                $"Report '{def.Name}': editLink.urlTemplate is invalid — {templateError}.");
        if (placeholders.Count == 0)
            throw new InvalidOperationException(
                $"Report '{def.Name}': editLink.urlTemplate needs at least one {{COLUMN}} placeholder — a constant URL is not a per-row edit link.");
        ValidateLinkPresentation(def, "editLink", editLink.Label, editLink.Mode);
    }

    /// <summary>
    /// Validates a definition's toolbar create button. The URL is a constant: placeholders are
    /// rejected because no row exists to substitute, and the URL may be omitted only in event mode.
    /// </summary>
    /// <param name="def">The definition whose optional create link is being validated.</param>
    /// <exception cref="InvalidOperationException">Thrown when the URL, label, or mode violates the public contract.</exception>
    private static void ValidateCreateLink(ReportDefinition def)
    {
        if (def.CreateLink is not { } createLink) return;

        var eventMode = string.Equals(createLink.Mode, "event", StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(createLink.Url))
        {
            if (!eventMode)
                throw new InvalidOperationException(
                    $"Report '{def.Name}': createLink.url is required unless createLink.mode is 'event'.");
        }
        else
        {
            if (createLink.Url.Length > 2048)
                throw new InvalidOperationException(
                    $"Report '{def.Name}': createLink.url must be at most 2048 characters.");
            if (createLink.Url.Contains('{') || createLink.Url.Contains('}'))
                throw new InvalidOperationException(
                    $"Report '{def.Name}': createLink.url does not take {{COLUMN}} placeholders — there is no row to create from.");
        }

        ValidateLinkPresentation(def, "createLink", createLink.Label, createLink.Mode);
    }

    /// <summary>Validates the label and mode; destinations and targets are trusted application choices.</summary>
    private static void ValidateLinkPresentation(
        ReportDefinition def, string setting, string? label, string? mode)
    {
        if (label is not null && string.IsNullOrWhiteSpace(label))
            throw new InvalidOperationException(
                $"Report '{def.Name}': {setting}.label must not be blank.");
        if (label is { Length: > 200 })
            throw new InvalidOperationException(
                $"Report '{def.Name}': {setting}.label must be at most 200 characters.");
        if (mode is not null
            && !string.Equals(mode, "navigate", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(mode, "event", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Report '{def.Name}': {setting}.mode must be 'navigate' or 'event'.");
    }

    /// <summary>
    /// Validates configured column overrides and their interaction with the default state.
    /// </summary>
    /// <param name="def">The definition whose optional column settings are being validated.</param>
    /// <exception cref="InvalidOperationException">Thrown for invalid names, labels, help text, duplicates, or a default sort/break on a locked column.</exception>
    private static void ValidateColumnOverrides(ReportDefinition def)
    {
        if (def.Columns is null) return;

        // Unknown column names stay tolerated (schema drift, same as columnLabels); blank keys,
        // case collisions, and double-configured labels fail fast.
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, over) in def.Columns)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException(
                    $"Report '{def.Name}': columns contains a blank column name.");
            if (!names.Add(name))
                throw new InvalidOperationException(
                    $"Report '{def.Name}': columns contains duplicate column '{name}' (names are case-insensitive).");
            if (over is null) continue;
            if (over.Label is not null && string.IsNullOrWhiteSpace(over.Label))
                throw new InvalidOperationException(
                    $"Report '{def.Name}': columns['{name}'].label must not be blank — use hideLabel to suppress the header text.");
            if (over.Label is { Length: > 200 })
                throw new InvalidOperationException(
                    $"Report '{def.Name}': columns['{name}'].label must be at most 200 characters.");
            if (over.Label is not null && def.ColumnLabels?.Keys.Any(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) == true)
                throw new InvalidOperationException(
                    $"Report '{def.Name}': column '{name}' has a label in both columnLabels and columns — configure it in one place.");
            if (over.HelpText is not null && string.IsNullOrWhiteSpace(over.HelpText))
                throw new InvalidOperationException(
                    $"Report '{def.Name}': columns['{name}'].helpText must not be blank.");
            if (over.HelpText is { Length: > 1000 })
                throw new InvalidOperationException(
                    $"Report '{def.Name}': columns['{name}'].helpText must be at most 1000 characters.");
        }

        // Invariant: a definition contradicting itself is a config mistake, not saved-state
        // drift: the default view must not sort or break on a column the definition locks.
        // (Default-state filters are expressions needing the schema; they degrade into
        // ignored[] at query time instead.)
        var restricted = new HashSet<string>(
            def.Columns.Where(e => e.Value?.Sortable == false).Select(e => e.Key),
            StringComparer.OrdinalIgnoreCase);
        if (restricted.Count == 0) return;
        var source = DefinitionInputTable(def.DefaultState);
        var sorted = source?.Composables?
            .Where(composable => string.Equals(composable.Kind, "sort", StringComparison.OrdinalIgnoreCase))
            .SelectMany(composable => composable.Sorts ?? [])
            .FirstOrDefault(sort => restricted.Contains(sort.Col));
        if (sorted is not null)
            throw new InvalidOperationException(
                $"Report '{def.Name}': defaultState sorts on '{sorted.Col}' but columns['{sorted.Col}'] is not sortable.");
        var broken = source?.Composables?
            .Where(composable => string.Equals(composable.Kind, "break", StringComparison.OrdinalIgnoreCase))
            .SelectMany(composable => composable.Breaks ?? [])
            .FirstOrDefault(restricted.Contains);
        if (broken is not null)
            throw new InvalidOperationException(
                $"Report '{def.Name}': defaultState breaks on '{broken}' but columns['{broken}'] is not sortable (control breaks imply sorting).");
    }

    /// <summary>
    /// Finds the definition-input table in the active table's ancestry. A table map
    /// is unordered, so unrelated roots and insertion order must not affect validation.
    /// </summary>
    /// <param name="state">The optional default state whose active ancestry should be followed.</param>
    /// <returns>The table that reads from <c>definition</c>, or <see langword="null"/> for a missing, broken, or cyclic ancestry.</returns>
    private static ReportTable? DefinitionInputTable(ReportState? state)
    {
        if (state?.Tables is not { Count: > 0 } tables
            || string.IsNullOrWhiteSpace(state.ActiveTable))
            return null;

        var lookup = new Dictionary<string, ReportTable>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, table) in tables)
            lookup.TryAdd(name, table);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? current = state.ActiveTable;
        while (!string.IsNullOrWhiteSpace(current)
            && seen.Add(current)
            && lookup.TryGetValue(current, out var table))
        {
            if (string.Equals(table.From, "definition", StringComparison.OrdinalIgnoreCase))
                return table;
            current = table.From;
        }

        return null;
    }

    /// <summary>
    /// Reserved route names: first-segment literals of the mounted endpoint routes. ASP.NET's literal-first
    /// routing makes a report with one of these names unreachable (or, worse,
    /// partially reachable), so configuration fails fast instead.
    /// </summary>
    private static readonly string[] ReservedRouteNames = ["ui", "saved", "whoami", "admin"];

    /// <summary>
    /// Builds the compiled expression used to reserve composer-generated parameter names such as <c>p0</c>.
    /// </summary>
    /// <returns>The compiled regular expression.</returns>
    [GeneratedRegex(@"^p\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex ReservedParamPattern();

    /// <summary>
    /// Unsubscribes from configuration reload notifications.
    /// </summary>
    public void Dispose() => _reloadSubscription?.Dispose();
}
