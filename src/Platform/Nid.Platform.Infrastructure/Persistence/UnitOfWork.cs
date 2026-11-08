using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Nid.Platform.Contracts;

namespace Nid.Platform.Infrastructure.Persistence;

/// <summary>A context that joins the unit of work's transaction.</summary>
public interface ITransactionParticipant
{
    Task EnlistAsync(DbTransaction? transaction, CancellationToken ct);
}

public sealed class DbContextParticipant<TContext>(TContext context) : ITransactionParticipant
    where TContext : DbContext
{
    public Task EnlistAsync(DbTransaction? transaction, CancellationToken ct)
        => context.Database.UseTransactionAsync(transaction, ct);
}

public sealed class UnitOfWork(SharedConnection shared, IEnumerable<ITransactionParticipant> participants) : IUnitOfWork
{
    private DbTransaction? open;
    private int depth;
    // An inner scope could not roll back to its savepoint: its writes may still be in the transaction, so it must not commit.
    private bool poisoned;

    public async Task<ITransactionScope> BeginAsync(CancellationToken ct)
    {
        if (open is not null)
            return await JoinAsync(open, ct);

        if (shared.Connection.State != ConnectionState.Open)
            await shared.Connection.OpenAsync(ct);

        var transaction = await shared.Connection.BeginTransactionAsync(ct);
        open = transaction;
        var enlisted = new List<ITransactionParticipant>();
        try
        {
            foreach (var participant in participants)
            {
                await participant.EnlistAsync(transaction, ct);
                enlisted.Add(participant);
            }
        }
        catch
        {
            // Nothing half-begun survives a failed enlistment: the contexts already enlisted let go of the
            // transaction, which is rolled back and disposed, and the next BeginAsync starts clean. Cleanup
            // failures are swallowed so the enlistment's own exception is the one that surfaces.
            foreach (var participant in enlisted)
            {
                try { await participant.EnlistAsync(null, CancellationToken.None); } catch { /* best effort */ }
            }

            try { await transaction.RollbackAsync(CancellationToken.None); } catch { /* best effort */ }
            await transaction.DisposeAsync();
            open = null;
            throw;
        }

        return new Scope(this, transaction, enlisted);
    }

    /// <summary>
    /// Spec 4D1 §5: a command run inside another's unit of work (an approval, a direct write) joins it under a savepoint.
    /// Committing releases the savepoint; disposing without a commit rolls back to it; the outer scope decides what lasts.
    /// </summary>
    private async Task<ITransactionScope> JoinAsync(DbTransaction transaction, CancellationToken ct)
    {
        var name = $"uow_{depth + 1}";
        await transaction.SaveAsync(name, ct);
        depth++;
        return new Savepoint(this, transaction, name);
    }

    // ponytail: rolling back to a savepoint leaves the rows saved under it tracked by the contexts; every caller today then
    // fails its outer unit of work too. Detach them here if a caller ever goes on after an inner failure.
    private sealed class Savepoint(UnitOfWork owner, DbTransaction transaction, string name) : ITransactionScope
    {
        private bool released;
        private bool disposed;

        public async Task CommitAsync(CancellationToken ct)
        {
            await transaction.ReleaseAsync(name, ct);
            released = true;
        }

        public async ValueTask DisposeAsync()
        {
            // A second rollback to the savepoint would undo what the outer scope wrote since; the depth would go negative.
            if (disposed)
                return;
            disposed = true;
            try
            {
                if (!released)
                {
                    try
                    {
                        await transaction.RollbackAsync(name);
                    }
                    catch
                    {
                        // The exception already unwinding is the one to surface; the outer commit is refused instead.
                        owner.poisoned = true;
                    }
                }
            }
            finally
            {
                owner.depth--;
            }
        }
    }

    private sealed class Scope(UnitOfWork owner, DbTransaction transaction, IReadOnlyList<ITransactionParticipant> enlisted)
        : ITransactionScope
    {
        private bool committed;
        private bool commitAttempted;

        public async Task CommitAsync(CancellationToken ct)
        {
            // Set before awaiting: a serialization failure, a deferred constraint, or a dropped connection can
            // all make this throw, but Npgsql still marks the transaction completed the moment a commit is
            // attempted — DisposeAsync must know not to roll back, whether the attempt succeeded or not.
            if (owner.poisoned)
                throw new InvalidOperationException("An inner unit of work could not roll back to its savepoint; nothing is committed.");
            commitAttempted = true;
            await transaction.CommitAsync(ct);
            committed = true;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                // Rolling back after a commit was attempted throws InvalidOperationException ("This
                // NpgsqlTransaction has completed"), which would replace whatever CommitAsync itself just
                // threw. Skip the rollback whenever a commit was attempted, committed or not.
                if (!committed && !commitAttempted)
                {
                    try
                    {
                        await transaction.RollbackAsync();
                    }
                    catch
                    {
                        // Best-effort backstop: a rollback can also fail on its own (a dropped connection),
                        // and that failure must never replace the exception already unwinding the stack from
                        // whatever this scope wrapped. Nothing else in this method is caught this broadly.
                    }
                }
            }
            finally
            {
                foreach (var participant in enlisted)
                    await participant.EnlistAsync(null, CancellationToken.None);
                await transaction.DisposeAsync();
                owner.open = null;
                owner.poisoned = false;
            }
        }
    }
}
