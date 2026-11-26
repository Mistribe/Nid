using Microsoft.EntityFrameworkCore;
using Nid.Banking.Application;
using Nid.Banking.Application.Operations;
using Nid.Banking.Contracts;
using Nid.Banking.Domain;
using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Banking.Infrastructure.Persistence;

public sealed class TransactionStore(BankingDbContext db, IClock clock) : ITransactionStore
{
    public async Task<TransactionWrite> AddAsync(Guid account, IReadOnlyList<NewBankTransaction> lines, CancellationToken ct, bool reconciled = false)
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
                Remittance = l.Remittance, NormalizedLabel = normalized[i], SuggestedPattern = CategoryRules.Suggest(normalized[i]), FetchedVia = l.FetchedVia, CreatedAt = now,
                ReconciledAt = reconciled ? now : null, BankCode = CleanBankCode(l.BankCode),
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
        IReadOnlyList<Guid> accounts, LocalDate? from, LocalDate? to, int skip, int take, CancellationToken ct, string? state = null,
        IReadOnlyList<Guid>? readable = null, bool uncategorised = false, string? normalizedLabel = null)
    {
        var query = db.Transactions.AsNoTracking().Where(t => accounts.Contains(t.AccountId));
        if (uncategorised)
            query = query.Where(t => t.CategoryId == null && t.CategorySource == null && t.LinkState != "ignored");
        if (normalizedLabel is not null)
            query = query.Where(t => t.NormalizedLabel == normalizedLabel);
        query = WithoutEchoes(ByState(query, state, readable), accounts, state);
        if (from is { } f)
            query = query.Where(t => t.BookedOn >= f);
        if (to is { } u)
            query = query.Where(t => t.BookedOn <= u);
        return (await query.OrderByDescending(t => t.BookedOn).ThenByDescending(t => t.Id).Skip(skip).Take(take).ToListAsync(ct))
            .Select(ToModel).ToList();
    }

    /// <summary>
    /// The accounts no figure counts: an ignored one, and a card attached to an ignored one. #780: a card follows its account, ignored or not,
    /// since its account's echoes of its purchases are internal.
    /// </summary>
    private IQueryable<Guid> LeftOut => db.Accounts.Where(a => a.CardOfAccountId == null ? a.Ignored : db.Accounts.Any(p => p.Id == a.CardOfAccountId && p.Ignored))
        .Select(a => a.Id);

    /// <summary>
    /// One purchase, one row: an immediate card's echo (#416) goes when its card is listed too, the card line saying where it was debited.
    /// « Interne » still lists it.
    /// </summary>
    private IQueryable<TransactionRow> WithoutEchoes(IQueryable<TransactionRow> query, IReadOnlyList<Guid> accounts, string? state)
        => state is "internal" ? query : query.Where(t => !db.Links.Any(l => l.TransactionId == t.Id && l.Outcome == "card_settlement"
            && l.TargetKey != null && l.TargetKey.StartsWith(CardEchoes.KeyPrefix) && l.CounterpartAccountId != null
            && accounts.Contains(l.CounterpartAccountId.Value)));

    /// <summary>D19: with <paramref name="readable"/>, an internal line whose counterpart account is not in it counts as <c>linked</c>.</summary>
    private IQueryable<TransactionRow> ByState(IQueryable<TransactionRow> query, string? state, IReadOnlyList<Guid>? readable)
    {
        var hidden = db.Links.Where(l => l.CounterpartAccountId != null && readable != null && !readable.Contains(l.CounterpartAccountId.Value))
            .Select(l => l.TransactionId);
        if (state is "internal" && readable is not null)
            return query.Where(t => t.LinkState == state && !hidden.Contains(t.Id));
        if (state is "linked" && readable is not null)
            return query.Where(t => t.LinkState == state || (t.LinkState == "internal" && hidden.Contains(t.Id)));
        // #437: the lines a spending figure counts (its Entries), whatever their counterpart.
        if (state is "counted")
        {
            var leftOut = LeftOut;
            return query.Where(t => t.LinkState != "ignored" && t.LinkState != "internal" && !leftOut.Contains(t.AccountId));
        }
        return state is not null ? query.Where(t => t.LinkState == state) : query;
    }

    public async Task<(IReadOnlyList<BankTransaction> Items, int Total, IReadOnlyList<SpendingSum> Sums)> OperationsAsync(
        IReadOnlyList<Guid> readable, OperationFilter filter, IReadOnlyList<Guid> categories, int skip, int take, CancellationToken ct)
    {
        var accounts = filter.Accounts;
        var query = WithoutEchoes(ByState(db.Transactions.AsNoTracking().Where(t => accounts.Contains(t.AccountId)), filter.State, readable),
            accounts, filter.State);
        if (filter.From is { } from)
            query = query.Where(t => t.BookedOn >= from);
        if (filter.To is { } to)
            query = query.Where(t => t.BookedOn <= to);
        if (filter.Categories.Count > 0 || filter.Uncategorised)
        {
            var uncategorised = filter.Uncategorised;
            // A split line matches by its parts (listed once), an unsplit one by its own category.
            query = query.Where(t => t.CategorySource != "split"
                ? (t.CategoryId != null && categories.Contains(t.CategoryId.Value)) || (uncategorised && t.CategoryId == null)
                : db.TransactionParts.Any(p => p.TransactionId == t.Id
                    && ((p.CategoryId != null && categories.Contains(p.CategoryId.Value)) || (uncategorised && p.CategoryId == null))));
        }
        if (filter.Direction is "out")
            query = query.Where(t => t.Amount < 0m);
        else if (filter.Direction is "in")
            query = query.Where(t => t.Amount > 0m);
        if (filter.Min is { } min)
            query = query.Where(t => Math.Abs(t.Amount) >= min);
        if (filter.Max is { } max)
            query = query.Where(t => Math.Abs(t.Amount) <= max);
        if (filter.Search is { Length: >= 2 } search)
        {
            // Literal: %, _ and \ escaped; accents and case aside on both sides (the unaccent extension).
            var pattern = "%" + search.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            query = query.Where(t => EF.Functions.ILike(EF.Functions.Unaccent(t.Label), EF.Functions.Unaccent(pattern), "\\")
                || (t.Counterparty != null && EF.Functions.ILike(EF.Functions.Unaccent(t.Counterparty), EF.Functions.Unaccent(pattern), "\\")));
        }

        var total = await query.CountAsync(ct);
        var page = await query.OrderByDescending(t => t.BookedOn).ThenByDescending(t => t.Id).Skip(skip).Take(take).ToListAsync(ct);
        var leftOut = LeftOut;
        // D19: an internal line whose counterpart the reader cannot read shows as linked, so it counts like one.
        var hidden = db.Links.Where(l => l.CounterpartAccountId != null && !readable.Contains(l.CounterpartAccountId.Value)).Select(l => l.TransactionId);
        var counted = query.Where(t => t.LinkState != "ignored" && (t.LinkState != "internal" || hidden.Contains(t.Id)) && !leftOut.Contains(t.AccountId));
        // Under a category filter a split line counts by its matching parts only (as the page lists it), else by its amount.
        var byCategory = filter.Categories.Count > 0 || filter.Uncategorised;
        var uncategorisedParts = filter.Uncategorised;
        var entries = counted.Where(t => !byCategory || t.CategorySource != "split")
            .Select(t => new SpendingEntry { LineId = t.Id, BookedOn = t.BookedOn, Currency = t.Currency, Amount = t.Amount });
        if (byCategory)
            entries = entries.Concat(db.TransactionParts.Where(p => (p.CategoryId != null && categories.Contains(p.CategoryId.Value)) || (uncategorisedParts && p.CategoryId == null))
                .Join(counted.Where(t => t.CategorySource == "split"), p => p.TransactionId, t => t.Id,
                    (p, t) => new SpendingEntry { LineId = t.Id, BookedOn = t.BookedOn, Currency = t.Currency, Amount = p.Amount }));
        var sums = (await entries
            .GroupBy(t => new { t.Currency, t.BookedOn.Year, t.BookedOn.Month })
            .Select(g => new { g.Key, Debits = g.Sum(t => t.Amount < 0 ? t.Amount : 0m), Credits = g.Sum(t => t.Amount > 0 ? t.Amount : 0m) })
            .ToListAsync(ct))
            .Select(g => new SpendingSum(null, Currency.TryCreate(g.Key.Currency, out var currency) ? currency
                    : throw new InvalidOperationException($"Bank lines in '{g.Key.Currency}' have no valid currency."),
                new YearMonth(g.Key.Year, g.Key.Month), g.Debits, g.Credits))
            .ToList();
        return (page.Select(ToModel).ToList(), total, sums);
    }

    public async Task<IReadOnlyList<Guid>> OperationCategoriesAsync(IReadOnlyList<Guid> accounts, CancellationToken ct)
        => await db.Transactions.Where(t => accounts.Contains(t.AccountId) && t.CategoryId != null).Select(t => t.CategoryId!.Value)
            .Union(db.TransactionParts.Where(p => p.CategoryId != null && db.Transactions.Any(t => t.Id == p.TransactionId && accounts.Contains(t.AccountId)))
                .Select(p => p.CategoryId!.Value)).ToListAsync(ct);

    public async Task<IReadOnlyList<BankTransaction>> UnreconciledAsync(IReadOnlyCollection<Guid>? accounts, CancellationToken ct)
        => (await db.Transactions.AsNoTracking()
            .Where(t => t.ReconciledAt == null && (accounts == null || accounts.Contains(t.AccountId)))
            .OrderBy(t => t.BookedOn).ThenBy(t => t.Id).ToListAsync(ct)).Select(ToModel).ToList();

    public async Task<IReadOnlyList<BankTransaction>> UnlinkedBetweenAsync(LocalDate from, LocalDate to, CancellationToken ct)
        => (await db.Transactions.AsNoTracking()
            .Where(t => t.LinkState == "unlinked" && t.BookedOn >= from && t.BookedOn <= to)
            .OrderBy(t => t.BookedOn).ThenBy(t => t.Id).ToListAsync(ct)).Select(ToModel).ToList();

    public async Task<BankTransaction?> FindAsync(Guid id, CancellationToken ct)
        => await db.Transactions.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct) is { } row ? ToModel(row) : null;

    public async Task<IReadOnlyList<BankTransaction>> UncategorisedLinkedAsync(Guid? after, int take, CancellationToken ct)
        => (await db.Transactions.AsNoTracking()
            .Where(t => t.CategorySource == null && (t.LinkState == "linked" || t.LinkState == "internal") && (after == null || t.Id.CompareTo(after.Value) > 0))
            .OrderBy(t => t.Id).Take(take).ToListAsync(ct)).Select(ToModel).ToList();

    public async Task MarkReconciledAsync(IReadOnlyCollection<Guid> ids, Instant at, CancellationToken ct)
        => await db.Transactions.Where(t => ids.Contains(t.Id)).ExecuteUpdateAsync(s => s.SetProperty(t => t.ReconciledAt, at), ct);

    public Task<int> PurgeBookedBeforeAsync(LocalDate before, IReadOnlyCollection<Guid> keep, CancellationToken ct)
    {
        var kept = keep.ToList();
        return Purgeable(before).Where(t => !kept.Contains(t.Id)).ExecuteDeleteAsync(ct);
    }

    public async Task<IReadOnlyList<Guid>> PurgeableBeforeAsync(LocalDate before, CancellationToken ct)
        => await Purgeable(before).Select(t => t.Id).ToListAsync(ct);

    private IQueryable<TransactionRow> Purgeable(LocalDate before)
        => db.Transactions.Where(t => t.BookedOn < before && (t.LinkState == "unlinked" || t.LinkState == "ignored")
            && t.Note == null && !db.TransactionParts.Any(p => p.TransactionId == t.Id));

    public async Task<IReadOnlyList<BankTransaction>> FindManyAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var wanted = ids.ToList();
        return (await db.Transactions.AsNoTracking().Where(t => wanted.Contains(t.Id)).ToListAsync(ct)).Select(ToModel).ToList();
    }

    public async Task<IReadOnlyList<Guid>> IdsOfAsync(IReadOnlyCollection<Guid> accounts, CancellationToken ct)
    {
        var wanted = accounts.ToList();
        return await db.Transactions.Where(t => wanted.Contains(t.AccountId)).Select(t => t.Id).ToListAsync(ct);
    }

    /// <summary>The lines of the spending views: an unsplit line is one entry, a split line its parts (never also itself).</summary>
    private IQueryable<SpendingEntry> Entries(IReadOnlyList<Guid> accounts, LocalDate from, LocalDate to, bool withLinked = true)
    {
        var lines = db.Transactions.AsNoTracking()
            .Where(t => accounts.Contains(t.AccountId) && t.BookedOn >= from && t.BookedOn <= to && t.LinkState != "ignored" && t.LinkState != "internal"
                && (withLinked || t.LinkState != "linked"));
        return lines.Where(t => t.CategorySource != "split").Select(t => new SpendingEntry { LineId = t.Id, PartId = null, BookedOn = t.BookedOn, CategoryId = t.CategoryId, Currency = t.Currency, Amount = t.Amount, Position = 0 })
            .Concat(db.TransactionParts.AsNoTracking().Join(lines.Where(t => t.CategorySource == "split"), p => p.TransactionId, t => t.Id,
                (p, t) => new SpendingEntry { LineId = t.Id, PartId = p.Id, BookedOn = t.BookedOn, CategoryId = p.CategoryId, Currency = t.Currency, Amount = p.Amount, Position = p.Position }));
    }

    private sealed class SpendingEntry
    {
        public Guid LineId { get; init; }
        public Guid? PartId { get; init; }
        public LocalDate BookedOn { get; init; }
        public Guid? CategoryId { get; init; }
        public string Currency { get; init; } = "";
        public decimal Amount { get; init; }
        public int Position { get; init; }
    }

    public async Task<IReadOnlyList<SpendingSum>> SpendingAsync(IReadOnlyList<Guid> accounts, LocalDate from, LocalDate to, CancellationToken ct, bool withLinked = true)
        => (await Entries(accounts, from, to, withLinked)
            .GroupBy(e => new { e.CategoryId, e.Currency, e.BookedOn.Year, e.BookedOn.Month })
            .Select(g => new { g.Key, Debits = g.Sum(e => e.Amount < 0 ? e.Amount : 0m), Credits = g.Sum(e => e.Amount > 0 ? e.Amount : 0m),
                DebitLines = g.Sum(e => e.Amount < 0 ? 1 : 0) })
            .ToListAsync(ct))
            .Select(g => new SpendingSum(g.Key.CategoryId, Currency.TryCreate(g.Key.Currency, out var currency) ? currency
                    : throw new InvalidOperationException($"Bank lines in '{g.Key.Currency}' have no valid currency."),
                new YearMonth(g.Key.Year, g.Key.Month), g.Debits, g.Credits, g.DebitLines))
            .ToList();

    public async Task<(IReadOnlyList<(BankTransaction Line, IReadOnlyList<Guid> PartIds, decimal Amount)> Items, int Count)> SpendingLinesAsync(
        IReadOnlyList<Guid> accounts, IReadOnlyList<Guid> categories, bool uncategorised, LocalDate from, LocalDate to, int skip, int take, CancellationToken ct)
    {
        var query = Entries(accounts, from, to);
        query = uncategorised ? query.Where(e => e.CategoryId == null && e.Amount < 0)
            : query.Where(e => e.CategoryId != null && categories.Contains(e.CategoryId.Value));
        // #446: one row per bank line, as Opérations counts them; a split line's parts that match add up.
        var byLine = query.GroupBy(e => new { e.LineId, e.BookedOn }).Select(g => new { g.Key.LineId, g.Key.BookedOn, Amount = g.Sum(e => e.Amount) });
        var count = await byLine.CountAsync(ct);
        var page = await byLine.OrderByDescending(e => e.BookedOn).ThenByDescending(e => e.LineId).Skip(skip).Take(take).ToListAsync(ct);
        var ids = page.Select(e => e.LineId).ToList();
        var parts = (await query.Where(e => e.PartId != null && ids.Contains(e.LineId)).Select(e => new { e.LineId, PartId = e.PartId!.Value }).ToListAsync(ct))
            .ToLookup(e => e.LineId, e => e.PartId);
        var rows = (await db.Transactions.AsNoTracking().Where(t => ids.Contains(t.Id)).ToListAsync(ct)).ToDictionary(t => t.Id);
        return (page.Select(e => (ToModel(rows[e.LineId]), (IReadOnlyList<Guid>)[.. parts[e.LineId]], e.Amount)).ToList(), count);
    }

    public async Task<IReadOnlyList<CategoryUse>> CategoryUseAsync(IReadOnlyList<Guid> accounts, LocalDate from, CancellationToken ct)
    {
        // Internal lines count: they are what the transfer categories classify.
        var lines = db.Transactions.AsNoTracking().Where(t => accounts.Contains(t.AccountId) && t.BookedOn >= from && t.LinkState != "ignored");
        var filed = lines.Where(t => t.CategorySource != "split" && t.CategoryId != null)
            .Select(t => new SpendingEntry { LineId = t.Id, BookedOn = t.BookedOn, CategoryId = t.CategoryId })
            .Concat(db.TransactionParts.AsNoTracking().Where(p => p.CategoryId != null).Join(lines.Where(t => t.CategorySource == "split"), p => p.TransactionId, t => t.Id,
                (p, t) => new SpendingEntry { LineId = t.Id, BookedOn = t.BookedOn, CategoryId = p.CategoryId }));
        return (await filed.GroupBy(e => e.CategoryId!.Value).Select(g => new { g.Key, Lines = g.Count(), LastOn = g.Max(e => e.BookedOn) }).ToListAsync(ct))
            .Select(g => new CategoryUse(g.Key, g.Lines, g.LastOn)).ToList();
    }

    public Task<LocalDate?> EarliestBookedOnAsync(IReadOnlyList<Guid> accounts, CancellationToken ct)
        => db.Transactions.Where(t => accounts.Contains(t.AccountId)).MinAsync(t => (LocalDate?)t.BookedOn, ct);

    private static string? CleanBankCode(string? code)
        => code?.Trim() is { Length: > 0 } t ? (t.Length > 140 ? t[..140] : t) : null;

    internal static BankTransaction ToModel(TransactionRow r) => new(
        r.Id, r.AccountId, r.ProviderRef, r.Fingerprint, r.BookedOn, r.ValueOn, r.Amount,
        Currency.TryCreate(r.Currency, out var currency) ? currency : throw new InvalidOperationException($"Bank transaction {r.Id} has no valid currency."),
        r.Label, r.Counterparty, r.Remittance, r.NormalizedLabel,
        r.LinkState switch
        {
            "linked" => TransactionLinkState.Linked, "ignored" => TransactionLinkState.Ignored, "internal" => TransactionLinkState.Internal,
            _ => TransactionLinkState.Unlinked,
        },
        r.FetchedVia, r.CreatedAt, r.CategoryId,
        r.CategorySource is { } source ? Enum.Parse<CategorySource>(source, ignoreCase: true) : null, r.BankCode, r.Note);
}
