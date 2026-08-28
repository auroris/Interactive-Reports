using System.Data.Common;
using Microsoft.Extensions.Logging;

namespace InteractiveReport.Core.Execution;

/// <summary>
/// Provider-neutral lifecycle for one logical multi-query read. Transaction is the
/// ADO.NET transaction commands must enlist in when the provider represents the scope
/// that way.
/// </summary>
internal abstract class ReportReadScope : IAsyncDisposable
{
    public static ReportReadScope None { get; } = new NoReadScope();

    public virtual DbTransaction? Transaction => null;

    public abstract Task CompleteAsync(CancellationToken ct);

    public abstract ValueTask DisposeAsync();

    public static ReportReadScope FromTransaction(DbTransaction transaction, ILogger? logger)
        => new TransactionReadScope(transaction, logger);

    private sealed class NoReadScope : ReportReadScope
    {
        public override Task CompleteAsync(CancellationToken ct) => Task.CompletedTask;

        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TransactionReadScope(DbTransaction transaction, ILogger? logger) : ReportReadScope
    {
        private bool _completed;

        public override DbTransaction Transaction => transaction;

        public override async Task CompleteAsync(CancellationToken ct)
        {
            // The scope is read-only by contract. Rollback closes it without ever
            // submitting a commit, even if a configured SELECT invokes a provider
            // function with unexpected transactional side effects.
            await transaction.RollbackAsync(ct);
            _completed = true;
        }

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
