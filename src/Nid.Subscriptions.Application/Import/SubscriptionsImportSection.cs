using System.Text.Json;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.Subscriptions.Application.Catalog;
using Nid.Subscriptions.Contracts;
using Nid.Subscriptions.Domain;
using NodaTime;
using NodaTime.Text;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Subscriptions.Application.Import;

/// <summary>
/// Sections <c>providers</c> (through <see cref="ProvidersImport"/>), <c>subscriptions</c>, <c>billing_phases</c> and <c>purchases</c>. Writes go through the same
/// <see cref="IItemRegistry"/> and <see cref="ISubscriptionStore"/> as the API; rules come from the Core and the
/// engine, never from here. A file id resolves only among rows the importer can see (spec 2026-09-25 §4).
/// </summary>
public sealed class SubscriptionsImportSection(
    IItemRegistry items, ISubscriptionStore store, IAccessPolicy policy, IItemRules rules, IFxRates fx, IHouseholdSettings household,
    IProviderStore providers, ProviderAccess providerAccess, ProviderKeys providerKeys, IVisibleItems visible,
    IPersonRegistry persons)
    : IImportSection
{
    public const string Subscriptions = "subscriptions";
    public const string Phases = "billing_phases";
    public const string Purchases = "purchases";

    /// <summary>Stands for every person and every subscription while a batch is checked alone.</summary>
    private static readonly ItemId Placeholder = new(Guid.Empty);

    private readonly ProvidersImport providerImport = new(providers, providerAccess, providerKeys);
    private readonly List<(StagedEntity Entity, SubscriptionRecord Record)> newSubscriptions = [];
    private readonly List<(StagedEntity Entity, BillingPhaseRecord Record)> newPhases = [];
    private readonly List<(StagedEntity Entity, PurchaseRecord Record)> newPurchases = [];
    private readonly List<(StagedEntity Entity, SubscriptionRecord Record, ItemId Id)> replacedSubscriptions = [];
    private readonly HashSet<string> replacedIds = new(StringComparer.Ordinal); // file ids of replaced subscriptions
    private readonly List<(StagedEntity Entity, BillingPhaseRecord Record, BillingPhaseId Id)> replacedPhases = [];
    private readonly List<(StagedEntity Entity, PurchaseRecord Record, PurchaseId Id)> replacedPurchases = [];
    private IReadOnlyDictionary<string, SectionCounts> counts = new Dictionary<string, SectionCounts>();

    public string Module => "subscriptions";
    public IReadOnlyList<string> Sections => [ProvidersImport.Section, Subscriptions, Phases, Purchases];
    public int Order => 10;

    public IReadOnlyList<ImportIssue> ValidateBatch(string section, JsonElement entities, int firstIndex)
    {
        if (section == ProvidersImport.Section)
            return ProvidersImport.ValidateBatch(entities, firstIndex);

        var issues = new List<ImportIssue>();
        var anyone = (string _) => (PersonId?)new PersonId(Guid.Empty);
        var standIn = ImportSectionKit.StandIns(); // every file ref stands for someone, one distinct stand-in per ref
        var position = 0;
        foreach (var entity in entities.EnumerateArray())
        {
            var path = NidDataJson.Path(section, firstIndex + position++);
            switch (section)
            {
                case Subscriptions:
                    if (NidDataJson.TryRead(entity, SubscriptionsFileJson.Records.SubscriptionRecord, path, issues, out var subscription)
                        && ImportSectionKit.TryId(subscription.Id, path, issues)
                        && RecordParsing.TryElements(subscription, path, issues))
                        RecordParsing.TrySubscription(subscription, path, standIn, issues, out _);
                    break;
                case Phases:
                    if (NidDataJson.TryRead(entity, SubscriptionsFileJson.Records.BillingPhaseRecord, path, issues, out var phaseRecord)
                        && ImportSectionKit.TryId(phaseRecord.Id, path, issues)
                        && RecordParsing.TryPhase(phaseRecord, path, anyone, issues, out var phase))
                        EngineRules(new SubscriptionInput(Header(Placeholder), [phase], []), phase.StartsOn, path, issues);
                    break;
                case Purchases:
                    if (NidDataJson.TryRead(entity, SubscriptionsFileJson.Records.PurchaseRecord, path, issues, out var purchaseRecord)
                        && ImportSectionKit.TryId(purchaseRecord.Id, path, issues)
                        && RecordParsing.TryElements(purchaseRecord, path, issues)
                        && RecordParsing.TryPurchase(purchaseRecord, path, anyone, _ => Placeholder, issues, out var purchase))
                        EngineRules(new SubscriptionInput(Header(Placeholder), [], [purchase]), purchase.Date, path, issues);
                    break;
            }
        }

        return issues;
    }

    public async Task<SectionOutcome> ValidateAllAsync(ImportContext context, IStagedBatches staged, CancellationToken ct)
    {
        newSubscriptions.Clear();
        newPhases.Clear();
        newPurchases.Clear();
        replacedSubscriptions.Clear();
        replacedIds.Clear();
        replacedPhases.Clear();
        replacedPurchases.Clear();
        var issues = new List<ImportIssue>();

        // Before subscriptions: their provider_key may name an entry of this section (design §5 "Import").
        var providerCounts = await providerImport.ValidateAllAsync(context, staged, issues, ct);

        var subscriptionRecords = ImportSectionKit.ReadAll(staged, Subscriptions, SubscriptionsFileJson.Records.SubscriptionRecord, r => r.Id, issues, RecordParsing.TryElements);
        var phaseRecords = ImportSectionKit.ReadAll(staged, Phases, SubscriptionsFileJson.Records.BillingPhaseRecord, r => r.Id, issues, RecordParsing.TryElements);
        var purchaseRecords = ImportSectionKit.ReadAll(staged, Purchases, SubscriptionsFileJson.Records.PurchaseRecord, r => r.Id, issues, RecordParsing.TryElements);

        var knownSubscriptions = await store.FindImportedAsync(ImportedTable.Subscription, subscriptionRecords.Select(s => s.Record.Id).ToList(), ct);
        var knownPhases = await store.FindImportedAsync(ImportedTable.BillingPhase, phaseRecords.Select(p => p.Record.Id).ToList(), ct);
        var knownPurchases = await store.FindImportedAsync(ImportedTable.Purchase, purchaseRecords.Select(p => p.Record.Id).ToList(), ct);
        var readable = new Dictionary<ItemId, bool>(); // one policy check per item, however many rows touch it
        IReadOnlyList<PersonId>? adults = null; // read once, by the first replaced subscription

        var linkable = await providerImport.LinkableAsync(
            context, subscriptionRecords.Select(s => s.Record.ProviderKey).OfType<string>().ToList(), ct);

        var tally = new Dictionary<string, SectionCounts>
        {
            [ProvidersImport.Section] = providerCounts,
            [Subscriptions] = SectionCounts.Zero, [Phases] = SectionCounts.Zero, [Purchases] = SectionCounts.Zero,
        };
        var runs = new Dictionary<string, DryRun>(StringComparer.Ordinal);    // file id → what the engine will walk
        var failed = new HashSet<string>(StringComparer.Ordinal);            // subscriptions already reported
        var existing = new HashSet<string>(StringComparer.Ordinal);          // subscriptions imported before
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);  // engine reference → file path
        var person = Persons(context);

        foreach (var (entity, record) in subscriptionRecords)
        {
            var path = NidDataJson.Path(Subscriptions, entity.Index);
            if (!RecordParsing.TrySubscription(record, path, r => context.Persons.GetValueOrDefault(r), issues, out var parsed))
            {
                failed.Add(record.Id);
                continue;
            }

            if (rules.ValidateOwners(parsed.Visibility, parsed.Owners).TryGetFailure(out var ownership))
            {
                issues.Add(ImportIssue.Error(ownership!.Code, ownership.Message, path + ".ownership"));
                failed.Add(record.Id);
                continue;
            }

            ItemId id;
            var resolved = await ResolveAsync(context.Actor, knownSubscriptions, record.Id, readable, ct);
            if (resolved.OwnHidden)
            {
                issues.Add(ImportedUnseen(path, record.Id));
                failed.Add(record.Id);
                continue;
            }

            if (resolved.Row is { } known)
            {
                // Nothing is created, so the creation rule does not apply. The row is one the actor can see
                // (ResolveAsync). What the file adds to it is checked for write access below.
                id = new ItemId(known.Id);
                if (known.Hash != entity.Hash)
                {
                    if (!context.Replace)
                    {
                        issues.Add(Conflict(path, record.Id));
                        tally[Subscriptions] += new SectionCounts(0, 0, 1);
                        failed.Add(record.Id);
                        continue;
                    }

                    // 1F3b spec §3.2 and the 2026-09-28 ruling: replacing is a write, on the item as it is now,
                    // that never takes it from another adult.
                    var refusal = await ImportSectionKit.ReplaceRefusalAsync(policy, rules, context.Actor, id,
                        await items.FindAsync(id, ct), parsed.Visibility, parsed.Owners, null, "subscription", path,
                        async () => adults ??= await persons.AdultIdsAsync(ct), ct);
                    if (refusal is not null)
                    {
                        issues.Add(refusal);
                        failed.Add(record.Id);
                        continue;
                    }

                    if (ProviderKeyUnresolved(record.ProviderKey, linkable, issues, path))
                    {
                        failed.Add(record.Id);
                        continue;
                    }

                    replacedSubscriptions.Add((entity, record, id));
                    replacedIds.Add(record.Id);
                    existing.Add(record.Id);
                    tally[Subscriptions] += new SectionCounts(0, 0, 0, 1);
                }
                else
                {
                    existing.Add(record.Id);
                    tally[Subscriptions] += new SectionCounts(0, 1, 0);
                }
            }
            else
            {
                if (!rules.CanCreate(context.Actor, parsed.Owners))
                {
                    issues.Add(ImportIssue.Error(ImportCodes.NotPermitted,
                        "An item is imported by one of its owners, unless every owner is a dependent.", path));
                    failed.Add(record.Id);
                    continue;
                }

                if (ProviderKeyUnresolved(record.ProviderKey, linkable, issues, path))
                {
                    failed.Add(record.Id);
                    continue;
                }

                id = ItemId.New(); // planned; ApplyAsync replaces it with the real one
                newSubscriptions.Add((entity, record));
                context.NewItems.Add(id); // writable by construction, for the sections that point at it (bank_links)
                tally[Subscriptions] += new SectionCounts(1, 0, 0);
            }

            context.Items[(Subscriptions, record.Id)] = id;
            paths[id.ToString()] = path;
            runs[record.Id] = new DryRun(id, path, record.AutoRenew, parsed.CancelledAt);
        }

        // A subscription imported before is walked from what is stored, plus what this file adds to it: an
        // unchanged file row is already among the stored ones.
        var stored = await store.LoadManyAsync(existing.Select(e => runs[e].Id).ToList(), ct);
        foreach (var input in stored)
        {
            var run = runs.Values.Single(r => r.Id == input.Subscription.Id);
            run.Phases.AddRange(input.Phases);
            run.Purchases.AddRange(input.Purchases);
        }

        foreach (var (entity, record) in phaseRecords)
        {
            var path = NidDataJson.Path(Phases, entity.Index);
            if (failed.Contains(record.SubscriptionId))
                continue;
            if (!runs.TryGetValue(record.SubscriptionId, out var run))
            {
                issues.Add(ImportIssue.Error(ImportCodes.UnresolvedReference, $"'{record.SubscriptionId}' is not in subscriptions.", path + ".subscription_id"));
                continue;
            }
            if (!RecordParsing.TryPhase(record, path, person, issues, out var phase))
                continue;

            var resolvedPhase = await ResolveAsync(context.Actor, knownPhases, record.Id, readable, ct,
                candidate => candidate.Items.Contains(run.Id));
            if (resolvedPhase.OwnHidden)
            {
                issues.Add(ImportedUnseen(path, record.Id));
                continue;
            }

            if (resolvedPhase.Row is { } known)
            {
                if (known.Hash != entity.Hash)
                {
                    if (!context.Replace)
                    {
                        issues.Add(Conflict(path, record.Id));
                        tally[Phases] += new SectionCounts(0, 0, 1);
                        continue;
                    }

                    // 1F3b spec §3.3: a phase is replaced under its subscription; it never moves to another one.
                    if (!known.Items.Contains(run.Id))
                    {
                        issues.Add(ImportIssue.Error(ImportCodes.InvalidValue,
                            $"'{record.Id}' was imported under another subscription; a replaced phase stays under its own.", path + ".subscription_id"));
                        continue;
                    }

                    if (!await CanWriteAsync(context.Actor, run.Id, ct))
                    {
                        issues.Add(ImportIssue.Error(ImportCodes.NotPermitted, "This replaces a phase of a subscription you cannot change.", path));
                        continue;
                    }

                    var replacement = phase with { Id = new BillingPhaseId(known.Id) };
                    run.Phases.RemoveAll(p => p.Id == replacement.Id); // the stored version leaves the dry run
                    run.Phases.Add(replacement);
                    run.Added.Add((replacement, path));
                    paths[replacement.Id.ToString()] = path;
                    replacedPhases.Add((entity, record, replacement.Id));
                    tally[Phases] += new SectionCounts(0, 0, 0, 1);
                    continue;
                }
                tally[Phases] += new SectionCounts(0, 1, 0);
                if (!existing.Contains(record.SubscriptionId))
                    run.Phases.Add(phase); // stored under another item: not among this run's stored rows
                continue;
            }
            else
            {
                if (existing.Contains(record.SubscriptionId) && !await CanWriteAsync(context.Actor, run.Id, ct))
                {
                    issues.Add(NotWritable(path + ".subscription_id"));
                    continue;
                }
                if (!WholePeriods(phase))
                    issues.Add(ImportIssue.Warning(ImportCodes.PhaseNotWholePeriods,
                        "The phase does not last a whole number of periods: check its dates.", path));
                newPhases.Add((entity, record));
                tally[Phases] += new SectionCounts(1, 0, 0);
            }

            paths[phase.Id.ToString()] = path;
            run.Phases.Add(phase);
            run.Added.Add((phase, path));
        }

        foreach (var (entity, record) in purchaseRecords)
        {
            var path = NidDataJson.Path(Purchases, entity.Index);
            if (record.Grants.Any(g => failed.Contains(g.SubscriptionId)))
                continue;
            if (!RecordParsing.TryPurchase(record, path, person, fileId => runs.TryGetValue(fileId, out var r) ? r.Id : null, issues, out var purchase))
                continue;

            var targets = record.Grants.Select(g => g.SubscriptionId).Distinct(StringComparer.Ordinal).ToList();
            var targetIds = targets.Select(t => runs[t].Id).ToHashSet();
            var resolvedPurchase = await ResolveAsync(context.Actor, knownPurchases, record.Id, readable, ct,
                candidate => candidate.Items.Any(targetIds.Contains));
            if (resolvedPurchase.OwnHidden)
            {
                issues.Add(ImportedUnseen(path, record.Id));
                continue;
            }

            if (resolvedPurchase.Row is { } known)
            {
                if (known.Hash != entity.Hash)
                {
                    if (!context.Replace)
                    {
                        issues.Add(Conflict(path, record.Id));
                        tally[Purchases] += new SectionCounts(0, 0, 1);
                        continue;
                    }

                    // 1F3b spec §3.2: a write on every item the purchase touches now and every existing one the file
                    // gives it. A subscription this file creates has no item yet, and nothing to check (as for a new purchase).
                    var writable = true;
                    foreach (var item in known.Items.Concat(targets.Where(existing.Contains).Select(t => runs[t].Id)).Distinct())
                        if (!await CanWriteAsync(context.Actor, item, ct))
                        {
                            writable = false;
                            break;
                        }
                    if (!writable)
                    {
                        issues.Add(ImportIssue.Error(ImportCodes.NotPermitted, "This replaces a purchase on a subscription you cannot change.", path + ".grants"));
                        continue;
                    }

                    // The stored version leaves every dry run of this file. An item it granted that the file does
                    // not hold is not walked again: the purchase only leaves it.
                    var replacement = purchase with { Id = new PurchaseId(known.Id) };
                    foreach (var other in runs.Values)
                        other.Purchases.RemoveAll(p => p.Id == replacement.Id);
                    paths[replacement.Id.ToString()] = path;
                    foreach (var target in targets)
                    {
                        runs[target].Purchases.Add(replacement);
                        runs[target].Added.Add((replacement, path));
                    }
                    replacedPurchases.Add((entity, record, replacement.Id));
                    tally[Purchases] += new SectionCounts(0, 0, 0, 1);
                    continue;
                }
                tally[Purchases] += new SectionCounts(0, 1, 0);
                foreach (var target in targets.Where(t => !existing.Contains(t)))
                    runs[target].Purchases.Add(purchase); // stored, but not under this run's item
                continue;
            }
            else
            {
                var refused = false;
                foreach (var target in targets.Where(existing.Contains))
                    if (!await CanWriteAsync(context.Actor, runs[target].Id, ct))
                        refused = true;
                if (refused)
                {
                    issues.Add(NotWritable(path + ".grants"));
                    continue;
                }
                newPurchases.Add((entity, record));
                tally[Purchases] += new SectionCounts(1, 0, 0);
            }

            paths[purchase.Id.ToString()] = path;
            foreach (var target in targets)
            {
                runs[target].Purchases.Add(purchase);
                runs[target].Added.Add((purchase, path));
            }
        }

        // The engine's own validation and findings, on what the subscription will hold once committed.
        var horizon = Horizons.Resolve(context.Today, null);
        var checkedRuns = new List<(DryRun Run, Timeline Timeline)>();
        foreach (var run in runs.Values)
        {
            var built = SubscriptionTimeline.Build(run.Input(run.Phases, run.Purchases), context.Today, horizon);
            if (built.TryGetFailure(out var failure))
            {
                issues.Add(ImportIssue.Error(failure!.Code, failure.Message,
                    PathOf(failure, paths) ?? Culprit(run, context.Today, horizon) ?? run.Path));
                continue;
            }

            built.TryGetValue(out var timeline);
            checkedRuns.Add((run, timeline!));
            // horizon_reached only says an open phase keeps renewing: true of every live subscription, and a
            // concern of reads (1C1 design §5), not of an import.
            foreach (var finding in timeline!.Warnings.Where(w => w.Code != "subscriptions.horizon_reached"))
                issues.Add(ImportIssue.Warning(finding.Code, finding.Message,
                    finding.Reference is { } reference && paths.TryGetValue(reference, out var at) ? at : run.Path));
        }

        // 1C2a design §5.4: a payment in another currency with no rate at its date is worth saying before the
        // commit — the backfill has not run yet, or neither source covers that currency. Never blocking.
        var referenceCurrency = await household.ReferenceCurrencyAsync(ct);
        var paid = checkedRuns
            .SelectMany(r => r.Timeline.Charges.Where(c => !c.IsProjected).Select(c => (r.Run, Charge: c)))
            .ToList();
        var need = FxConversion.Need(paid.Select(p => Piece(p.Charge)), referenceCurrency);
        if (need is not null)
        {
            var table = await fx.LoadAsync(need.Quotes, need.From, need.To, ct);
            var reported = new HashSet<(string Path, FxMiss Miss)>();
            foreach (var (run, charge) in paid)
            {
                if (table.TryConvert(charge.Price.Amount, charge.Price.Currency, referenceCurrency, charge.Date, false, out _, out var miss))
                    continue;

                var origin = charge.Origin switch
                {
                    ChargeOrigin.FromPurchase p => p.PurchaseId.ToString(),
                    ChargeOrigin.FromPhase p => p.PhaseId.ToString(),
                    _ => null,
                };
                var at = origin is not null && paths.TryGetValue(origin, out var found) ? found : run.Path;
                if (reported.Add((at, miss)))
                    issues.Add(ImportIssue.Warning(FxCodes.RateMissing,
                        $"No {miss.Currency.Code} rate on or within {table.MaxGapDays} days before {LocalDatePattern.Iso.Format(miss.Date)}: the totals of this payment stay in {charge.Price.Currency.Code}.",
                        at));
            }
        }

        counts = tally;
        return new SectionOutcome(issues, tally);
    }

    public async Task<Result<IReadOnlyDictionary<string, SectionCounts>>> ApplyAsync(ImportContext context, IStagedBatches staged, CancellationToken ct)
    {
        var issues = new List<ImportIssue>();
        var person = Persons(context);

        var providersCreated = await providerImport.ApplyAsync(context, ct);
        if (providersCreated.TryGetFailure(out var providerFailure))
            return Result.Failure<IReadOnlyDictionary<string, SectionCounts>>(providerFailure!);

        foreach (var (entity, record) in newSubscriptions)
        {
            var path = NidDataJson.Path(Subscriptions, entity.Index);
            if (!RecordParsing.TrySubscription(record, path, r => context.Persons.GetValueOrDefault(r), issues, out var parsed))
                return ImportSectionKit.Drift(issues);

            var created = await items.CreateAsync(new NewItem(
                "subscriptions", "subscription", record.Name, parsed.Visibility, null,
                parsed.Owners.Select(o => new OwnerShare(o.PersonId, o.ShareBp)).ToList()), ct);
            if (!created.TryGetValue(out var id))
                return ImportSectionKit.FailureOf(created);

            context.NewItems.Remove(context.Items[(Subscriptions, record.Id)]);
            context.NewItems.Add(id);
            context.Items[(Subscriptions, record.Id)] = id;
            var stored = await store.CreateAsync(id, new NewSubscription(
                providerImport.StoredKey(record.ProviderKey), record.AutoRenew, parsed.Users, record.Note, parsed.CancelledAt, new ImportRef(record.Id, entity.Hash, context.Actor), parsed.Terms), ct);
            if (stored.TryGetFailure(out var failure))
                return Result.Failure<IReadOnlyDictionary<string, SectionCounts>>(failure!);
        }

        foreach (var (entity, record, id) in replacedSubscriptions)
        {
            var path = NidDataJson.Path(Subscriptions, entity.Index);
            if (!RecordParsing.TrySubscription(record, path, r => context.Persons.GetValueOrDefault(r), issues, out var parsed))
                return ImportSectionKit.Drift(issues);

            var item = await items.ReplaceAsync(id, record.Name, parsed.Visibility,
                parsed.Owners.Select(o => new OwnerShare(o.PersonId, o.ShareBp)).ToList(), ct);
            if (item.TryGetFailure(out var itemFailure))
                return Result.Failure<IReadOnlyDictionary<string, SectionCounts>>(itemFailure!);

            var stored = await store.ReplaceAsync(id, new SubscriptionReplacement(
                providerImport.StoredKey(record.ProviderKey), record.AutoRenew, parsed.Users, record.Note, parsed.CancelledAt,
                new ImportRef(record.Id, entity.Hash, context.Actor), parsed.Terms), ct);
            if (stored.TryGetFailure(out var failure))
                return Result.Failure<IReadOnlyDictionary<string, SectionCounts>>(failure!);
        }

        foreach (var (entity, record, phaseId) in replacedPhases)
        {
            if (!RecordParsing.TryPhase(record, NidDataJson.Path(Phases, entity.Index), person, issues, out var phase))
                return ImportSectionKit.Drift(issues);
            var replaced = await store.ReplacePhaseAsync(context.Items[(Subscriptions, record.SubscriptionId)], phase with { Id = phaseId },
                new RowOrigin(record.Note, new ImportRef(record.Id, entity.Hash, context.Actor)), ct);
            if (replaced.TryGetFailure(out var failure))
                return Result.Failure<IReadOnlyDictionary<string, SectionCounts>>(failure!);
        }

        foreach (var (entity, record, purchaseId) in replacedPurchases)
        {
            if (!RecordParsing.TryPurchase(record, NidDataJson.Path(Purchases, entity.Index), person,
                    fileId => context.Items.TryGetValue((Subscriptions, fileId), out var id) ? id : null, issues, out var purchase))
                return ImportSectionKit.Drift(issues);
            var replaced = await store.ReplacePurchaseAsync(purchase with { Id = purchaseId },
                new RowOrigin(record.Note, new ImportRef(record.Id, entity.Hash, context.Actor)), ct);
            if (replaced.TryGetFailure(out var failure))
                return Result.Failure<IReadOnlyDictionary<string, SectionCounts>>(failure!);
        }

        foreach (var (entity, record) in newPhases)
        {
            if (!RecordParsing.TryPhase(record, NidDataJson.Path(Phases, entity.Index), person, issues, out var phase))
                return ImportSectionKit.Drift(issues);
            var added = await store.AddPhaseAsync(context.Items[(Subscriptions, record.SubscriptionId)], phase,
                new RowOrigin(record.Note, new ImportRef(record.Id, entity.Hash, context.Actor)), ct);
            if (added.TryGetFailure(out var failure))
                return Result.Failure<IReadOnlyDictionary<string, SectionCounts>>(failure!);
        }

        foreach (var (entity, record) in newPurchases)
        {
            if (!RecordParsing.TryPurchase(record, NidDataJson.Path(Purchases, entity.Index), person,
                    fileId => context.Items.TryGetValue((Subscriptions, fileId), out var id) ? id : null, issues, out var purchase))
                return ImportSectionKit.Drift(issues);
            var added = await store.AddPurchaseAsync(purchase, new RowOrigin(record.Note, new ImportRef(record.Id, entity.Hash, context.Actor)), ct);
            if (added.TryGetFailure(out var failure))
                return Result.Failure<IReadOnlyDictionary<string, SectionCounts>>(failure!);
        }

        return Result.Success(counts);
    }

    public Task<IReadOnlyDictionary<string, IReadOnlyList<JsonElement>>> ExportAsync(ExportContext context, CancellationToken ct)
        => new SubscriptionsExport(visible, store, items, providers).ExportAsync(context, ct);

    private sealed record DryRun(ItemId Id, string Path, bool AutoRenew, LocalDate? CancelledAt)
    {
        public List<BillingPhase> Phases { get; } = [];
        public List<Purchase> Purchases { get; } = [];

        /// <summary>What this file adds, in file order, with the path of each: phases and purchases.</summary>
        public List<(object Row, string Path)> Added { get; } = [];

        public SubscriptionInput Input(IEnumerable<BillingPhase> phases, IReadOnlyList<Purchase> purchases)
            => new(new SubscriptionHeader(Id, AutoRenew, CancelledAt), phases.OrderBy(p => p.StartsOn).ToList(), purchases);
    }

    /// <summary>
    /// The engine refused a subscription on a row this file did not add — a stored one, whose partner in the
    /// fault is new. Adds the new rows one at a time to what was there before; the first the engine refuses
    /// is the one to report. Runs only on a refusal, so its cost is never paid on a valid file.
    /// </summary>
    private static string? Culprit(DryRun run, LocalDate today, LocalDate horizon)
    {
        var added = run.Added.Select(a => a.Row).ToHashSet();
        var phases = run.Phases.Where(p => !added.Contains(p)).ToList();
        var purchases = run.Purchases.Where(p => !added.Contains(p)).ToList();
        foreach (var (row, path) in run.Added)
        {
            if (row is BillingPhase phase)
                phases.Add(phase);
            else
                purchases.Add((Purchase)row);
            if (SubscriptionTimeline.Build(run.Input(phases, purchases), today, horizon).TryGetFailure(out _))
                return path;
        }
        return null;
    }

    private static SubscriptionHeader Header(ItemId id) => new(id, true, null);

    private static Func<string, PersonId?> Persons(ImportContext context)
        => reference => context.Persons.TryGetValue(reference, out var resolved) ? resolved.Id : null;

    /// <summary>The engine's hard rules on one entity, checked alone: its reference is the entity itself.</summary>
    private static void EngineRules(SubscriptionInput input, LocalDate day, string path, List<ImportIssue> issues)
    {
        if (SubscriptionTimeline.Build(input, day, day).TryGetFailure(out var failure))
            issues.Add(ImportIssue.Error(failure!.Code, failure.Message, path));
    }

    private static string? PathOf(Failure failure, IReadOnlyDictionary<string, string> paths)
        => failure.Metadata is { } metadata && metadata.TryGetValue("reference", out var reference)
           && reference is string key && paths.TryGetValue(key, out var path)
            ? path
            : null;

    private static bool WholePeriods(BillingPhase phase)
    {
        if (phase.EndsOn is not { } end)
            return true;
        for (var k = 1; k <= SubscriptionTimeline.MaxCyclesPerPhase; k++)
        {
            var renewal = phase.Every.AddTo(phase.StartsOn, k);
            if (renewal == end)
                return true;
            if (renewal > end)
                return false;
        }
        return false;
    }

    /// <summary>What a file id resolves to: a row the importer sees, their own row they no longer see, or nothing.</summary>
    private sealed record Resolution(ImportedRow? Row, bool OwnHidden)
    {
        public static Resolution New { get; } = new(null, false);
    }

    /// <summary>
    /// Import provenance privacy (spec 2026-09-25 §4), decided by <see cref="ImportResolver"/>. Here <paramref name="belongs"/>
    /// narrows the rows of others to those under the subscriptions this file resolved to: a phase or purchase of another
    /// adult's same-id subscription is not this file's. <paramref name="readable"/> keeps one policy check per item.
    /// </summary>
    private async Task<Resolution> ResolveAsync(
        PersonId actor, IReadOnlyDictionary<string, IReadOnlyList<ImportedRow>> known, string fileId,
        Dictionary<ItemId, bool> readable, CancellationToken ct, Func<ImportedRow, bool>? belongs = null)
    {
        if (!known.TryGetValue(fileId, out var rows))
            return Resolution.New;

        var candidates = rows.Select(r => new ImportCandidate(r.Id, r.Hash, r.ImportedBy, r.Items)).ToList();
        var byId = rows.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First());

        var resolution = await ImportResolver.ResolveAsync(
            actor, candidates, ImportSectionKit.Readable(policy, actor, readable, ct), belongs is null ? null : c => belongs(byId[c.Id]));
        return new Resolution(resolution.Row is { } row ? byId[row.Id] : null, resolution.OwnHidden);
    }

    private static ImportIssue ImportedUnseen(string path, string id)
        => ImportIssue.Error(ImportCodes.NotPermitted, $"'{id}' was imported before as an item you cannot see.", path);

    private Task<bool> CanWriteAsync(PersonId actor, ItemId item, CancellationToken ct)
        => ImportSectionKit.CanAsync(policy, actor, item, AccessKind.Write, ct);

    private static ImportIssue Conflict(string path, string id)
        => ImportIssue.Error(ImportCodes.Conflict,
            $"'{id}' was imported before with other content; nothing is overwritten (import-format.md §8).", path);

    private static ImportIssue NotWritable(string path)
        => ImportIssue.Error(ImportCodes.NotPermitted, "This adds to a subscription you cannot change.", path);

    /// <summary>
    /// True when <paramref name="providerKey"/> resolves neither to a provider nor to one the actor can link
    /// (a creation and a replacement both refuse on this). Adds the issue itself, unless the batch already
    /// failed to resolve this same key (<see cref="ProvidersImport.Failed"/>), so it is not reported twice.
    /// </summary>
    private bool ProviderKeyUnresolved(string? providerKey, IReadOnlySet<string> linkable, List<ImportIssue> issues, string path)
    {
        if (providerKey is null || providerImport.Resolves(providerKey, linkable))
            return false;
        if (!providerImport.Failed(providerKey))
            issues.Add(ImportIssue.Error(ImportCodes.UnresolvedReference,
                $"'{providerKey}' is neither in providers nor a provider you can link.", path + ".provider_key"));
        return true;
    }

    private static CostPiece Piece(Charge charge) => new(charge.Date, charge.Price.Currency, charge.Price.Amount, charge.IsProjected);
}
