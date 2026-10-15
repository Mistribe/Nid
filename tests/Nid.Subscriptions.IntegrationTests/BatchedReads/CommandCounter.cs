using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Nid.Subscriptions.IntegrationTests.BatchedReads;

/// <summary>
/// Batched reads spec §4: counts the SQL commands the EF Core contexts of one host execute. A test registers its own
/// instance on its own host (<see cref="CommandCountTests"/>), so no other test's commands reach it.
/// </summary>
internal sealed class CommandCounter : DbCommandInterceptor
{
    private int count;

    public int Count => Volatile.Read(ref count);

    public void Reset() => Interlocked.Exchange(ref count, 0);

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        => Counted(result);

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Counted(result));

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        => Counted(result);

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Counted(result));

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        => Counted(result);

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Counted(result));

    private T Counted<T>(T result)
    {
        Interlocked.Increment(ref count);
        return result;
    }
}
