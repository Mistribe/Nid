using Nid.Banking.Contracts;
using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Banking.Application;

/// <summary>The bank accounts the actor reads (D7, D22); with <paramref name="Envelope"/>, the one associated with it, if any.</summary>
public sealed record ListBankAccountsQuery(ItemId? Envelope) : IQuery<IReadOnlyList<BankAccountView>>;

/// <summary>
/// An account's settings; a null field is left as it is. <paramref name="EnvelopeId"/> associates the account (write on a checking,
/// savings or other envelope in its currency, one account per envelope); <paramref name="ClearEnvelope"/> dissociates it;
/// <paramref name="CardOfAccountId"/> attaches a card to a checking account its author may write; <paramref name="ClearCard"/> detaches it.
/// Moving it (to an envelope or an account) is its owner's alone (<see cref="BankingAccess.OwnsAsync(BankAccount, CancellationToken)"/>);
/// a card takes no envelope.
/// </summary>
public sealed record UpdateBankAccountCommand(
    Guid Id, Guid? EnvelopeId, bool ClearEnvelope, Guid? CardOfAccountId, bool ClearCard, bool? AutoApplyBalance, bool? Ignored) : ICommand<Guid>;

[RequestHandler<ListBankAccountsQuery, IReadOnlyList<BankAccountView>>]
public sealed class ListBankAccountsHandler(BankingAccess access, IAccountStore accounts, BankAccountReader reader)
    : IRequestHandler<ListBankAccountsQuery, IReadOnlyList<BankAccountView>>
{
    public async ValueTask<Result<IReadOnlyList<BankAccountView>>> HandleAsync(ListBankAccountsQuery query, CancellationToken ct = default)
    {
        var enabled = await access.EnabledAsync(ct);
        if (!enabled.IsSuccess)
            return BankingAccess.Failed<IReadOnlyList<BankAccountView>>(enabled);
        var all = await accounts.AllAsync(ct);
        return Result.Success(await reader.ReadAsync(query.Envelope is { } envelope ? all.Where(a => a.Envelope == envelope).ToList() : all, ct));
    }
}

[RequestHandler<UpdateBankAccountCommand, Guid>]
public sealed class UpdateBankAccountHandler(
    BankingAccess access, IAccountStore accounts, IAccessPolicy policy, IEnvelopeStore envelopes, IItemRegistry items, IHouseholdCalendar calendar,
    IUnitOfWork unitOfWork)
    : IRequestHandler<UpdateBankAccountCommand, Guid>
{
    public async ValueTask<Result<Guid>> HandleAsync(UpdateBankAccountCommand command, CancellationToken ct = default)
    {
        var enabled = await access.EnabledAsync(ct);
        if (!enabled.IsSuccess)
            return BankingAccess.Failed<Guid>(enabled);
        var found = await access.AccountAsync(command.Id, AccessKind.Write, ct);
        if (!found.TryGetValue(out var account))
            return BankingAccess.Failed<Guid, BankAccount>(found);

        var changed = account! with
        {
            AutoApplyBalance = command.AutoApplyBalance ?? account.AutoApplyBalance, Ignored = command.Ignored ?? account.Ignored,
        };
        if (command.ClearEnvelope)
            changed = changed with { Envelope = null, AssociatedOn = null, AssociatedBy = null };
        else if (command.EnvelopeId is { } id && new ItemId(id) is var envelope && envelope != account.Envelope)
        {
            // A card is refused before anything is asked of the envelope.
            if (account.Kind == BankAccountKind.Card)
                return Result.Failure<Guid>(BankAccountRules.Association(account.Kind, account.Currency, null, null)!);
            if (!await access.OwnsAsync(account, ct))
                return NotOwner();
            if ((await policy.CheckAsync(access.Person, envelope, AccessKind.Write, ct)).TryGetFailure(out var denied))
                return Result.Failure<Guid>(denied!);
            if (BankAccountRules.Association(account.Kind, account.Currency, (await envelopes.FindAsync(envelope, ct))?.Kind,
                    (await items.FindAsync(envelope, ct))?.Currency) is { } refused)
                return Result.Failure<Guid>(refused);
            changed = changed with { Envelope = envelope, AssociatedOn = await calendar.TodayAsync(ct), AssociatedBy = access.Person };
        }
        if (command.ClearCard)
            changed = changed with { CardOf = null };
        else if (command.CardOfAccountId is { } parent && parent != account.CardOf)
        {
            // The card takes that account's readers: its author must own the card and write that account.
            if (!await access.OwnsAsync(account, ct))
                return NotOwner();
            var attached = await access.AccountAsync(parent, AccessKind.Write, ct);
            if (!attached.TryGetValue(out var parentAccount))
                return BankingAccess.Failed<Guid, BankAccount>(attached);
            if (BankAccountRules.CardParent(account.Kind, parentAccount!.Kind) is { } refused)
                return Result.Failure<Guid>(refused);
            changed = changed with { CardOf = parent };
        }

        // Its own transaction (a savepoint under an approved proposal's): the store's unique violation rolls back to it, not further.
        await using var transaction = await unitOfWork.BeginAsync(ct);
        if ((await accounts.UpdateAsync(changed, ct)).TryGetFailure(out var failure))
            return Result.Failure<Guid>(failure!);
        await transaction.CommitAsync(ct);
        return Result.Success(account.Id);
    }

    private static Result<Guid> NotOwner()
        => Result.Failure<Guid>(new UnauthorizedFailure("Only whose bank reads this account moves it elsewhere.", null));
}

/// <summary>5A's rules on what a bank account goes with (D22), for the API and the <c>nid.data</c> import alike: the refusal, at the API's field, or null.</summary>
public static class BankAccountRules
{
    private static readonly EnvelopeKind[] Associable = [EnvelopeKind.Checking, EnvelopeKind.Savings, EnvelopeKind.Other];

    /// <summary>A card takes no envelope; any other account a checking, savings or other envelope (<paramref name="envelopeKind"/> null: none) in its currency.</summary>
    public static Failure? Association(BankAccountKind kind, Currency currency, EnvelopeKind? envelopeKind, string? envelopeCurrency)
    {
        // A card's deferred debit is no value of anything (spec failure 7).
        if (kind == BankAccountKind.Card)
            return FieldErrors.At(new Failure(BankingCodes.EnvelopeKind, "A card goes with no envelope: attach it to its checking account.", null), "envelopeId");
        if (envelopeKind is not { } target || !Associable.Contains(target))
            return new Failure(BankingCodes.EnvelopeKind, "A bank account goes with a checking, savings or other envelope.", null);
        // Its balances become the envelope's values: they must be in its money.
        return envelopeCurrency != currency.Code
            ? FieldErrors.At(new Failure(BankingCodes.CurrencyMismatch, "The envelope is in another currency than the bank account.", null), "envelopeId")
            : null;
    }

    /// <summary>Only a card is attached, and only to a checking account.</summary>
    public static Failure? CardParent(BankAccountKind card, BankAccountKind parent)
        => card != BankAccountKind.Card || parent != BankAccountKind.Checking
            ? FieldErrors.At(new Failure(BankingCodes.CardParent, "Only a card is attached, and only to a checking account.", null), "cardOfAccountId")
            : null;
}
