using System.Globalization;
using Nid.Banking.Contracts;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Banking.Application;

/// <summary>
/// The booked transactions of an envelope's bank account (<paramref name="ItemId"/> is the envelope) and of the cards attached to it (spec §6), newest first, 50 a page
/// (<paramref name="Page"/> counts from 1); whoever reads the envelope reads them, the pipeline checks it.
/// </summary>
public sealed record ListBankTransactionsQuery(ItemId ItemId, LocalDate? From, LocalDate? To, int Page) : IQuery<BankTransactionPage>, IItemScoped
{
    public AccessKind Access => AccessKind.Read;
}

public sealed record BankTransactionPage(IReadOnlyList<BankTransactionView> Items, bool HasMore);

/// <summary><paramref name="Amount"/> is signed (a debit is negative); <paramref name="CardName"/> names the card of a <paramref name="FromCard"/> line.</summary>
public sealed record BankTransactionView(
    Guid Id, LocalDate BookedOn, LocalDate? ValueOn, string Amount, string Currency, string Label, string? Counterparty, bool FromCard,
    string? CardName, string FetchedVia);

[RequestHandler<ListBankTransactionsQuery, BankTransactionPage>]
public sealed class ListBankTransactionsHandler(BankingAccess access, IAccountStore accounts, ITransactionStore transactions)
    : IRequestHandler<ListBankTransactionsQuery, BankTransactionPage>
{
    public const int PageSize = 50;

    /// <summary>Half a million lines: a page past it would overflow the offset.</summary>
    public const int MaxPage = 10_000;

    public async ValueTask<Result<BankTransactionPage>> HandleAsync(ListBankTransactionsQuery query, CancellationToken ct = default)
    {
        var enabled = await access.EnabledAsync(ct);
        if (!enabled.IsSuccess)
            return BankingAccess.Failed<BankTransactionPage>(enabled);
        if (await accounts.ForEnvelopeAsync(query.ItemId, ct) is not { } account)
            return Result.Success(new BankTransactionPage([], false));

        var cards = (await accounts.AllAsync(ct)).Where(a => a.CardOf == account.Id).ToDictionary(a => a.Id, a => a.Name);
        // One more than a page tells whether another follows.
        var lines = await transactions.ListAsync([account.Id, .. cards.Keys], query.From, query.To, (Math.Clamp(query.Page, 1, MaxPage) - 1) * PageSize, PageSize + 1, ct);
        return Result.Success(new BankTransactionPage(
            [.. lines.Take(PageSize).Select(t => new BankTransactionView(
                t.Id, t.BookedOn, t.ValueOn, t.Amount.ToString("0.00##", CultureInfo.InvariantCulture), t.Currency.Code, t.Label, t.Counterparty,
                t.Account != account.Id, cards.GetValueOrDefault(t.Account), t.FetchedVia))],
            lines.Count > PageSize));
    }
}
