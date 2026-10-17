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
}

/// <summary>Disposing a scope that was not committed rolls everything back.</summary>
public interface ITransactionScope : IAsyncDisposable
{
    Task CommitAsync(CancellationToken ct);
}
