using System.Globalization;
using Nid.Banking.Contracts;
using Nid.Core.Contracts;
using Nid.Documents.Contracts;
using NodaTime.Text;

namespace Nid.Banking.Application.Operations;

/// <summary>
/// #84: a bank line as a document target (a receipt), anchored on its account's envelope, a card's on its account's: that envelope's readers read
/// the receipt. An account on no envelope gives no anchor: the receipt is then its uploader's alone, while they write the account.
/// </summary>
public sealed class BankingDocumentTargets(ITransactionStore transactions, IAccountStore accounts, BankingAccess access) : IDocumentTargets
{
    public const string Kind = "banking.transaction";

    private static readonly LocalDatePattern Day = LocalDatePattern.Create("dd/MM/yyyy", CultureInfo.InvariantCulture);
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    /// <summary>As the household reads it, like the calendar feed: <c>-100,00 €</c>, another currency by its code.</summary>
    private static string Money(decimal amount, string currency) => $"{amount.ToString("#,##0.00", French)} {(currency == "EUR" ? "€" : currency)}";

    public IReadOnlySet<string> Kinds { get; } = new HashSet<string> { Kind };

    public async Task<IReadOnlyDictionary<Guid, ResolvedTarget>> ResolveAsync(string kind, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (kind != Kind || ids.Count == 0)
            return new Dictionary<Guid, ResolvedTarget>();
        var lines = await transactions.FindManyAsync(ids, ct);
        var all = (await accounts.AllAsync(ct)).ToDictionary(a => a.Id);
        return lines.ToDictionary(t => t.Id, t => new ResolvedTarget(Anchor(all[t.Account], all),
            $"{t.Label} · {Day.Format(t.BookedOn)} · {Money(t.Amount, t.Currency.Code)}"));
    }

    public async Task<IReadOnlySet<Guid>> WritableUnanchoredAsync(string kind, IReadOnlyCollection<Guid> ids, PersonId actor, CancellationToken ct)
    {
        if (kind != Kind || ids.Count == 0)
            return new HashSet<Guid>();
        var lines = await transactions.FindManyAsync(ids, ct);
        var all = (await accounts.AllAsync(ct)).ToDictionary(a => a.Id);
        var writes = new HashSet<Guid>();
        foreach (var account in lines.Select(l => l.Account).Distinct())
            if ((await access.ReadersAsync(all[account], ct, AccessKind.Write)).Contains(actor))
                writes.Add(account);
        return lines.Where(l => writes.Contains(l.Account)).Select(l => l.Id).ToHashSet();
    }

    /// <summary>The envelope a line of <paramref name="account"/> counts through: its own, a card's account's, or none.</summary>
    public static Guid? Anchor(BankAccount account, IReadOnlyDictionary<Guid, BankAccount> all)
        => (account.CardOf is { } parent && all.TryGetValue(parent, out var found) ? found : account).Envelope?.Value;
}

/// <summary>
/// #84: re-anchors the receipts of an account's lines, and of its cards' lines, on the envelope they now count through, in the caller's
/// transaction: the envelope's former readers stop reading them with the lines.
/// </summary>
public sealed class ReceiptAnchors(ITransactionStore transactions, IAccountStore accounts, IDocumentLinks documents)
{
    /// <summary>After a write that may change what <paramref name="account"/> counts through: its envelope, or the account a card is attached to.</summary>
    public async Task FollowAsync(Guid account, CancellationToken ct)
    {
        var all = (await accounts.AllAsync(ct)).ToDictionary(a => a.Id);
        if (!all.TryGetValue(account, out var found))
            return;
        // ponytail: every line id of the account, a household's few thousand; ask Documents for the linked ones first if accounts grow far past that.
        var lines = await transactions.IdsOfAsync([account, .. all.Values.Where(c => c.CardOf == account).Select(c => c.Id)], ct);
        if (lines.Count > 0)
            await documents.ReanchorTargetsAsync(BankingDocumentTargets.Kind, lines, BankingDocumentTargets.Anchor(found, all), ct);
    }
}
