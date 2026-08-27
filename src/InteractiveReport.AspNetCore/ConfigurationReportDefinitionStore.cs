using System.Text.RegularExpressions;
using System.Text.Json;
using InteractiveReport.Core.Definitions;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.SavedReports;
using InteractiveReport.Core.Schema;
using Microsoft.Extensions.Options;

namespace InteractiveReport.AspNetCore;

/// <summary>
/// Config-backed definition store. Definitions are validated on access (fail fast, with
/// the report named in the error), and a configuration reload clears the schema cache.
/// </summary>
public sealed partial class ConfigurationReportDefinitionStore : IReportDefinitionStore, IDisposable
{
    private readonly IOptionsMonitor<InteractiveReportOptions> _options;
    private readonly ConfiguredReportDocumentSynchronizer? _synchronizer;
    private readonly ISavedReportStore? _savedReports;
    private readonly IDisposable? _reloadSubscription;

    internal ConfigurationReportDefinitionStore(IOptionsMonitor<InteractiveReportOptions> options, SchemaCache schemaCache)
        : this(options, schemaCache, synchronizer: null!, savedReports: null!)
    {
    }

    public ConfigurationReportDefinitionStore(
        IOptionsMonitor<InteractiveReportOptions> options,
        SchemaCache schemaCache,
        ConfiguredReportDocumentSynchronizer synchronizer,
        ISavedReportStore savedReports)
    {
        _options = options;
        _synchronizer = synchronizer;
        _savedReports = savedReports;
        _reloadSubscription = options.OnChange(_ => schemaCache.Clear());
    }

    public async ValueTask<ReportDefinition?> Find(string name, CancellationToken ct = default)
    {
        if (SavedReportsListingDefinition.Matches(name))
        {
            // Reserved: configuration cannot declare or shadow the built-in name.
            if (_options.CurrentValue.Reports.ContainsKey(name))
                throw new InvalidOperationException(
                    $"Report '{name}': this name is reserved for the built-in saved-reports listing.");
            // Syncing here both freshens configured rows and — via the store's lazy
            // auto-create on its first operation — guarantees the table exists before
            // schema discovery probes it. Skipping the built-in definition entirely,
            // not synthesizing it unsynced, keeps a null synchronizer (internal test
            // constructor) honest.
            if (_synchronizer is null) return null;
            await _synchronizer.EnsureSynced(ct);
            return SavedReportsListingDefinition.Create(_options.CurrentValue.SavedReports);
        }

        if (!_options.CurrentValue.Reports.TryGetValue(name, out var def))
            return null;

        var snapshot = Snapshot(name, def);
        Validate(snapshot);
        if (_synchronizer is not null && _savedReports is not null)
        {
            await _synchronizer.EnsureSynced(ct);
            var defaultPrimary = (await _savedReports.ListVisible(snapshot.Name, identity: null, ct: ct))
                .Where(report => report.IsPrimary
                    && string.Equals(report.Title, "Default", StringComparison.OrdinalIgnoreCase))
                // A database-authored report wins if a configured title collision was
                // introduced outside the normal endpoint uniqueness checks.
                .OrderBy(report => report.Origin == SavedReportOrigin.User ? 0 : 1)
                .ThenByDescending(report => report.ModifiedUtc)
                .FirstOrDefault();
            if (defaultPrimary is not null)
            {
                try
                {
                    snapshot.DefaultState = JsonSerializer.Deserialize<ReportState>(
                        defaultPrimary.StateJson,
                        IrJson.Options) ?? throw new JsonException("state is null");
                }
                catch (JsonException ex)
                {
                    throw new InvalidOperationException(
                        $"Report '{snapshot.Name}': primary Default report '{defaultPrimary.Id}' has an invalid state document.",
                        ex);
                }
            }
        }
        return snapshot;
    }

    private static ReportDefinition Snapshot(string name, ReportDefinition source)
    {
        // OptionsMonitor owns and may replace its object graph. Returning a detached
        // snapshot prevents request code from mutating configuration or observing a
        // half-reloaded nested definition.
        var snapshot = JsonSerializer.Deserialize<ReportDefinition>(
            JsonSerializer.Serialize(source, IrJson.Options),
            IrJson.Options) ?? throw new InvalidOperationException($"Report '{name}': definition could not be copied.");
        snapshot.Name = name;
        return snapshot;
    }

    private static void Validate(ReportDefinition def)
    {
        if (string.IsNullOrWhiteSpace(def.Name))
            throw new InvalidOperationException("Report name is required.");
        if (def.Name.StartsWith("__", StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Report '{def.Name}': names beginning with '__' are reserved for built-in reports.");
        if (def.Authorization is { AllowAnonymous: true, AdministratorsOnly: true })
            throw new InvalidOperationException(
                $"Report '{def.Name}': authorization cannot be both allowAnonymous and administratorsOnly.");
        if (string.IsNullOrWhiteSpace(def.Sql))
            throw new InvalidOperationException($"Report '{def.Name}': sql is required.");
        if (string.IsNullOrWhiteSpace(def.Connection))
            throw new InvalidOperationException($"Report '{def.Name}': connection is required.");

        if (def.MaxRows is < 1 or int.MaxValue)
            throw new InvalidOperationException(
                $"Report '{def.Name}': maxRows must be between 1 and {int.MaxValue - 1}.");
        if (def.MaxPageSize < 1)
            throw new InvalidOperationException($"Report '{def.Name}': maxPageSize must be at least 1.");
        if (def.DefaultPageSize < 1 || def.DefaultPageSize > def.MaxPageSize)
            throw new InvalidOperationException(
                $"Report '{def.Name}': defaultPageSize must be between 1 and maxPageSize ({def.MaxPageSize}).");
        if (def.MaxPageSize > def.MaxRows)
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
        // The base SELECT becomes a derived table; a trailing ORDER BY breaks that on
        // SQL Server (APEX imposes the same rule). Heuristic: an ORDER BY after the last
        // closing paren is top-level.
        var sql = def.Sql.TrimEnd().TrimEnd(';');
        var lastOrderBy = OrderByPattern().Matches(sql).LastOrDefault()?.Index ?? -1;
        if (lastOrderBy >= 0 && lastOrderBy > sql.LastIndexOf(')'))
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
            // Unknown column names stay tolerated (schema drift), but a blank or
            // case-colliding entry is a config mistake worth failing fast on.
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

        if (def.StyleSheet is not null)
        {
            if (string.IsNullOrWhiteSpace(def.StyleSheet))
                throw new InvalidOperationException(
                    $"Report '{def.Name}': styleSheet must not be blank.");
            if (!Uri.TryCreate(def.StyleSheet.Trim(), UriKind.RelativeOrAbsolute, out var styleSheet))
                throw new InvalidOperationException(
                    $"Report '{def.Name}': styleSheet must be a valid relative or absolute URL.");
            if (styleSheet.IsAbsoluteUri
                && !string.Equals(styleSheet.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(styleSheet.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Report '{def.Name}': styleSheet absolute URLs must use http or https.");
        }

        ValidateEditLink(def);
        ValidateColumnOverrides(def);
    }

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
        // Same rule as styleSheet, probed with placeholders neutralized: relative URLs
        // (the primary case) always pass, and substituted values cannot introduce a
        // scheme because the client URL-encodes them.
        var probe = EditLinkTemplate.Rewrite(editLink.UrlTemplate, _ => "x").Replace("{", "").Replace("}", "");
        if (Uri.TryCreate(probe, UriKind.RelativeOrAbsolute, out var probeUri)
            && probeUri.IsAbsoluteUri
            && !string.Equals(probeUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(probeUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Report '{def.Name}': editLink.urlTemplate absolute URLs must use http or https.");

        if (editLink.Label is not null && string.IsNullOrWhiteSpace(editLink.Label))
            throw new InvalidOperationException(
                $"Report '{def.Name}': editLink.label must not be blank.");
        if (editLink.Label is { Length: > 200 })
            throw new InvalidOperationException(
                $"Report '{def.Name}': editLink.label must be at most 200 characters.");
        if (editLink.Target is not null
            && !string.Equals(editLink.Target, "_self", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(editLink.Target, "_blank", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Report '{def.Name}': editLink.target must be '_self' or '_blank'.");
    }

    private static void ValidateColumnOverrides(ReportDefinition def)
    {
        if (def.Columns is null) return;

        // Unknown column names stay tolerated (schema drift, same as columnLabels);
        // blank keys, case collisions, and double-configured labels fail fast.
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

        // A definition contradicting itself is a config mistake, not saved-state drift:
        // the default view must not sort or break on a column the definition locks.
        // (Default-state filters are expressions needing the schema; they degrade into
        // ignored[] at query time instead.)
        var restricted = new HashSet<string>(
            def.Columns.Where(e => e.Value?.Sortable == false).Select(e => e.Key),
            StringComparer.OrdinalIgnoreCase);
        if (restricted.Count == 0) return;
        var source = def.DefaultState?.Pipeline is { Count: > 0 } pipeline
            && string.Equals((pipeline[0].Shape?.Kind ?? "source").Trim(), "source", StringComparison.OrdinalIgnoreCase)
                ? pipeline[0].Layer
                : null;
        var sorted = source?.Sorts?.FirstOrDefault(s => restricted.Contains(s.Col));
        if (sorted is not null)
            throw new InvalidOperationException(
                $"Report '{def.Name}': defaultState sorts on '{sorted.Col}' but columns['{sorted.Col}'] is not sortable.");
        var broken = source?.Breaks?.FirstOrDefault(restricted.Contains);
        if (broken is not null)
            throw new InvalidOperationException(
                $"Report '{def.Name}': defaultState breaks on '{broken}' but columns['{broken}'] is not sortable (control breaks imply sorting).");
    }

    [GeneratedRegex(@"\bORDER\s+BY\b", RegexOptions.IgnoreCase)]
    private static partial Regex OrderByPattern();

    [GeneratedRegex(@"^p\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex ReservedParamPattern();

    public void Dispose() => _reloadSubscription?.Dispose();
}
