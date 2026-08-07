using System.Text.RegularExpressions;
using System.Text.Json;
using InteractiveReport.Core.Definitions;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;
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
    private readonly ConfiguredReportDocumentStore? _documents;
    private readonly IDisposable? _reloadSubscription;

    internal ConfigurationReportDefinitionStore(IOptionsMonitor<InteractiveReportOptions> options, SchemaCache schemaCache)
        : this(options, schemaCache, documents: null!)
    {
    }

    public ConfigurationReportDefinitionStore(
        IOptionsMonitor<InteractiveReportOptions> options,
        SchemaCache schemaCache,
        ConfiguredReportDocumentStore documents)
    {
        _options = options;
        _documents = documents;
        _reloadSubscription = options.OnChange(_ => schemaCache.Clear());
    }

    public ValueTask<ReportDefinition?> Find(string name, CancellationToken ct = default)
    {
        if (!_options.CurrentValue.Reports.TryGetValue(name, out var def))
            return ValueTask.FromResult<ReportDefinition?>(null);

        var snapshot = Snapshot(name, def);
        Validate(snapshot);
        var primary = _documents?.List(snapshot).SingleOrDefault(document => document.Primary);
        if (primary is not null)
            snapshot.DefaultState = primary.State;
        return ValueTask.FromResult<ReportDefinition?>(snapshot);
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
    }

    [GeneratedRegex(@"\bORDER\s+BY\b", RegexOptions.IgnoreCase)]
    private static partial Regex OrderByPattern();

    [GeneratedRegex(@"^p\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex ReservedParamPattern();

    public void Dispose() => _reloadSubscription?.Dispose();
}
