using Nid.Core.Contracts;
using Nid.RealEstate.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.RealEstate.Application;

internal static class LoanCheck
{
    /// <summary>The loan and its item; an item that is not a loan answers as a missing loan (spec 3A2 §4).</summary>
    public static async Task<Result<(StoredLoan Loan, ItemAccess Item)>> LoadAsync(
        ILoanStore loans, IItemRegistry items, ItemId id, CancellationToken ct)
    {
        var loan = await loans.FindAsync(id, ct);
        var item = loan is null ? null : await items.FindAsync(id, ct);
        return loan is null || item is null
            ? Result.Failure<(StoredLoan, ItemAccess)>(new NotFoundFailure("loan", id.ToString()))
            : Result.Success((loan, item));
    }

    /// <summary>Precision 5: a write locks the loan's row for its transaction, then reads the loan as it now is.</summary>
    public static async Task<Result<(StoredLoan Loan, ItemAccess Item)>> LockAsync(
        ILoanStore loans, IItemRegistry items, ItemId id, CancellationToken ct)
    {
        await loans.LockAsync(id, ct);
        return await LoadAsync(loans, items, id, ct);
    }

    public static Failure Conflict(string code, string what)
        => new(code, $"Someone else changed this {what} since you read it; read it again.", null);
}
