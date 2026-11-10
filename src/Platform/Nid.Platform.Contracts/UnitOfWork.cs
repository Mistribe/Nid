namespace Nid.Platform.Contracts;

/// <summary>
/// One database transaction spanning every module's context in the current request. Stores keep calling
/// their own <c>SaveChanges</c>; inside a scope those writes all land in the same transaction.
/// </summary>
public interface IUnitOfWork
{
    /// <summary>
    /// Opens the transaction or, while one is open in this request, a savepoint inside it (spec 4D1 §5): the inner scope's
    /// commit releases the savepoint, its disposal without a commit rolls back to it, and only the outermost commit lasts.
    /// </summary>
    Task<ITransactionScope> BeginAsync(CancellationToken ct);

    /// <summary>
    /// Runs <paramref name="action"/> once the outermost scope has committed, for a side effect outside the transaction (a notice).
    /// Dropped when the transaction, or the savepoint it was registered under, rolls back: a dry run never runs it. The action must not
    /// throw (the commit already happened). Throws when no scope is open.
    /// </summary>
    void AfterCommit(Func<Task> action);
}

/// <summary>Disposing a scope that was not committed rolls everything back.</summary>
public interface ITransactionScope : IAsyncDisposable
{
    Task CommitAsync(CancellationToken ct);
}
