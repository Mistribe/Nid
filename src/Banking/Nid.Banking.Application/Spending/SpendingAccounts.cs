using Nid.Banking.Contracts;
using Nid.Core.Contracts;

namespace Nid.Banking.Application.Spending;

/// <summary>
/// #85 decision 2: the bank accounts whose lines a spending figure counts for the actor. « Foyer » (<see cref="VisibilityScope.Household"/>):
/// the accounts on a shared envelope, and those on no envelope that every adult reads. « Moi »: every account the actor reads, less (for
/// <see cref="VisibilityScope.Mine"/>) those on an envelope only dependents own. A card follows its account; an ignored account never counts.
/// </summary>
public sealed class SpendingAccounts(BankingAccess access, IAccountStore accounts, IVisibleItems visible)
{
    public async Task<IReadOnlyList<Guid>> ForAsync(VisibilityScope scope, CancellationToken ct)
        => [.. (await AccountsAsync(scope, ct)).Select(a => a.Id)];

    public async Task<IReadOnlyList<BankAccount>> AccountsAsync(VisibilityScope scope, CancellationToken ct)
    {
        var readable = (await access.ReadableAsync(await accounts.AllAsync(ct), ct)).Select(r => r.Account).Where(a => !a.Ignored).ToList();
        // Archived envelopes too: their accounts' past months still count.
        var envelopes = await visible.ForAsync(access.Person, scope, includeArchived: true, ct);
        var adults = await access.AdultCountAsync(ct);
        var kept = new HashSet<Guid>();
        foreach (var account in readable.Where(a => a.CardOf is null))
            if (account.Envelope is { } envelope
                    ? envelopes.Contains(envelope)
                    : scope != VisibilityScope.Household || await access.SharedAsync(account, adults, ct))
                kept.Add(account.Id);
        return [.. readable.Where(a => kept.Contains(a.CardOf ?? a.Id))];
    }
}
