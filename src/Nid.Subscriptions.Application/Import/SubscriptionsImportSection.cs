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
/// engine, never from here.
/// </summary>
public sealed class SubscriptionsImportSection(
    IItemRegistry items, ISubscriptionStore store, IAccessPolicy policy, IItemRules rules, IFxRates fx, IHouseholdSettings household,
    IProviderStore providers, ProviderAccess providerAccess, ProviderKeys providerKeys)
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
        // Every file ref stands for someone, one distinct stand-in per ref, so a ref repeated is still seen.
        var standIns = new Dictionary<string, ResolvedPerson>(StringComparer.Ordinal);
        ResolvedPerson StandIn(string fileRef)
            => standIns.TryGetValue(fileRef, out var standIn) ? standIn : standIns[fileRef] = new ResolvedPerson(PersonId.New(), PersonKind.Adult);
        var position = 0;
        foreach (var entity in entities.EnumerateArray())
        {
            var path = NidDataJson.Path(section, firstIndex + position++);
            switch (section)
            {
                case Subscriptions:
                    if (NidDataJson.TryRead<SubscriptionRecord>(entity, path, issues, out var subscription)
                        && RecordParsing.TryId(subscription.Id, path, issues)
                        && RecordParsing.TryElements(subscription, path, issues))
                        RecordParsing.TrySubscription(subscription, path, StandIn, issues, out _);
                    break;
                case Phases:
                    if (NidDataJson.TryRead<BillingPhaseRecord>(entity, path, issues, out var phaseRecord)
                        && RecordParsing.TryId(phaseRecord.Id, path, issues)
                        && RecordParsing.TryPhase(phaseRecord, path, anyone, issues, out var phase))
                        EngineRules(new SubscriptionInput(Header(Placeholder), [phase], []), phase.StartsOn, path, issues);
                    break;
                case Purchases:
                    if (NidDataJson.TryRead<PurchaseRecord>(entity, path, issues, out var purchaseRecord)
                        && RecordParsing.TryId(purchaseRecord.Id, path, issues)
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
        var issues = new List<ImportIssue>();

        // Before subscriptions: their provider_key may name an entry of this section (design §5 "Import").
        var providerCounts = await providerImport.ValidateAllAsync(context, staged, issues, ct);

        var subscriptionRecords = ReadAll<SubscriptionRecord>(staged, Subscriptions, r => r.Id, issues);
        var phaseRecords = ReadAll<BillingPhaseRecord>(staged, Phases, r => r.Id, issues);
        var purchaseRecords = ReadAll<PurchaseRecord>(staged, Purchases, r => r.Id, issues);

        var knownSubscriptions = First(await store.FindImportedAsync(ImportedTable.Subscription, subscriptionRecords.Select(s => s.Record.Id).ToList(), ct));
        var knownPhases = First(await store.FindImportedAsync(ImportedTable.BillingPhase, phaseRecords.Select(p => p.Record.Id).ToList(), ct));
        var knownPurchases = First(await store.FindImportedAsync(ImportedTable.Purchase, purchaseRecords.Select(p => p.Record.Id).ToList(), ct));

        // Task 1 only: keep 1D1's single-row lookup until Task 2 resolves by visibility.
        static IReadOnlyDictionary<string, ImportedRow> First(IReadOnlyDictionary<string, IReadOnlyList<ImportedRow>> found)
            => found.ToDictionary(f => f.Key, f => f.Value[0], StringComparer.Ordinal);

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
            if (knownSubscriptions.TryGetValue(record.Id, out var known))
            {
                // Nothing is created, so the creation rule does not apply: an unchanged record only has to be
                // one the actor can see. What the file adds to it is checked for write access below.
                if (!await CanAccessAsync(context.Actor, new ItemId(known.Id), AccessKind.Read, ct))
                {
                    issues.Add(ImportIssue.Error(ImportCodes.NotPermitted,
                        $"'{record.Id}' was imported before as an item you cannot see.", path));
                    failed.Add(record.Id);
                    continue;
                }

                if (known.Hash != entity.Hash)
                {
                    issues.Add(Conflict(path, record.Id));
                    tally[Subscriptions] += new SectionCounts(0, 0, 1);
                    failed.Add(record.Id);
                    continue;
                }

                id = new ItemId(known.Id);
                existing.Add(record.Id);
                tally[Subscriptions] += new SectionCounts(0, 1, 0);
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

                if (record.ProviderKey is { } providerKey && !providerImport.Resolves(providerKey, linkable))
                {
                    if (!providerImport.Failed(providerKey))
                        issues.Add(ImportIssue.Error(ImportCodes.UnresolvedReference,
                            $"'{providerKey}' is neither in providers nor a provider you can link.", path + ".provider_key"));
                    failed.Add(record.Id);
                    continue;
                }

                id = ItemId.New(); // planned; ApplyAsync replaces it with the real one
                newSubscriptions.Add((entity, record));
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

            if (knownPhases.TryGetValue(record.Id, out var known))
            {
                if (known.Hash != entity.Hash)
                {
                    issues.Add(Conflict(path, record.Id));
                    tally[Phases] += new SectionCounts(0, 0, 1);
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
            if (knownPurchases.TryGetValue(record.Id, out var known))
            {
                if (known.Hash != entity.Hash)
                {
                    issues.Add(Conflict(path, record.Id));
                    tally[Purchases] += new SectionCounts(0, 0, 1);
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
                return Drift(issues);

            var created = await items.CreateAsync(new NewItem(
                "subscriptions", "subscription", record.Name, parsed.Visibility, null,
                parsed.Owners.Select(o => new OwnerShare(o.PersonId, o.ShareBp)).ToList()), ct);
            if (!created.TryGetValue(out var id))
                return FailureOf(created);

            context.Items[(Subscriptions, record.Id)] = id;
            var stored = await store.CreateAsync(id, new NewSubscription(
                providerImport.StoredKey(record.ProviderKey), record.AutoRenew, parsed.Users, record.Note, parsed.CancelledAt, new ImportRef(record.Id, entity.Hash)), ct);
            if (stored.TryGetFailure(out var failure))
                return Result.Failure<IReadOnlyDictionary<string, SectionCounts>>(failure!);
        }

        foreach (var (entity, record) in newPhases)
        {
            if (!RecordParsing.TryPhase(record, NidDataJson.Path(Phases, entity.Index), person, issues, out var phase))
                return Drift(issues);
            var added = await store.AddPhaseAsync(context.Items[(Subscriptions, record.SubscriptionId)], phase,
                new RowOrigin(record.Note, new ImportRef(record.Id, entity.Hash)), ct);
            if (added.TryGetFailure(out var failure))
                return Result.Failure<IReadOnlyDictionary<string, SectionCounts>>(failure!);
        }

        foreach (var (entity, record) in newPurchases)
        {
            if (!RecordParsing.TryPurchase(record, NidDataJson.Path(Purchases, entity.Index), person,
                    fileId => context.Items.TryGetValue((Subscriptions, fileId), out var id) ? id : null, issues, out var purchase))
                return Drift(issues);
            var added = await store.AddPurchaseAsync(purchase, new RowOrigin(record.Note, new ImportRef(record.Id, entity.Hash)), ct);
            if (added.TryGetFailure(out var failure))
                return Result.Failure<IReadOnlyDictionary<string, SectionCounts>>(failure!);
        }

        return Result.Success(counts);
    }

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

    private static List<(StagedEntity Entity, T Record)> ReadAll<T>(IStagedBatches staged, string section, Func<T, string> id, List<ImportIssue> issues)
        where T : class
    {
        var records = new List<(StagedEntity, T)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entity in staged.Section(section))
        {
            var path = NidDataJson.Path(section, entity.Index);
            if (!NidDataJson.TryRead<T>(entity.Value, path, issues, out var record) || !RecordParsing.TryId(id(record), path, issues)
                || !RecordParsing.TryElements(record, path, issues))
                continue;
            if (!seen.Add(id(record)))
            {
                issues.Add(ImportIssue.Error(ImportCodes.DuplicateId, $"'{id(record)}' appears twice in {section}.", path + ".id"));
                continue;
            }
            records.Add((entity, record));
        }
        return records;
    }

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

    private Task<bool> CanWriteAsync(PersonId actor, ItemId item, CancellationToken ct)
        => CanAccessAsync(actor, item, AccessKind.Write, ct);

    private async Task<bool> CanAccessAsync(PersonId actor, ItemId item, AccessKind kind, CancellationToken ct)
        => !(await policy.CheckAsync(actor, item, kind, ct)).TryGetFailure(out _);

    private static ImportIssue Conflict(string path, string id)
        => ImportIssue.Error(ImportCodes.Conflict,
            $"'{id}' was imported before with other content; nothing is overwritten (import-format.md §8).", path);

    private static ImportIssue NotWritable(string path)
        => ImportIssue.Error(ImportCodes.NotPermitted, "This adds to a subscription you cannot change.", path);

    /// <summary>What validated a moment ago no longer parses: refuse rather than write half of it.</summary>
    private static Result<IReadOnlyDictionary<string, SectionCounts>> Drift(List<ImportIssue> issues)
        => Result.Failure<IReadOnlyDictionary<string, SectionCounts>>(new Failure(ImportCodes.ApplyFailed,
            $"The staging changed between validation and writing: {string.Join("; ", issues.Select(i => $"{i.Code} at {i.Path}"))}", null));

    private static Result<IReadOnlyDictionary<string, SectionCounts>> FailureOf<T>(Result<T> result) where T : notnull
        => Result.Failure<IReadOnlyDictionary<string, SectionCounts>>(
            result.TryGetFailure(out var failure) ? failure! : new Failure(ImportCodes.ApplyFailed, "A write failed.", null));

    private static CostPiece Piece(Charge charge) => new(charge.Date, charge.Price.Currency, charge.Price.Amount, charge.IsProjected);
}
