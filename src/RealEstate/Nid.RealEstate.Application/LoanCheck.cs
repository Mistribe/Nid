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

    /// <summary>Spec 3C1 §4: the linked property's name when the reader can read it; null otherwise — the id stays, the loan's reader knows its own link.</summary>
    public static async Task<string?> PropertyNameAsync(IAccessPolicy policy, IItemRegistry items, PersonId reader, ItemId? property, CancellationToken ct)
        => property is { } id && (await policy.CheckAsync(reader, id, AccessKind.Read, ct)).IsSuccess
            ? (await items.FindAsync(id, ct))?.DisplayName
            : null;

    /// <summary>
    /// #180: a loan finances an SCPI envelope its actor reads. An unreadable id, an unknown id and any other item get this one
    /// refusal, as <see cref="PropertyCheck.LinkableAsync"/>.
    /// </summary>
    public static async Task<Failure?> EnvelopeLinkableAsync(
        IFinancedEnvelopes envelopes, IAccessPolicy policy, PersonId actor, ItemId? envelope, CancellationToken ct)
        => envelope is not { } id
           || ((await policy.CheckAsync(actor, id, AccessKind.Read, ct)).IsSuccess && await envelopes.IsScpiAsync(id, ct))
            ? null
            : InvalidEnvelope("This is not an SCPI envelope you can read.");

    /// <summary>#180: a loan finances a property or an SCPI, never both.</summary>
    public static Failure? OneFinanced(ItemId? property, ItemId? envelope)
        => property is not null && envelope is not null ? InvalidEnvelope("A loan finances a property or an SCPI, not both.") : null;

    private static Failure InvalidEnvelope(string message)
        => new(LoanCodes.InvalidEnvelope, message, new Dictionary<string, object?> { ["field"] = "envelopeId" });

    public static Failure Conflict(string code, string what)
        => new ConflictFailure($"Someone else changed this {what} since you read it; read it again.") { Code = code };
}
