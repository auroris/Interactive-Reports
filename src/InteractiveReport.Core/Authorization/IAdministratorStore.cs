using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Authorization;

/// <summary>
/// Persists database-authored administrator grants beside saved reports. A grant is one
/// canonical identity value; identities compare ordinally, exactly as ownership does. The
/// store is the last administrator fallback: the integrating application's own answer and
/// the configured list are consulted before it.
/// </summary>
public interface IAdministratorStore
{
    /// <summary>
    /// Lists every granted identity.
    /// </summary>
    /// <param name="ct">Signals that the operation should be canceled; defaults to <c>default</c>.</param>
    /// <returns>A task containing the granted identities in stable presentation order.</returns>
    Task<IReadOnlyList<string>> List(CancellationToken ct = default);

    /// <summary>
    /// Determines whether an identity holds a grant.
    /// </summary>
    /// <param name="identity">The canonical identity to check.</param>
    /// <param name="ct">Signals that the operation should be canceled; defaults to <c>default</c>.</param>
    /// <returns>A task whose result is <see langword="true"/> when the identity is granted; otherwise, <see langword="false"/>.</returns>
    Task<bool> IsAdministrator(string identity, CancellationToken ct = default);

    /// <summary>
    /// Creates a grant if it does not already exist.
    /// </summary>
    /// <param name="identity">The canonical identity to grant.</param>
    /// <param name="ct">Signals that the operation should be canceled; defaults to <c>default</c>.</param>
    /// <returns>A task that completes after the grant exists in persistence.</returns>
    Task Grant(string identity, CancellationToken ct = default);

    /// <summary>
    /// Removes a grant.
    /// </summary>
    /// <param name="identity">The canonical identity to revoke.</param>
    /// <param name="ct">Signals that the operation should be canceled; defaults to <c>default</c>.</param>
    /// <returns>A task whose result is <see langword="true"/> when a grant was removed; otherwise, <see langword="false"/>.</returns>
    Task<bool> Revoke(string identity, CancellationToken ct = default);
}

/// <summary>Identifies the administrator table on the resolved saved-report connection.</summary>
/// <param name="ConnectionName">The connection registry name.</param>
/// <param name="Dialect">The SQL dialect used by the connection.</param>
/// <param name="AutoCreate">Whether the store may create its table.</param>
/// <param name="TableName">The validated effective table name.</param>
public sealed record AdministratorStoreConfig(
    string ConnectionName,
    ReportDialect Dialect,
    bool AutoCreate = true,
    string TableName = "IR_ADMINISTRATORS");
