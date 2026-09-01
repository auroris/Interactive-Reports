using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Definitions;

/// <summary>
/// Supplies executable report definitions. The built-in implementation is configuration-backed;
/// applications may replace it with a database-backed or otherwise dynamic store without changing
/// the execution engine.
/// </summary>
/// <example>
/// <code><![CDATA[
/// var definition = await store.Find("orders", ct)
///     ?? throw new KeyNotFoundException("Report 'orders' was not found.");
/// ]]></code>
/// </example>
public interface IReportDefinitionStore
{
    /// <summary>
    /// Finds an executable report definition by its case-insensitive name.
    /// </summary>
    /// <param name="name">The report name to resolve.</param>
    /// <param name="ct">Signals that the operation should be canceled; defaults to <c>default</c>.</param>
    /// <returns>A task containing a detached definition, or <see langword="null"/> when no report has that name.</returns>
    ValueTask<ReportDefinition?> Find(string name, CancellationToken ct = default);
}

/// <summary>
/// Optional companion for definition stores that can resolve the small authorization
/// envelope without hydrating, validating, or connecting the executable report.
/// Endpoint authorization uses this first when available, then loads the full
/// definition only for callers that pass the report-level gate.
/// </summary>
public interface IReportDefinitionAuthorizationStore
{
    /// <summary>
    /// Finds the authorization envelope for a report without loading its executable definition.
    /// </summary>
    /// <param name="name">The report name to resolve.</param>
    /// <param name="ct">Signals that the operation should be canceled; defaults to <c>default</c>.</param>
    /// <returns>A task containing the authorization envelope, or <see langword="null"/> when no report has that name.</returns>
    ValueTask<ReportDefinitionAuthorization?> FindAuthorization(
        string name,
        CancellationToken ct = default);
}

/// <summary>Contains the report metadata required by the report-level authorization gate.</summary>
/// <param name="Name">The canonical configured report name.</param>
/// <param name="Authorization">The definition's optional authentication and identity restrictions.</param>
public sealed record ReportDefinitionAuthorization(
    string Name,
    ReportAuthorization? Authorization);
