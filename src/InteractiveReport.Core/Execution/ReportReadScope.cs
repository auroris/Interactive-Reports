using System.Data.Common;
using Microsoft.Extensions.Logging;

namespace InteractiveReport.Core.Execution;

/// <summary>
/// Provides a provider-neutral lifecycle for one logical multi-query read. <see cref="Transaction"/> is the
/// ADO.NET transaction commands must enlist in when the provider represents the scope
/// that way.
/// </summary>
internal abstract class ReportReadScope : IAsyncDisposable
{
    /// <summary>Gets the no-op scope used when a definition requests independent statements.</summary>
    public static ReportReadScope None { get; } = new NoReadScope();

    /// <summary>Gets the transaction to assign to commands, or <see langword="null"/> when no transaction is required.</summary>
    public virtual DbTransaction? Transaction => null;

    /// <summary>
    /// Ends the read scope after all related queries succeed.
    /// </summary>
    /// <param name="ct">Signals that the operation should be canceled.</param>
    /// <returns>A task that completes when the provider scope has ended.</returns>
    public abstract Task CompleteAsync(CancellationToken ct);

    /// <summary>
    /// Ends an incomplete scope if necessary and releases its provider resources.
    /// </summary>
    /// <returns>A task that completes when all owned resources are released.</returns>
    public abstract ValueTask DisposeAsync();

    /// <summary>
    /// Creates a read scope that owns the supplied transaction and commits or rolls it back on disposal.
    /// </summary>
    /// <param name="transaction">The transaction that keeps related database reads consistent.</param>
    /// <param name="logger">The host-provided logger that receives diagnostic events; <see langword="null"/> disables logging.</param>
    /// <returns>A scope that exposes, rolls back, and disposes <paramref name="transaction"/>.</returns>
    public static ReportReadScope FromTransaction(DbTransaction transaction, ILogger? logger)
        => new TransactionReadScope(transaction, logger);

    private sealed class NoReadScope : ReportReadScope
    {
        /// <summary>
        /// Completes immediately because no provider scope exists.
        /// </summary>
        /// <param name="ct">Signals that the operation should be canceled.</param>
        /// <returns>A completed task.</returns>
        public override Task CompleteAsync(CancellationToken ct) => Task.CompletedTask;

        /// <summary>
        /// Completes immediately because the scope owns no resources.
        /// </summary>
        /// <returns>A completed task.</returns>
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TransactionReadScope(DbTransaction transaction, ILogger? logger) : ReportReadScope
    {
        private bool _completed;

        /// <summary>Gets the transaction that every command in this logical read must use.</summary>
        public override DbTransaction Transaction => transaction;

        /// <summary>
        /// Ends the read-only transaction with rollback and marks the scope complete.
        /// </summary>
        /// <param name="ct">Signals that the operation should be canceled.</param>
        /// <returns>A task that completes after rollback succeeds.</returns>
        /// <remarks>Rolls back <see cref="Transaction"/> and records completion so disposal does not repeat it.</remarks>
        public override async Task CompleteAsync(CancellationToken ct)
        {
            // Provider constraint: the scope is read-only by contract. Rollback closes it
            // without ever submitting a commit, even if a configured SELECT invokes a provider
            // function with unexpected transactional side effects.
            await transaction.RollbackAsync(ct);
            _completed = true;
        }

        /// <summary>
        /// Rolls back an incomplete transaction, suppresses cleanup failures to debug logging, and disposes it.
        /// </summary>
        /// <returns>A task that completes after cleanup.</returns>
        public override async ValueTask DisposeAsync()
        {
            if (!_completed)
            {
                try
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger?.LogDebug(ex, "Failed to roll back an interrupted report read transaction.");
                }

                try
                {
                    await transaction.DisposeAsync();
                }
                catch (Exception ex)
                {
                    logger?.LogDebug(ex, "Failed to dispose an interrupted report read transaction.");
                }
                return;
            }
            await transaction.DisposeAsync();
        }
    }

}
