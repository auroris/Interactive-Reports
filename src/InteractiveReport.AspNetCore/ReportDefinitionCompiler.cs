using InteractiveReport.Core.Model;

namespace InteractiveReport.AspNetCore;

/// <summary>
/// Compiles a detached host-supplied definition through the same validation and connection
/// resolution as appsettings. Useful to any dynamic definition store or embedding host.
/// </summary>
public sealed class ReportDefinitionCompiler
{
    private readonly ReportConnectionRegistry connections;
    internal ReportDefinitionCompiler(ReportConnectionRegistry connections) => this.connections = connections;
    public ReportDefinition Compile(string name, ReportDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var snapshot = ConfigurationReportDefinitionStore.Snapshot(name, definition);
        ConfigurationReportDefinitionStore.Validate(snapshot);
        ConfigurationReportDefinitionStore.ResolveConnection(snapshot, connections);
        return snapshot;
    }
}
