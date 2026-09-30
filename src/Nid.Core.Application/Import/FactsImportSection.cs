using System.Text.Json;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using NodaTime.Text;
using UnambitiousFx.Functional;

namespace Nid.Core.Application.Import;

/// <summary>
/// The Core's sections <c>valuations</c> and <c>flows</c> (2B1b spec §5). A fact's <c>item</c> names an entity of any
/// section that creates items, through <see cref="ImportContext.Items"/>; writes go through the same
/// <see cref="IValuations"/> and <see cref="IFlows"/> as the API, whose rules are <see cref="Nid.Core.Domain.FactRules"/>.
/// A file id resolves only among facts the importer can see (<see cref="ImportResolver"/>): another adult's fact the
/// importer cannot see answers exactly as an unused id. Nothing is replaced silently: a new fact that meets a current
/// one the file does not name is a conflict, which <c>replace</c> corrects through the <c>supersedes_id</c> chain.
/// </summary>
public sealed class FactsImportSection(
    IValuations valuations, IFlows flows, IFactImports facts, IAccessPolicy policy, IHouseholdCalendar calendar) : IImportSection
{
    public const string ValuationsSection = "valuations";
    public const string FlowsSection = "flows";

    /// <summary>A write planned by the validation: a new fact, or the correction of the current fact <see cref="Supersedes"/>.</summary>
    private sealed record Planned(ParsedFact Fact, Guid? Supersedes);

    /// <summary>A current stored fact, by what makes two facts of one item meet (<see cref="Keys"/>).</summary>
    private sealed record StoredKeys(Guid Id, ItemId Item, IReadOnlyList<string> Keys);

    private readonly List<Planned> plannedValues = [];
    private readonly List<Planned> plannedFlows = [];
    private IReadOnlyDictionary<string, SectionCounts> counts = new Dictionary<string, SectionCounts>();

    public string Module => "core";
    public IReadOnlyList<string> Sections => [ValuationsSection, FlowsSection];
    public int Order => 90;

    public IReadOnlyList<ImportIssue> ValidateBatch(string section, JsonElement entities, int firstIndex)
    {
        var issues = new List<ImportIssue>();
        var position = 0;
        foreach (var value in entities.EnumerateArray())
        {
            var index = firstIndex + position++;
            var path = NidDataJson.Path(section, index);
            var entity = new StagedEntity(index, value, "");
            if (section == ValuationsSection)
            {
                if (NidDataJson.TryRead<ValuationRecord>(value, path, issues, out var record) && ImportSectionKit.TryId(record.Id, path, issues))
                    FactParsing.TryValuation(entity, record, path, currency: null, today: null, issues, out _);
            }
            else if (NidDataJson.TryRead<FlowRecord>(value, path, issues, out var record) && ImportSectionKit.TryId(record.Id, path, issues))
                FactParsing.TryFlow(entity, record, path, currency: null, today: null, issues, out _);
        }
        return issues;
    }

    public async Task<SectionOutcome> ValidateAllAsync(ImportContext context, IStagedBatches staged, CancellationToken ct)
    {
        plannedValues.Clear();
        plannedFlows.Clear();
        var issues = new List<ImportIssue>();
        var today = await calendar.TodayAsync(ct); // the household's, as the API and the envelopes decide it
        var access = new Access(policy, context.Actor, ct);

        var values = await ValidateSectionAsync(FactTable.Valuation, context, staged, today, access, issues, plannedValues, ct);
        var flowCounts = await ValidateSectionAsync(FactTable.Flow, context, staged, today, access, issues, plannedFlows, ct);

        counts = new Dictionary<string, SectionCounts> { [ValuationsSection] = values, [FlowsSection] = flowCounts };
        return new SectionOutcome(issues, counts);
    }

    public async Task<Result<IReadOnlyDictionary<string, SectionCounts>>> ApplyAsync(ImportContext context, IStagedBatches staged, CancellationToken ct)
    {
        // Corrections first: a new fact may take the date, the opening or the reference a correction moves away from.
        foreach (var planned in CorrectionsFirst(plannedValues))
        {
            if (ItemAtApply(context, planned.Fact) is not { } item)
                return Moved(planned.Fact);
            var value = planned.Fact.ToValuation(item, Mark(context, planned.Fact));
            var result = planned.Supersedes is { } old
                ? await valuations.SupersedeAsync(item, old, value, ct)
                : await valuations.RecordAsync(value, ct);
            if (!result.IsSuccess)
                return ImportSectionKit.FailureOf(result);
        }

        foreach (var planned in CorrectionsFirst(plannedFlows))
        {
            if (ItemAtApply(context, planned.Fact) is not { } item)
                return Moved(planned.Fact);
            var flow = planned.Fact.ToFlow(item, Mark(context, planned.Fact));
            var result = planned.Supersedes is { } old
                ? await flows.SupersedeAsync(item, old, flow, ct)
                : await flows.RecordAsync(flow, ct);
            if (!result.IsSuccess)
                return ImportSectionKit.FailureOf(result);
        }

        return Result.Success(counts);
    }

    /// <summary>
    /// 2B1b spec §6: the current facts of every item an item section exported (which ran first and named them), by the
    /// item's file id, then date, then the fact's file id. Amounts are strings; absent members are written as null.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<JsonElement>>> ExportAsync(ExportContext context, CancellationToken ct)
    {
        var named = context.ItemFileIds;
        var items = named.Keys.ToList();
        var storedValues = items.Count == 0 ? [] : await facts.ValuationsOfAsync(items, ct);
        var storedFlows = items.Count == 0 ? [] : await facts.FlowsOfAsync(items, ct);
        var valueIds = FileIds.Assign("val",
            storedValues.Select(v => new KeyedRow(v.Valuation.Id, v.Import?.Ref, v.Import?.ImportedBy)).ToList(), context.Reader);
        var flowIds = FileIds.Assign("fl",
            storedFlows.Select(f => new KeyedRow(f.Flow.Id, f.Import?.Ref, f.Import?.ImportedBy)).ToList(), context.Reader);

        var values = storedValues
            .Select(s => s.Valuation)
            .Select(v => new ValuationRecord
            {
                Id = valueIds[v.Id],
                Item = named[v.Item],
                AsOf = LocalDatePattern.Iso.Format(v.AsOf),
                Amount = new MoneyRecord { Amount = v.Value.Amount, Currency = v.Value.Currency.Code },
                Note = v.Note,
            })
            .OrderBy(r => r.Item, StringComparer.Ordinal).ThenBy(r => r.AsOf, StringComparer.Ordinal).ThenBy(r => r.Id, StringComparer.Ordinal);
        var flowRecords = storedFlows
            .Select(s => s.Flow)
            .Select(f => new FlowRecord
            {
                Id = flowIds[f.Id],
                Item = named[f.Item],
                OccurredOn = LocalDatePattern.Iso.Format(f.OccurredOn),
                Kind = FactParsing.Wire(f.Kind),
                Amount = new MoneyRecord { Amount = f.Amount.Amount, Currency = f.Amount.Currency.Code },
                Category = f.Category,
                ExternalRef = f.ExternalRef,
                Note = f.Note,
            })
            .OrderBy(r => r.Item, StringComparer.Ordinal).ThenBy(r => r.OccurredOn, StringComparer.Ordinal).ThenBy(r => r.Id, StringComparer.Ordinal);

        return new Dictionary<string, IReadOnlyList<JsonElement>>
        {
            [ValuationsSection] = values.Select(r => JsonSerializer.SerializeToElement(r, NidDataJson.Options)).ToList(),
            [FlowsSection] = flowRecords.Select(r => JsonSerializer.SerializeToElement(r, NidDataJson.Options)).ToList(),
        };
    }

    private async Task<SectionCounts> ValidateSectionAsync(
        FactTable table, ImportContext context, IStagedBatches staged, LocalDate today, Access access,
        List<ImportIssue> issues, List<Planned> planned, CancellationToken ct)
    {
        var section = table == FactTable.Valuation ? ValuationsSection : FlowsSection;
        var records = table == FactTable.Valuation
            ? ImportSectionKit.ReadAll<ValuationRecord>(staged, section, r => r.Id, issues)
                .Select(r => (r.Entity, r.Record.Id, r.Record.Item, Record: (object)r.Record)).ToList()
            : ImportSectionKit.ReadAll<FlowRecord>(staged, section, r => r.Id, issues)
                .Select(r => (r.Entity, r.Record.Id, r.Record.Item, Record: (object)r.Record)).ToList();
        // Every importer's rows under these ids, across items: only ImportResolver's answer may reach an issue.
        var known = await facts.FindImportedAsync(table, records.Select(r => r.Id).ToList(), ct);

        var tally = SectionCounts.Zero;
        var inFile = new HashSet<(ItemId, string)>();
        var named = new HashSet<Guid>(); // stored facts this file names: a new fact does not meet them
        var pending = new List<(ParsedFact Fact, ItemId Item, Guid? Corrects)>();

        foreach (var (entity, id, itemRef, record) in records)
        {
            var path = NidDataJson.Path(section, entity.Index);

            // 2B1b spec §5: exactly one entity of the sections that create items; one refused is reported already.
            var (resolvedItem, matches, skip) = ItemOf(context, itemRef);
            if (skip)
                continue;
            if (resolvedItem is not { } item)
            {
                issues.Add(ImportIssue.Error(ImportCodes.UnresolvedReference, matches == 0
                    ? $"'{itemRef}' names no entity of a section that creates items."
                    : $"'{itemRef}' names two entities of sections that create items.", path + ".item"));
                continue;
            }
            if (context.ItemCurrencies.GetValueOrDefault(item) is not { } currency)
            {
                issues.Add(ImportIssue.Error(CoreFactCodes.ItemWithoutCurrency,
                    $"'{itemRef}' takes no values or flows: it has no currency.", path + ".item"));
                continue;
            }

            if (!TryParse(entity, record, path, currency, today, issues, out var fact))
                continue;

            // Duplicates in the file, at the second one: what the Core allows once per item.
            var keys = Keys(fact);
            if (keys.FirstOrDefault(k => inFile.Contains((item, k))) is { } repeated)
            {
                issues.Add(ImportIssue.Error(ImportCodes.DuplicateFact, $"This file already has {Describe(repeated)} for '{itemRef}'.", path));
                continue;
            }
            foreach (var key in keys)
                inFile.Add((item, key));

            var candidates = known.GetValueOrDefault(id)?
                .Select(k => new ImportCandidate(k.Id, k.Hash, k.ImportedBy, [k.Item])).ToList();
            var resolution = await ImportResolver.ResolveAsync(context.Actor, candidates, access.Readable, belongs: c => c.Items[0] == item);
            if (resolution.OwnHidden)
            {
                issues.Add(ImportIssue.Error(ImportCodes.NotPermitted, $"'{id}' was imported before as a fact you cannot see.", path));
                continue;
            }

            if (resolution.Row is { } row)
            {
                // Whatever the hash and the mode: a stored fact never moves to another item, nor is it taken as this file's.
                if (row.Items[0] != item)
                {
                    issues.Add(ImportIssue.Error(ImportCodes.InvalidValue,
                        "A fact stays on its item: give the fact of the other item an id of its own.", path + ".item"));
                    continue;
                }
                named.Add(row.Id);
                if (row.Hash == entity.Hash)
                {
                    tally += new SectionCounts(0, 1, 0);
                    continue;
                }
                if (!context.Replace)
                {
                    issues.Add(ImportIssue.Error(ImportCodes.Conflict,
                        $"'{id}' was imported before with other content; nothing is overwritten (import-format.md §8).", path));
                    tally += new SectionCounts(0, 0, 1);
                    continue;
                }
                if (!await access.WritableAsync(item))
                {
                    issues.Add(ImportIssue.Error(ImportCodes.NotPermitted, "This corrects a fact of an item you cannot change.", path));
                    continue;
                }
                pending.Add((fact, item, row.Id));
                continue;
            }

            // An item this import creates is writable by construction and has no stored facts.
            if (!context.NewItems.Contains(item) && !await access.WritableAsync(item))
            {
                issues.Add(ImportIssue.Error(ImportCodes.NotPermitted, "This adds to an item you cannot change.", path));
                continue;
            }
            pending.Add((fact, item, null));
        }

        // What exists: the current facts of every existing item these facts go to, read once. The items are ones the
        // importer reads (their entities resolved through ImportResolver), so what they hold may be named.
        var existing = pending.Select(p => p.Item).Where(i => !context.NewItems.Contains(i)).Distinct().ToList();
        var stored = (existing.Count == 0 ? [] : await StoredAsync(table, existing, ct)).ToLookup(s => s.Item);
        var corrected = new HashSet<Guid>();
        foreach (var (fact, item, corrects) in pending)
        {
            var keys = Keys(fact);
            var meets = stored[item]
                .Where(s => s.Id != corrects && (corrects is not null || !named.Contains(s.Id)) && s.Keys.Intersect(keys).Any())
                .ToList();

            if (corrects is { } old)
            {
                // One correction cannot retire two facts, even with replace.
                if (meets.Count > 0)
                {
                    issues.Add(ImportIssue.Error(ImportCodes.Conflict,
                        $"This correction meets another current fact of its item ({Describe(meets[0].Keys.Intersect(keys).First())}): one correction retires one fact.",
                        fact.Path));
                    continue;
                }
                corrected.Add(old);
                planned.Add(new Planned(fact, old));
                tally += new SectionCounts(0, 0, 0, 1);
                continue;
            }

            if (meets.Count == 0)
            {
                planned.Add(new Planned(fact, null));
                tally += new SectionCounts(1, 0, 0);
                continue;
            }

            var met = Describe(meets[0].Keys.Intersect(keys).First());
            if (!context.Replace)
            {
                issues.Add(ImportIssue.Error(ImportCodes.Conflict,
                    $"This item already has {met}, which this file does not name; nothing is overwritten (import-format.md §8).", fact.Path));
                tally += new SectionCounts(0, 0, 1);
                continue;
            }
            if (meets.Count > 1 || corrected.Contains(meets[0].Id))
            {
                issues.Add(ImportIssue.Error(ImportCodes.Conflict, meets.Count > 1
                    ? "This fact meets two current facts of its item: one correction retires one fact."
                    : $"Another fact of this file already corrects {met} of this item.", fact.Path));
                continue;
            }
            corrected.Add(meets[0].Id);
            planned.Add(new Planned(fact, meets[0].Id)); // the write access was checked above
            tally += new SectionCounts(0, 0, 0, 1);
        }

        return tally;
    }

    /// <summary>
    /// 2B1b spec §5: the item a fact names — exactly one entity of the sections that create items. Skipped when an
    /// entity of that id was refused: it is reported already.
    /// </summary>
    private static (ItemId? Item, int Matches, bool Skip) ItemOf(ImportContext context, string fileId)
    {
        if (context.FailedItems.Any(f => f.Id == fileId))
            return (null, 0, true);
        var matches = context.Items.Where(e => e.Key.Id == fileId).Select(e => e.Value).Distinct().ToList();
        return (matches.Count == 1 ? matches[0] : null, matches.Count, false);
    }

    /// <summary>At apply, the item is looked up again by its file id: the item sections have replaced planned ids with real ones.</summary>
    private static ItemId? ItemAtApply(ImportContext context, ParsedFact fact) => ItemOf(context, fact.Item) is { Skip: false, Item: { } item } ? item : null;

    private static Result<IReadOnlyDictionary<string, SectionCounts>> Moved(ParsedFact fact)
        => ImportSectionKit.Drift([ImportIssue.Error(ImportCodes.UnresolvedReference, $"'{fact.Item}' no longer names one item.", fact.Path + ".item")]);

    private static ImportMark Mark(ImportContext context, ParsedFact fact) => new(fact.Id, fact.Entity.Hash, context.Actor);

    private static IEnumerable<Planned> CorrectionsFirst(List<Planned> planned)
        => planned.Where(p => p.Supersedes is not null).Concat(planned.Where(p => p.Supersedes is null));

    private static bool TryParse(StagedEntity entity, object record, string path, Currency currency, LocalDate today,
        List<ImportIssue> issues, out ParsedFact fact)
    {
        if (record is ValuationRecord valuation)
            return FactParsing.TryValuation(entity, valuation, path, currency, today, issues, out fact);
        return FactParsing.TryFlow(entity, (FlowRecord)record, path, currency, today, issues, out fact);
    }

    /// <summary>
    /// What the Core allows once per current fact of an item (<c>core-data-model.md</c> §5): a value per date, one
    /// opening, a flow per external reference.
    /// </summary>
    private static IReadOnlyList<string> Keys(FactTable table, LocalDate date, FlowKind? kind, string? externalRef)
    {
        if (table == FactTable.Valuation)
            return ["date:" + LocalDatePattern.Iso.Format(date)];
        var keys = new List<string>(2);
        if (kind == FlowKind.Opening)
            keys.Add("opening");
        if (externalRef is not null)
            keys.Add("ref:" + externalRef);
        return keys;
    }

    private static IReadOnlyList<string> Keys(ParsedFact fact)
        => Keys(fact.Kind is null ? FactTable.Valuation : FactTable.Flow, fact.Date, fact.Kind, fact.ExternalRef);

    private static string Describe(string key)
        => key.StartsWith("date:", StringComparison.Ordinal) ? $"a value on {key[5..]}"
            : key == "opening" ? "an opening"
            : $"a flow with the reference '{key[4..]}'";

    private async Task<IReadOnlyList<StoredKeys>> StoredAsync(FactTable table, IReadOnlyCollection<ItemId> items, CancellationToken ct)
        => table == FactTable.Valuation
            ? (await facts.ValuationsOfAsync(items, ct))
                .Select(v => new StoredKeys(v.Valuation.Id, v.Valuation.Item, Keys(table, v.Valuation.AsOf, null, null))).ToList()
            : (await facts.FlowsOfAsync(items, ct))
                .Select(f => new StoredKeys(f.Flow.Id, f.Flow.Item, Keys(table, f.Flow.OccurredOn, f.Flow.Kind, f.Flow.ExternalRef))).ToList();

    /// <summary>The importer's read and write checks, one policy call per item and kind for the whole validation.</summary>
    private sealed class Access(IAccessPolicy policy, PersonId actor, CancellationToken ct)
    {
        private readonly Dictionary<ItemId, bool> readable = [];
        private readonly Dictionary<ItemId, bool> writable = [];

        public Func<ItemId, Task<bool>> Readable => ImportSectionKit.Readable(policy, actor, readable, ct);

        public async Task<bool> WritableAsync(ItemId item)
        {
            if (!writable.TryGetValue(item, out var canWrite))
                writable[item] = canWrite = await ImportSectionKit.CanAsync(policy, actor, item, AccessKind.Write, ct);
            return canWrite;
        }
    }
}
