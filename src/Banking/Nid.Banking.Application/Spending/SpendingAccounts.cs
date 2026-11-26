using Nid.Banking.Contracts;
using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Banking.Application.Spending;

/// <summary>
/// #85 decision 2: the bank accounts whose lines a spending figure counts for the actor. « Foyer » (<see cref="VisibilityScope.Household"/>):
/// the accounts on a shared envelope, and those on no envelope that every adult reads. « Moi »: every account the actor reads, less (for
/// <see cref="VisibilityScope.Mine"/>) those on an envelope only dependents own. A card follows its account; an ignored account never counts.
/// </summary>
public sealed class SpendingAccounts(BankingAccess access, IAccountStore accounts, IVisibleItems visible, ITransactionStore lines)
{
    public async Task<IReadOnlyList<Guid>> ForAsync(VisibilityScope scope, CancellationToken ct)
        => [.. (await AccountsAsync(scope, ct)).Select(a => a.Id)];

    public async Task<IReadOnlyList<BankAccount>> AccountsAsync(VisibilityScope scope, CancellationToken ct)
        => (await ReadAsync(scope, ct)).Counted;

    /// <summary>
    /// The scope's accounts, and (#720) <paramref name="Unattached"/>: the cards the actor may write, attached to no account and not
    /// ignored, that the scope's figures leave out.
    /// </summary>
    public async Task<(IReadOnlyList<BankAccount> Counted, IReadOnlyList<BankAccount> Unattached)> ReadAsync(VisibilityScope scope, CancellationToken ct)
    {
        var all = (await access.ReadableAsync(await accounts.AllAsync(ct), ct)).Where(r => !r.Account.Ignored).ToList();
        var readable = all.Select(r => r.Account).ToList();
        // Archived envelopes too: their accounts' past months still count.
        var envelopes = await visible.ForAsync(access.Person, scope, includeArchived: true, ct);
        var adults = await access.AdultCountAsync(ct);
        var kept = new HashSet<Guid>();
        foreach (var account in readable.Where(a => a.CardOf is null))
            if (account.Envelope is { } envelope
                    ? envelopes.Contains(envelope)
                    : scope != VisibilityScope.Household || await access.SharedAsync(account, adults, ct))
                kept.Add(account.Id);
        return ([.. readable.Where(a => kept.Contains(a.CardOf ?? a.Id))],
            [.. all.Where(r => r.CanWrite && r.Account is { Kind: BankAccountKind.Card, CardOf: null } && !kept.Contains(r.Account.Id)).Select(r => r.Account)]);
    }

    /// <summary>#720: of <paramref name="cards"/>, those with a line booked within [<paramref name="from"/>, <paramref name="to"/>], as options by name.</summary>
    public async Task<IReadOnlyList<Operations.OptionView>> WithLinesAsync(IReadOnlyList<BankAccount> cards, LocalDate from, LocalDate to, CancellationToken ct)
    {
        var shown = new List<Operations.OptionView>();
        foreach (var card in cards)
            if ((await lines.ListAsync([card.Id], from, to, 0, 1, ct)).Count > 0)
                shown.Add(new Operations.OptionView(card.Id, card.Name));
        return [.. shown.OrderBy(o => o.Name, GetSpendingHandler.ByName)];
    }
}
