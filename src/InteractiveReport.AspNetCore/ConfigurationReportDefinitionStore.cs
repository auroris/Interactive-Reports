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
    private readonly IDisposable? _reloadSubscription;

    public ConfigurationReportDefinitionStore(IOptionsMonitor<InteractiveReportOptions> options, SchemaCache schemaCache)
    {
        _options = options;
        _reloadSubscription = options.OnChange(_ => schemaCache.Clear());
    }

    public ValueTask<ReportDefinition?> Find(string name, CancellationToken ct = default)
    {
        if (!_options.CurrentValue.Reports.TryGetValue(name, out var def))
            return ValueTask.FromResult<ReportDefinition?>(null);

        var snapshot = Snapshot(name, def);
        Validate(snapshot);
        return ValueTask.FromResult<ReportDefinition?>(snapshot);
    }

    public ValueTask<IReadOnlyList<ReportDefinition>> List(CancellationToken ct = default)
    {
        var result = new List<ReportDefinition>();
        foreach (var (name, def) in _options.CurrentValue.Reports)
        {
            var snapshot = Snapshot(name, def);
            Validate(snapshot);
            result.Add(snapshot);
        }
        return ValueTask.FromResult<IReadOnlyList<ReportDefinition>>(result);
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
    }

    [GeneratedRegex(@"\bORDER\s+BY\b", RegexOptions.IgnoreCase)]
    private static partial Regex OrderByPattern();

    [GeneratedRegex(@"^p\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex ReservedParamPattern();

    public void Dispose() => _reloadSubscription?.Dispose();
}
