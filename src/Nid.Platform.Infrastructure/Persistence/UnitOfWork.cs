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
    private bool active;

    public async Task<ITransactionScope> BeginAsync(CancellationToken ct)
    {
        if (active)
            throw new InvalidOperationException("A unit of work is already open in this request; nesting is not supported.");

        if (shared.Connection.State != ConnectionState.Open)
            await shared.Connection.OpenAsync(ct);

        var transaction = await shared.Connection.BeginTransactionAsync(ct);
        active = true;
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
            active = false;
            throw;
        }

        return new Scope(this, transaction, enlisted);
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
                owner.active = false;
            }
        }
    }
}
