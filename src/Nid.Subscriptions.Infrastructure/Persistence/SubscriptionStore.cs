using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.Subscriptions.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using Duration = Nid.Core.Contracts.Duration;

namespace Nid.Subscriptions.Infrastructure.Persistence;

/// <summary>Maps rows to the pure engine's input and back. It computes nothing.</summary>
public sealed class SubscriptionStore(SubscriptionsDbContext db) : ISubscriptionStore
{
    public async Task<SubscriptionInput?> LoadAsync(ItemId id, CancellationToken ct)
        => (await LoadManyAsync([id], ct)).SingleOrDefault();

    public async Task<IReadOnlyList<SubscriptionInput>> LoadManyAsync(IReadOnlyCollection<ItemId> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
            return [];

        var keys = ids.Select(i => i.Value).ToHashSet();

        var subscriptions = await db.Subscriptions.AsNoTracking()
            .Include(s => s.Phases)
            .Where(s => keys.Contains(s.ItemId))
            .ToListAsync(ct);

        // A purchase belongs to a subscription through its grants, and may carry grants for others too:
        // load whole purchases, and let the engine filter the grants it does not own.
        var purchaseIds = await db.Grants.AsNoTracking()
            .Where(g => keys.Contains(g.SubscriptionItemId))
            .Select(g => g.PurchaseId)
            .Distinct()
            .ToListAsync(ct);

        var purchases = await db.Purchases.AsNoTracking()
            .Include(p => p.Grants)
            .Where(p => purchaseIds.Contains(p.Id))
            .ToListAsync(ct);

        return subscriptions.Select(s => new SubscriptionInput(
            new SubscriptionHeader(new ItemId(s.ItemId), s.AutoRenew, s.CancelledAt, ToTerms(s)),
            s.Phases.OrderBy(p => p.StartsOn).Select(ToPhase).ToList(),
            purchases
                .Where(p => p.Grants.Any(g => g.SubscriptionItemId == s.ItemId))
                .OrderBy(p => p.Date).ThenBy(p => p.Id)
                .Select(ToPurchase)
                .ToList())).ToList();
    }

    public async Task<Result> CreateAsync(ItemId id, NewSubscription subscription, CancellationToken ct)
    {
        db.Subscriptions.Add(new SubscriptionRow
        {
            ItemId = id.Value,
            ProviderKey = subscription.ProviderKey,
            AutoRenew = subscription.AutoRenew,
            Note = subscription.Note,
            CancelledAt = subscription.CancelledAt,
            AnniversaryOn = subscription.Terms?.AnniversaryOn,
            NoticeCount = subscription.Terms?.Notice?.Count,
            NoticeUnit = subscription.Terms?.Notice is { } notice ? Name(notice.Unit) : null,
            CommitmentEndsOn = subscription.Terms?.CommitmentEndsOn,
            CancellableFrom = subscription.Terms?.CancellableFrom,
            ImportRef = subscription.Import?.Ref,
            ImportHash = subscription.Import?.Hash,
            ImportedBy = subscription.Import?.ImportedBy?.Value,
            Users = subscription.Users.Select(u => new SubscriptionUserRow { ItemId = id.Value, PersonId = u.Value }).ToList(),
        });
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result> ReplaceAsync(ItemId id, SubscriptionReplacement replacement, CancellationToken ct)
    {
        var row = await db.Subscriptions.Include(s => s.Users).FirstOrDefaultAsync(s => s.ItemId == id.Value, ct);
        if (row is null)
            return NotFound(id);

        row.ProviderKey = replacement.ProviderKey;
        row.AutoRenew = replacement.AutoRenew;
        row.Note = replacement.Note;
        row.CancelledAt = replacement.CancelledAt;
        Write(row, replacement.Terms ?? new ContractTerms(null, null, null, null));
        row.ImportHash = replacement.Import.Hash;
        var wanted = replacement.Users.Select(u => u.Value).ToHashSet();
        foreach (var user in row.Users.Where(u => !wanted.Contains(u.PersonId)).ToList())
            row.Users.Remove(user);
        foreach (var person in wanted.Where(p => row.Users.All(u => u.PersonId != p)))
            row.Users.Add(new SubscriptionUserRow { ItemId = id.Value, PersonId = person });

        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result> EditAsync(ItemId id, SubscriptionEdit edit, CancellationToken ct)
    {
        var row = await db.Subscriptions.FirstOrDefaultAsync(s => s.ItemId == id.Value, ct);
        if (row is null)
            return NotFound(id);
        if (edit.Terms is { } change)
        {
            // Validated on the row just loaded: the save carries its xmin, so nothing stored can differ from what was checked.
            var merged = change.ApplyTo(ToTerms(row));
            if (merged.Validate() is { } refused)
                return Result.Failure(FieldErrors.At(refused, "notice"));
            Write(row, merged);
        }

        if (edit.AutoRenew is { } autoRenew)
            row.AutoRenew = autoRenew;
        if (edit.ClearCancelledAt)
            row.CancelledAt = null;
        else if (edit.CancelledAt is { } cancelled)
            row.CancelledAt = cancelled;
        if (edit.Note is not null)
            row.Note = edit.Note;
        if (edit.ClearProviderKey)
            row.ProviderKey = null;
        else if (edit.ProviderKey is { } providerKey)
            row.ProviderKey = providerKey;

        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<IReadOnlyDictionary<ItemId, string>> ProviderKeysAsync(IReadOnlyCollection<ItemId> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
            return new Dictionary<ItemId, string>();

        var keys = ids.Select(i => i.Value).ToHashSet();
        var rows = await db.Subscriptions.AsNoTracking()
            .Where(s => keys.Contains(s.ItemId) && s.ProviderKey != null)
            .Select(s => new { s.ItemId, s.ProviderKey })
            .ToListAsync(ct);
        return rows.ToDictionary(r => new ItemId(r.ItemId), r => r.ProviderKey!);
    }

    public async Task<Result<BillingPhaseId>> AddPhaseAsync(ItemId id, BillingPhase phase, RowOrigin origin, CancellationToken ct)
    {
        if (!await db.Subscriptions.AnyAsync(s => s.ItemId == id.Value, ct))
            return Result.Failure<BillingPhaseId>(NotFoundFailureFor(id));

        db.Phases.Add(new BillingPhaseRow
        {
            Id = phase.Id.Value,
            ItemId = id.Value,
            StartsOn = phase.StartsOn,
            EndsOn = phase.EndsOn,
            PriceAmount = phase.Price.Amount,
            PriceCurrency = phase.Price.Currency.Code,
            EveryCount = phase.Every.Count,
            EveryUnit = Name(phase.Every.Unit),
            CoversCount = phase.Covers?.Count,
            CoversUnit = phase.Covers is { } covers ? Name(covers.Unit) : null,
            MaxCycles = phase.MaxCycles,
            PayerType = Name(phase.Payer.Type),
            PayerPersonId = phase.Payer.PersonId?.Value,
            Note = origin.Note,
            ImportRef = origin.Import?.Ref,
            ImportHash = origin.Import?.Hash,
            ImportedBy = origin.Import?.ImportedBy?.Value,
        });
        await db.SaveChangesAsync(ct);
        return Result.Success(phase.Id);
    }

    public async Task<Result> ClosePhaseAsync(ItemId id, BillingPhaseId phase, LocalDate endsOn, int? maxCycles, CancellationToken ct)
    {
        var row = await db.Phases.FirstOrDefaultAsync(p => p.Id == phase.Value && p.ItemId == id.Value, ct);
        if (row is null)
            return Result.Failure(new NotFoundFailure("phase", phase.ToString()));

        if (endsOn <= row.StartsOn)
            return Result.Failure(new Failure("subscriptions.phase_range", "A phase ends after it starts.", null));

        row.EndsOn = endsOn;
        if (maxCycles is { } cycles)
            row.MaxCycles = cycles;
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result<PurchaseId>> AddPurchaseAsync(Purchase purchase, RowOrigin origin, CancellationToken ct)
    {
        db.Purchases.Add(new PurchaseRow
        {
            Id = purchase.Id.Value,
            Date = purchase.Date,
            Label = purchase.Label,
            PriceAmount = purchase.Price.Amount,
            PriceCurrency = purchase.Price.Currency.Code,
            PayerType = Name(purchase.Payer.Type),
            PayerPersonId = purchase.Payer.PersonId?.Value,
            Kind = Name(purchase.Kind),
            AllocatedAmount = purchase.AllocatedAmount,
            Note = origin.Note,
            ImportRef = origin.Import?.Ref,
            ImportHash = origin.Import?.Hash,
            ImportedBy = origin.Import?.ImportedBy?.Value,
            Grants = purchase.Grants.Select((g, index) => new GrantRow
            {
                PurchaseId = purchase.Id.Value,
                Index = index,
                SubscriptionItemId = g.SubscriptionId.Value,
                DurationCount = g.Duration.Count,
                DurationUnit = Name(g.Duration.Unit),
                Source = Name(g.Source),
                StartsOn = g.StartsOn,
            }).ToList(),
        });
        await db.SaveChangesAsync(ct);
        return Result.Success(purchase.Id);
    }

    public async Task<Result> ReplacePhaseAsync(ItemId id, BillingPhase phase, RowOrigin origin, CancellationToken ct)
    {
        var row = await db.Phases.FirstOrDefaultAsync(p => p.Id == phase.Id.Value && p.ItemId == id.Value, ct);
        if (row is null)
            return Result.Failure(new NotFoundFailure("phase", phase.Id.ToString()));

        row.StartsOn = phase.StartsOn;
        row.EndsOn = phase.EndsOn;
        row.PriceAmount = phase.Price.Amount;
        row.PriceCurrency = phase.Price.Currency.Code;
        row.EveryCount = phase.Every.Count;
        row.EveryUnit = Name(phase.Every.Unit);
        row.CoversCount = phase.Covers?.Count;
        row.CoversUnit = phase.Covers is { } covers ? Name(covers.Unit) : null;
        row.MaxCycles = phase.MaxCycles;
        row.PayerType = Name(phase.Payer.Type);
        row.PayerPersonId = phase.Payer.PersonId?.Value;
        row.Note = origin.Note;
        row.ImportHash = origin.Import?.Hash;
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result> ReplacePurchaseAsync(Purchase purchase, RowOrigin origin, CancellationToken ct)
    {
        var row = await db.Purchases.Include(p => p.Grants).FirstOrDefaultAsync(p => p.Id == purchase.Id.Value, ct);
        if (row is null)
            return Result.Failure(new NotFoundFailure("purchase", purchase.Id.ToString()));

        // Grants have no identity of their own beyond (purchase, index): the list is replaced, and the history
        // records each grant row deleted and inserted (1F3b spec §3.3). Two saves: the key (purchase, index) is
        // deleted and inserted again, which one save would order as a clash. Both are inside the commit's transaction.
        // The first save deletes the old grants only; the second writes the purchase's new values with the new
        // grants, and its update line names the old grants' items as well as the new ones' (SubscriptionsAuditMap).
        var replacedItems = row.Grants.Select(g => g.SubscriptionItemId).Distinct().ToList();
        db.Grants.RemoveRange(row.Grants);
        row.Grants.Clear();
        await db.SaveChangesAsync(ct);

        row.Date = purchase.Date;
        row.Label = purchase.Label;
        row.PriceAmount = purchase.Price.Amount;
        row.PriceCurrency = purchase.Price.Currency.Code;
        row.PayerType = Name(purchase.Payer.Type);
        row.PayerPersonId = purchase.Payer.PersonId?.Value;
        row.Kind = Name(purchase.Kind);
        row.AllocatedAmount = purchase.AllocatedAmount;
        row.Note = origin.Note;
        row.ImportHash = origin.Import?.Hash;
        row.ReplacedGrantItems = replacedItems;
        row.Grants.AddRange(purchase.Grants.Select((g, index) => new GrantRow
        {
            PurchaseId = purchase.Id.Value,
            Index = index,
            SubscriptionItemId = g.SubscriptionId.Value,
            DurationCount = g.Duration.Count,
            DurationUnit = Name(g.Duration.Unit),
            Source = Name(g.Source),
            StartsOn = g.StartsOn,
        }));
        await db.SaveChangesAsync(ct);
        row.ReplacedGrantItems = [];
        return Result.Success();
    }

    public async Task<StoredRows> ReadAllAsync(IReadOnlyCollection<ItemId> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
            return new StoredRows([], [], []);

        var keys = ids.Select(i => i.Value).ToHashSet();
        var subscriptions = await db.Subscriptions.AsNoTracking()
            .Include(s => s.Users).Include(s => s.Phases)
            .Where(s => keys.Contains(s.ItemId))
            .ToListAsync(ct);
        var purchaseIds = await db.Grants.AsNoTracking()
            .Where(g => keys.Contains(g.SubscriptionItemId)).Select(g => g.PurchaseId).Distinct().ToListAsync(ct);
        var purchases = await db.Purchases.AsNoTracking().Include(p => p.Grants)
            .Where(p => purchaseIds.Contains(p.Id)).ToListAsync(ct);

        return new StoredRows(
            subscriptions.Select(s => new StoredSubscription(
                new ItemId(s.ItemId), s.ProviderKey, s.AutoRenew, s.CancelledAt, s.Note,
                s.Users.Select(u => new PersonId(u.PersonId)).ToList(), Origin(s.ImportRef, s.ImportHash, s.ImportedBy), ToTerms(s))).ToList(),
            subscriptions.SelectMany(s => s.Phases.Select(p => new StoredPhase(
                new ItemId(s.ItemId), ToPhase(p), p.Note, Origin(p.ImportRef, p.ImportHash, p.ImportedBy)))).ToList(),
            purchases.Select(p => new StoredPurchase(ToPurchase(p), p.Note, Origin(p.ImportRef, p.ImportHash, p.ImportedBy))).ToList());

        static ImportRef? Origin(string? importRef, string? hash, Guid? by)
            => importRef is null ? null : new ImportRef(importRef, hash!, by is { } b ? new PersonId(b) : null);
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<ImportedRow>>> FindImportedAsync(
        ImportedTable table, IReadOnlyCollection<string> refs, CancellationToken ct)
    {
        if (refs.Count == 0)
            return new Dictionary<string, IReadOnlyList<ImportedRow>>();

        var keys = refs.ToList();
        var rows = table switch
        {
            ImportedTable.Subscription => (await db.Subscriptions.AsNoTracking()
                    .Where(r => r.ImportRef != null && keys.Contains(r.ImportRef))
                    .Select(r => new { Ref = r.ImportRef!, Id = r.ItemId, Hash = r.ImportHash!, r.ImportedBy })
                    .ToListAsync(ct))
                .Select(r => (r.Ref, Row: Row(r.Id, r.Hash, r.ImportedBy, [r.Id]))),
            ImportedTable.BillingPhase => (await db.Phases.AsNoTracking()
                    .Where(r => r.ImportRef != null && keys.Contains(r.ImportRef))
                    .Select(r => new { Ref = r.ImportRef!, r.Id, Hash = r.ImportHash!, r.ImportedBy, r.ItemId })
                    .ToListAsync(ct))
                .Select(r => (r.Ref, Row: Row(r.Id, r.Hash, r.ImportedBy, [r.ItemId]))),
            _ => (await db.Purchases.AsNoTracking()
                    .Where(r => r.ImportRef != null && keys.Contains(r.ImportRef))
                    .Select(r => new
                    {
                        Ref = r.ImportRef!, r.Id, Hash = r.ImportHash!, r.ImportedBy,
                        Items = r.Grants.OrderBy(g => g.Index).Select(g => g.SubscriptionItemId).ToList(),
                    })
                    .ToListAsync(ct))
                .Select(r => (r.Ref, Row: Row(r.Id, r.Hash, r.ImportedBy, r.Items.Distinct()))),
        };

        return rows
            .GroupBy(r => r.Ref, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ImportedRow>)g.Select(r => r.Row).OrderBy(r => r.Id).ToList(), StringComparer.Ordinal);

        static ImportedRow Row(Guid id, string hash, Guid? importedBy, IEnumerable<Guid> items)
            => new(id, hash, importedBy is { } by ? new PersonId(by) : null, items.Select(i => new ItemId(i)).ToList());
    }

    public async Task<IReadOnlyList<ItemId>> GrantedItemsAsync(PurchaseId purchase, CancellationToken ct)
    {
        var ids = await db.Grants.AsNoTracking()
            .Where(g => g.PurchaseId == purchase.Value)
            .Select(g => g.SubscriptionItemId)
            .Distinct()
            .ToListAsync(ct);
        return ids.Select(id => new ItemId(id)).ToList();
    }

    public async Task<Result> DeletePurchaseAsync(ItemId id, PurchaseId purchase, CancellationToken ct)
    {
        var row = await db.Purchases.Include(p => p.Grants)
            .FirstOrDefaultAsync(p => p.Id == purchase.Value, ct);
        // A purchase that does not exist, and one that exists but grants nothing to this subscription, look
        // the same from here: neither is a purchase of the id the caller had access to.
        if (row is null || row.Grants.All(g => g.SubscriptionItemId != id.Value))
            return Result.Failure(new NotFoundFailure("purchase", purchase.ToString()));

        db.Purchases.Remove(row); // grants cascade
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    private static ContractTerms? ToTerms(SubscriptionRow r)
    {
        var terms = new ContractTerms(
            r.AnniversaryOn,
            r.NoticeCount is { } count && r.NoticeUnit is { } unit
                ? Required(Duration.Create(count, ParseUnit(unit)), "notice_count")
                : null,
            r.CommitmentEndsOn,
            r.CancellableFrom);
        return terms.IsEmpty ? null : terms;
    }

    private static void Write(SubscriptionRow row, ContractTerms terms)
    {
        row.AnniversaryOn = terms.AnniversaryOn;
        row.NoticeCount = terms.Notice?.Count;
        row.NoticeUnit = terms.Notice is { } notice ? Name(notice.Unit) : null;
        row.CommitmentEndsOn = terms.CommitmentEndsOn;
        row.CancellableFrom = terms.CancellableFrom;
    }

    private static BillingPhase ToPhase(BillingPhaseRow r) => new(
        new BillingPhaseId(r.Id), r.StartsOn, r.EndsOn,
        Required(Money.Create(r.PriceAmount, ParseCurrency(r.PriceCurrency)), "price_amount"),
        Required(Duration.Create(r.EveryCount, ParseUnit(r.EveryUnit)), "every_count"),
        r.CoversCount is { } count && r.CoversUnit is { } unit
            ? Required(Duration.Create(count, ParseUnit(unit)), "covers_count")
            : null,
        r.MaxCycles,
        new Payer(ParsePayer(r.PayerType), r.PayerPersonId is { } p ? new PersonId(p) : null));

    private static Purchase ToPurchase(PurchaseRow r) => new(
        new PurchaseId(r.Id), r.Date, r.Label,
        Required(Money.Create(r.PriceAmount, ParseCurrency(r.PriceCurrency)), "price_amount"),
        new Payer(ParsePayer(r.PayerType), r.PayerPersonId is { } p ? new PersonId(p) : null),
        ParseKind(r.Kind),
        r.Grants.OrderBy(g => g.Index).Select(g => new Grant(
            new ItemId(g.SubscriptionItemId),
            Required(Duration.Create(g.DurationCount, ParseUnit(g.DurationUnit)), "duration_count"),
            ParseSource(g.Source),
            g.StartsOn)).ToList(),
        r.AllocatedAmount);

    private static Result NotFound(ItemId id) => Result.Failure(NotFoundFailureFor(id));

    private static Failure NotFoundFailureFor(ItemId id)
        => new NotFoundFailure("subscription", id.ToString());

    // Stored spellings are the data model's snake_case names, not .NET enum names.
    private static string Name(DurationUnit unit) => unit.ToString().ToLowerInvariant();
    private static string Name(PayerType type) => type.ToString().ToLowerInvariant();
    private static string Name(CoverageSource source) => source.ToString().ToLowerInvariant();
    private static string Name(PurchaseKind kind) => kind switch
    {
        PurchaseKind.PrepaidTerm => "prepaid_term",
        _ => kind.ToString().ToLowerInvariant(),
    };

    private static DurationUnit ParseUnit(string s) => Enum.Parse<DurationUnit>(s, ignoreCase: true);
    private static PayerType ParsePayer(string s) => Enum.Parse<PayerType>(s, ignoreCase: true);
    private static CoverageSource ParseSource(string s) => Enum.Parse<CoverageSource>(s, ignoreCase: true);
    private static PurchaseKind ParseKind(string s) => s == "prepaid_term" ? PurchaseKind.PrepaidTerm : Enum.Parse<PurchaseKind>(s, ignoreCase: true);

    private static Currency ParseCurrency(string code)
        => Currency.TryCreate(code.Trim(), out var currency) ? currency : Currency.Eur;

    /// <summary>A row that violates its own check constraint is a corrupted database, not a user error.</summary>
    private static T Required<T>(Result<T> result, string column) where T : notnull
        => result.TryGetValue(out var value)
            ? value!
            : throw new InvalidOperationException($"Column '{column}' holds a value the domain refuses.");
}
