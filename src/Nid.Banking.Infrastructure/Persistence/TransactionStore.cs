using Microsoft.EntityFrameworkCore;
using Nid.Banking.Application;
using Nid.Banking.Contracts;
using Nid.Banking.Domain;
using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Banking.Infrastructure.Persistence;

public sealed class TransactionStore(BankingDbContext db, IClock clock) : ITransactionStore
{
    public async Task<TransactionWrite> AddAsync(Guid account, IReadOnlyList<NewBankTransaction> lines, CancellationToken ct)
    {
        if (lines.Count == 0)
            return new TransactionWrite(0, 0);

        await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        // One writer per account (a nightly read and a file import): the unique keys never see the race.
        await db.Database.ExecuteSqlAsync($"select pg_advisory_xact_lock(hashtextextended({account.ToString()}, 0))", ct);

        var normalized = lines.Select(l => TransactionLabel.Normalize(l.Label)).ToList();
        var fingerprints = TransactionFingerprint.ForBatch([.. lines.Select((l, i) => (l.BookedOn, l.Amount, normalized[i]))]);
        var refs = lines.Select((l, i) => l.ProviderRef ?? fingerprints[i]).ToList();

        var stored = await db.Transactions.AsNoTracking()
            .Where(t => t.AccountId == account && (refs.Contains(t.ProviderRef) || fingerprints.Contains(t.Fingerprint)))
            .Select(t => new { t.ProviderRef, t.Fingerprint, t.FetchedVia })
            .ToListAsync(ct);
        var knownRefs = stored.Select(t => t.ProviderRef).ToHashSet();
        var byFingerprint = stored.ToDictionary(t => t.Fingerprint, t => (t.ProviderRef, t.FetchedVia));

        var now = clock.GetCurrentInstant();
        var added = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            // Within the batch too: a repeated ref or fingerprint is the same line.
            if (knownRefs.Contains(refs[i]))
                continue;

            var l = lines[i];
            var fingerprint = fingerprints[i];
            if (byFingerprint.TryGetValue(fingerprint, out var holder))
            {
                // Another real ref of the same source holds this rank (a read in another order): a new line, at the next free rank.
                if (l.ProviderRef is null || holder.ProviderRef == fingerprint || holder.FetchedVia != l.FetchedVia)
                    continue;

                for (var rank = 2; byFingerprint.ContainsKey(fingerprint) || await db.Transactions.AnyAsync(t => t.AccountId == account && t.Fingerprint == fingerprint, ct); rank++)
                    fingerprint = TransactionFingerprint.Compute(l.BookedOn, l.Amount, normalized[i], rank);
            }

            knownRefs.Add(refs[i]);
            byFingerprint[fingerprint] = (refs[i], l.FetchedVia);
            db.Transactions.Add(new TransactionRow
            {
                Id = Guid.CreateVersion7(), AccountId = account, ProviderRef = refs[i], Fingerprint = fingerprint, BookedOn = l.BookedOn,
                ValueOn = l.ValueOn, Amount = l.Amount, Currency = l.Currency.Code, Label = l.Label, Counterparty = l.Counterparty,
                Remittance = l.Remittance, NormalizedLabel = normalized[i], FetchedVia = l.FetchedVia, CreatedAt = now,
            });
            added++;
        }

        await db.SaveChangesAsync(ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);
        return new TransactionWrite(added, lines.Count - added);
    }

    public async Task<LocalDate?> LatestBookedOnAsync(Guid account, string? fetchedVia, CancellationToken ct)
        => await db.Transactions.Where(t => t.AccountId == account && (fetchedVia == null || t.FetchedVia == fetchedVia)).MaxAsync(t => (LocalDate?)t.BookedOn, ct);

    public Task<bool> HasFetchedViaAsync(Guid account, string fetchedVia, CancellationToken ct)
        => db.Transactions.AnyAsync(t => t.AccountId == account && t.FetchedVia == fetchedVia, ct);

    public async Task<IReadOnlyList<BankTransaction>> NearAsync(Guid account, IReadOnlyList<(LocalDate BookedOn, decimal Amount)> lines, CancellationToken ct)
    {
        if (lines.Count == 0)
            return [];

        var amounts = lines.Select(l => l.Amount).Distinct().ToList();
        var low = lines.Min(l => l.BookedOn).PlusDays(-1);
        var high = lines.Max(l => l.BookedOn).PlusDays(1);
        var candidates = await db.Transactions.AsNoTracking()
            .Where(t => t.AccountId == account && amounts.Contains(t.Amount) && t.BookedOn >= low && t.BookedOn <= high)
            .OrderBy(t => t.BookedOn).ThenBy(t => t.Id)
            .ToListAsync(ct);
        // Hashed: a 20 000-line statement against as many stored lines.
        var asked = lines.ToHashSet();
        return candidates
            .Where(t => asked.Contains((t.BookedOn.PlusDays(-1), t.Amount)) || asked.Contains((t.BookedOn, t.Amount)) || asked.Contains((t.BookedOn.PlusDays(1), t.Amount)))
            .Select(ToModel).ToList();
    }

    public async Task<IReadOnlyList<BankTransaction>> ListAsync(
        IReadOnlyList<Guid> accounts, LocalDate? from, LocalDate? to, int skip, int take, CancellationToken ct)
    {
        var query = db.Transactions.AsNoTracking().Where(t => accounts.Contains(t.AccountId));
        if (from is { } f)
            query = query.Where(t => t.BookedOn >= f);
        if (to is { } u)
            query = query.Where(t => t.BookedOn <= u);
        return (await query.OrderByDescending(t => t.BookedOn).ThenByDescending(t => t.Id).Skip(skip).Take(take).ToListAsync(ct))
            .Select(ToModel).ToList();
    }

    public Task<int> PurgeBookedBeforeAsync(LocalDate before, CancellationToken ct)
        => db.Transactions.Where(t => t.BookedOn < before && (t.LinkState == "unlinked" || t.LinkState == "ignored")).ExecuteDeleteAsync(ct);

    private static BankTransaction ToModel(TransactionRow r) => new(
        r.Id, r.AccountId, r.ProviderRef, r.Fingerprint, r.BookedOn, r.ValueOn, r.Amount,
        Currency.TryCreate(r.Currency, out var currency) ? currency : throw new InvalidOperationException($"Bank transaction {r.Id} has no valid currency."),
        r.Label, r.Counterparty, r.Remittance, r.NormalizedLabel,
        r.LinkState switch
        {
            "linked" => TransactionLinkState.Linked, "ignored" => TransactionLinkState.Ignored, "internal" => TransactionLinkState.Internal,
            _ => TransactionLinkState.Unlinked,
        },
        r.FetchedVia, r.CreatedAt);
}
