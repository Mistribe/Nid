using System.Text.Json;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.Subscriptions.Contracts;
using NodaTime;
using NodaTime.Text;
using Duration = Nid.Core.Contracts.Duration;

namespace Nid.Subscriptions.Application.Import;

/// <summary>
/// 1D2 design §5.1 and §5.2: what the reader can read of the module, as file records. Everything starts from the
/// reader's visible items; a purchase keeps only its grants to those, and only the providers an instance with the
/// same catalog could not resolve are listed.
/// </summary>
internal sealed class SubscriptionsExport(IVisibleItems visible, ISubscriptionStore store, IItemRegistry items, IProviderStore providers)
{
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<JsonElement>>> ExportAsync(ExportContext context, CancellationToken ct)
    {
        var ids = await visible.ForAsync(context.Reader, VisibilityScope.Me, ct);
        var rows = await store.ReadAllAsync(ids.ToList(), ct);

        var heads = await items.FindManyAsync(rows.Subscriptions.Select(x => x.Id).ToList(), ct);
        var exported = rows.Subscriptions.Where(s => heads.ContainsKey(s.Id)).ToList();
        var exportedIds = exported.Select(s => s.Id).ToHashSet();
        foreach (var id in exportedIds)
            context.Expose(id);

        var subscriptionIds = FileIds.Assign("sub", exported.Select(s => new KeyedRow(s.Id.Value, s.Import?.Ref, s.Import?.ImportedBy)).ToList(), context.Reader);
        foreach (var s in exported)
            context.NameItem(s.Id, subscriptionIds[s.Id.Value]); // for the sections that point at an item (bank_links)
        var phases = rows.Phases.Where(p => exportedIds.Contains(p.SubscriptionId)).ToList();
        var phaseIds = FileIds.Assign("ph", phases.Select(p => new KeyedRow(p.Phase.Id.Value, p.Import?.Ref, p.Import?.ImportedBy)).ToList(), context.Reader);
        var purchases = rows.Purchases.Where(p => p.Purchase.Grants.Any(g => exportedIds.Contains(g.SubscriptionId))).ToList();
        var purchaseIds = FileIds.Assign("pu", purchases.Select(p => new KeyedRow(p.Purchase.Id.Value, p.Import?.Ref, p.Import?.ImportedBy)).ToList(), context.Reader);

        var (providerKeys, listed) = await ProvidersAsync(exported, context, ct);

        var subscriptionRecords = exported
            .OrderBy(s => heads[s.Id].DisplayName, StringComparer.Ordinal).ThenBy(s => subscriptionIds[s.Id.Value], StringComparer.Ordinal)
            .Select(s => new SubscriptionRecord
            {
                Id = subscriptionIds[s.Id.Value],
                ProviderKey = s.ProviderKey is { } key ? providerKeys.GetValueOrDefault(key, key) : null,
                Name = heads[s.Id].DisplayName,
                Ownership = heads[s.Id].Owners
                    .Select(o => new ShareRecord { Person = context.PersonRef(o.PersonId), ShareBp = o.ShareBp })
                    .OrderBy(o => o.Person, StringComparer.Ordinal).ToList(),
                Visibility = heads[s.Id].Visibility == Visibility.Private ? "private" : "shared",
                ShareLevel = ImportSectionKit.ShareLevelText(heads[s.Id]),
                ServiceUsers = s.Users.Select(context.PersonRef).Order(StringComparer.Ordinal).ToList(),
                AutoRenew = s.AutoRenew,
                CancelledAt = s.CancelledAt is { } c ? Date(c) : null,
                AnniversaryOn = s.Terms?.AnniversaryOn is { } anniversary ? Date(anniversary) : null,
                Notice = s.Terms?.Notice is { } notice ? DurationOf(notice) : null,
                CommitmentEndsOn = s.Terms?.CommitmentEndsOn is { } commitment ? Date(commitment) : null,
                CancellableFrom = s.Terms?.CancellableFrom is { } cancellable ? Date(cancellable) : null,
                Note = s.Note,
            }).ToList();
        var order = subscriptionRecords.Select((r, i) => (r.Id, i)).ToDictionary(x => x.Id, x => x.i, StringComparer.Ordinal);

        var phaseRecords = phases
            .Select(p => new BillingPhaseRecord
            {
                Id = phaseIds[p.Phase.Id.Value],
                SubscriptionId = subscriptionIds[p.SubscriptionId.Value],
                StartsOn = Date(p.Phase.StartsOn),
                EndsOn = p.Phase.EndsOn is { } e ? Date(e) : null,
                Price = MoneyOf(p.Phase.Price),
                Every = DurationOf(p.Phase.Every),
                Covers = DurationOf(p.Phase.CoverageEvery),
                MaxCycles = p.Phase.MaxCycles,
                Payer = PayerOf(p.Phase.Payer, context),
                Note = p.Note,
            })
            .OrderBy(r => order[r.SubscriptionId]).ThenBy(r => r.StartsOn, StringComparer.Ordinal).ThenBy(r => r.Id, StringComparer.Ordinal)
            .ToList();

        var purchaseRecords = purchases
            .Select(p => new PurchaseRecord
            {
                Id = purchaseIds[p.Purchase.Id.Value],
                Date = Date(p.Purchase.Date),
                Label = p.Purchase.Label,
                Kind = Kind(p.Purchase.Kind),
                Price = MoneyOf(p.Purchase.Price),
                Payer = PayerOf(p.Purchase.Payer, context),
                AllocatedAmount = p.Purchase.AllocatedAmount,
                Grants = p.Purchase.Grants.Where(g => exportedIds.Contains(g.SubscriptionId)).Select(g => new GrantRecord
                {
                    SubscriptionId = subscriptionIds[g.SubscriptionId.Value],
                    Duration = DurationOf(g.Duration),
                    Source = g.Source.ToString().ToLowerInvariant(),
                    StartsOn = g.StartsOn is { } s ? Date(s) : null,
                }).ToList(),
                Note = p.Note,
            })
            .OrderBy(r => r.Date, StringComparer.Ordinal).ThenBy(r => r.Id, StringComparer.Ordinal)
            .ToList();

        return new Dictionary<string, IReadOnlyList<JsonElement>>
        {
            [ProvidersImport.Section] = listed
                .Select(p => new ProviderRecord { Key = providerKeys[p.Key], Name = p.Name, Website = p.Website, PricingPage = p.PricingPage, LabelKeys = p.LabelKeys })
                .OrderBy(r => r.Key, StringComparer.Ordinal).Select(Element).ToList(),
            [SubscriptionsImportSection.Subscriptions] = subscriptionRecords.Select(Element).ToList(),
            [SubscriptionsImportSection.Phases] = phaseRecords.Select(Element).ToList(),
            [SubscriptionsImportSection.Purchases] = purchaseRecords.Select(Element).ToList(),
        };
    }

    /// <summary>
    /// §5.2: the providers the file lists (custom, adopted, and catalog ones the catalog archived) and, for them, stored
    /// key → file key. A catalog key named directly stays as it is and is kept out of the file keys.
    /// </summary>
    private async Task<(IReadOnlyDictionary<string, string> Keys, IReadOnlyList<Provider> Listed)> ProvidersAsync(
        IReadOnlyList<StoredSubscription> exported, ExportContext context, CancellationToken ct)
    {
        var named = exported.Select(s => s.ProviderKey).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        var found = await providers.FindAsync(named, ct);
        var listed = found.Values.Where(p => p.Origin != ProviderOrigin.Catalog || p.IsArchived).ToList();
        var direct = found.Values.Where(p => p.Origin == ProviderOrigin.Catalog && !p.IsArchived).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        var keys = ProviderFileKeys.Assign(listed.Select(p => new KeyedProvider(p.Key, p.Name, p.Import, p.CreatedBy)).ToList(), direct, context.Reader);
        return (keys, listed);
    }

    private static JsonElement Element<T>(T record) => JsonSerializer.SerializeToElement(record, NidDataJson.InfoOf<T>(SubscriptionsFileJson.Records));
    private static string Date(LocalDate date) => LocalDatePattern.Iso.Format(date);
    private static MoneyRecord MoneyOf(Money money) => new() { Amount = money.Amount, Currency = money.Currency.Code };
    private static DurationRecord DurationOf(Duration d) => new() { Count = d.Count, Unit = d.Unit.ToString().ToLowerInvariant() };
    private static string Kind(PurchaseKind kind) => kind == PurchaseKind.PrepaidTerm ? "prepaid_term" : kind.ToString().ToLowerInvariant();
    private static PayerRecord PayerOf(Payer payer, ExportContext context) => new()
    {
        Type = payer.Type.ToString().ToLowerInvariant(),
        Person = payer.PersonId is { } person ? context.PersonRef(person) : null,
    };
}
