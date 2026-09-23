namespace Nid.Platform.Contracts;

/// <summary>
/// One database transaction spanning every module's context in the current request. Stores keep calling
/// their own <c>SaveChanges</c>; inside a scope those writes all land in the same transaction.
/// </summary>
public interface IUnitOfWork
{
    /// <summary>Opens the transaction. A second call while one is open throws: nesting is not supported.</summary>
    Task<ITransactionScope> BeginAsync(CancellationToken ct);
}

/// <summary>Disposing a scope that was not committed rolls everything back.</summary>
public interface ITransactionScope : IAsyncDisposable
{
    Task CommitAsync(CancellationToken ct);
}
