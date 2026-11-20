using Nid.Banking.Application.Reconciliation;
using Nid.Banking.Contracts;
using Nid.Banking.Domain;
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
/// a card takes no envelope. <paramref name="Name"/> is the household's own name for it, which whoever writes it sets;
/// <paramref name="ClearName"/> goes back to the bank's. #362: a card's <paramref name="MinPayments"/> a month (1 to 31) and whether
/// <paramref name="CountWithdrawals"/>; <paramref name="ClearMinPayments"/> turns the rule off; whoever writes the card sets them.
/// </summary>
public sealed record UpdateBankAccountCommand(
    Guid Id, Guid? EnvelopeId, bool ClearEnvelope, Guid? CardOfAccountId, bool ClearCard, bool? AutoApplyBalance, bool? Ignored, string? Name = null,
    bool ClearName = false, int? MinPayments = null, bool ClearMinPayments = false, bool? CountWithdrawals = null) : ICommand<Guid>, ITransactional;

/// <summary>
/// #353: what « Régler » shows to recognise an account: its latest lines, and for a card not yet attached, the checking account it most
/// likely belongs to (<see cref="CardParents"/>), among those the actor may attach it to.
/// </summary>
public sealed record BankAccountOverviewQuery(Guid Id) : IQuery<BankAccountOverview>;

/// <param name="Recent">The <see cref="BankAccountOverviewHandler.Recent"/> latest lines, newest first.</param>
public sealed record BankAccountOverview(IReadOnlyList<BankTransactionView> Recent, Guid? SuggestedCardOf);

[RequestHandler<BankAccountOverviewQuery, BankAccountOverview>]
public sealed class BankAccountOverviewHandler(
    BankingAccess access, IAccountStore accounts, ITransactionStore transactions, BankLines lines, IHouseholdCalendar calendar)
    : IRequestHandler<BankAccountOverviewQuery, BankAccountOverview>
{
    public const int Recent = 3;

    /// <summary>How far back the lines that suggest a card's account are read.</summary>
    public const int SuggestionDays = 92;

    public async ValueTask<Result<BankAccountOverview>> HandleAsync(BankAccountOverviewQuery query, CancellationToken ct = default)
    {
        var enabled = await access.EnabledAsync(ct);
        if (!enabled.IsSuccess)
            return BankingAccess.Failed<BankAccountOverview>(enabled);
        var found = await access.AccountAsync(query.Id, AccessKind.Read, ct);
        if (!found.TryGetValue(out var account))
            return BankingAccess.Failed<BankAccountOverview, BankAccount>(found);

        var recent = (await lines.PageAsync([account!.Id], "all", null, null, 1, ct)).Items.Take(Recent).ToList();
        return Result.Success(new BankAccountOverview(recent, account.Kind == BankAccountKind.Card && account.CardOf is null ? await SuggestAsync(account, ct) : null));
    }

    private async Task<Guid?> SuggestAsync(BankAccount card, CancellationToken ct)
    {
        var since = (await calendar.TodayAsync(ct)).PlusDays(-SuggestionDays);
        async Task<IReadOnlyList<Line>> LinesOf(Guid id)
            => [.. (await transactions.ListAsync([id], since, null, 0, int.MaxValue, ct)).Select(TransactionLinker.LineOf)];
        var connections = (await accounts.SourcesAsync(card.Id, ct)).Select(s => s.Connection).ToHashSet();
        var candidates = new List<(Guid, bool, IReadOnlyList<Line>)>();
        // Attaching takes writing the account: only those are offered.
        foreach (var (checking, canWrite) in await access.ReadableAsync(await accounts.AllAsync(ct), ct))
            if (checking.Kind == BankAccountKind.Checking && canWrite)
                candidates.Add((checking.Id, (await accounts.SourcesAsync(checking.Id, ct)).Any(s => connections.Contains(s.Connection)), await LinesOf(checking.Id)));
        return CardParents.Suggest(await LinesOf(card.Id), candidates);
    }
}

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
    Operations.ReceiptAnchors receipts, SavingsFlows savingsFlows)
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
        var bankName = account.BankName ?? account.Name;
        if (command.ClearName)
            changed = changed with { HouseholdName = null, Name = bankName };
        else if (command.Name is { } raw)
        {
            var name = raw.Trim();
            if (name.Length is 0 or > BankAccountRules.MaxName)
                return Result.Failure<Guid>(FieldErrors.At(
                    new Failure(BankingCodes.InvalidName, $"A bank account's name is 1 to {BankAccountRules.MaxName} characters, and not only whitespace.", null), "name"));
            // The bank's own name is no renaming: the account keeps following it.
            changed = changed with { HouseholdName = name == bankName ? null : name, Name = name };
        }
        if ((command.MinPayments is not null || command.ClearMinPayments || command.CountWithdrawals is not null) && account.Kind != BankAccountKind.Card)
            return Result.Failure<Guid>(FieldErrors.At(new Failure(BankingCodes.CardRule, "Only a card has a minimum usage.", null), "minPayments"));
        if (command.MinPayments is { } payments and (< 1 or > CardUsage.MaxPayments))
            return Result.Failure<Guid>(FieldErrors.At(
                new Failure(BankingCodes.CardRule, $"A card's minimum usage is 1 to {CardUsage.MaxPayments} payments a month.", null), "minPayments"));
        changed = changed with
        {
            MinPayments = command.ClearMinPayments ? null : command.MinPayments ?? account.MinPayments,
            CountWithdrawals = command.CountWithdrawals ?? account.CountWithdrawals,
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
        if ((await accounts.UpdateAsync(changed, ct)).TryGetFailure(out var failure))
            return Result.Failure<Guid>(failure!);
        if (changed.Envelope != account.Envelope || changed.CardOf != account.CardOf)
            await receipts.FollowAsync(account.Id, ct);
        // #16: newly associated, its lines since the start of the month become the envelope's flows (moved or detached, the old ones stay).
        if (changed.Envelope is not null && changed.Envelope != account.Envelope)
            await savingsFlows.RunAsync([account.Id], access.Person, ct);
        return Result.Success(account.Id);
    }

    private static Result<Guid> NotOwner()
        => Result.Failure<Guid>(new UnauthorizedFailure("Only whose bank reads this account moves it elsewhere.", null));
}

/// <summary>5A's rules on what a bank account goes with (D22), for the API and the <c>nid.data</c> import alike: the refusal, at the API's field, or null.</summary>
public static class BankAccountRules
{
    private static readonly EnvelopeKind[] Associable = [EnvelopeKind.Checking, EnvelopeKind.Savings, EnvelopeKind.Other];

    /// <summary>The longest name the household gives a bank account (the column's check says the same).</summary>
    public const int MaxName = 100;

    /// <summary>A card takes no envelope; any other account a checking, savings or other envelope (<paramref name="envelopeKind"/> null: none) in its currency.</summary>
    public static Failure? Association(BankAccountKind kind, Currency currency, EnvelopeKind? envelopeKind, string? envelopeCurrency)
    {
        // A card's deferred debit is no value of anything (spec failure 7).
        if (kind == BankAccountKind.Card)
            return FieldErrors.At(new Failure(BankingCodes.EnvelopeKind, "A card goes with no envelope: attach it to its checking account.", null), "envelopeId");
        // Its balances could not be valued in any money yet (#355).
        if (currency == Currency.None)
            return FieldErrors.At(new Failure(BankingCodes.CurrencyUnknown, "The bank gave no currency for this account yet: sync it first.", null), "envelopeId");
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
