using System.Text.Json;
using Nid.Core.Contracts;
using Nid.Income.Contracts;
using Nid.Income.Domain;
using Nid.Platform.Contracts;
using NodaTime;
using NodaTime.Text;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Income.Application.Import;

/// <summary>
/// The sections <c>income_sources</c> and <c>income_phases</c> (2C1 spec §5). Writes go through the same
/// <see cref="IItemRegistry"/>, <see cref="ISourceStore"/> and <see cref="IPhaseStore"/> as the API; rules come from the
/// Core, <see cref="SourceRules"/>, <see cref="PhaseRules"/> and <see cref="Ending"/>, never from here. A file id
/// resolves only among rows the importer can see (<see cref="ImportResolver"/>): another adult's source or phase the
/// importer cannot see answers exactly as an unused id. One-off income is the Core's <c>flows</c> section, which finds
/// the sources through <see cref="ImportContext.Items"/>.
/// </summary>
public sealed class IncomeImportSection(
    IItemRegistry items, ISourceStore sources, IPhaseStore phases, IAccessPolicy policy, IItemRules rules,
    IHouseholdCalendar calendar, IVisibleItems visible, IPersonRegistry persons, IClock clock) : IImportSection
{
    public const string SourcesSection = "income_sources";
    public const string PhasesSection = "income_phases";

    private enum Mode { Created, Unchanged, Replaced }

    /// <summary>A source of the file as validation decided it. <see cref="Id"/> is planned for a new one until applied.</summary>
    private sealed class SourcePlan(StagedEntity entity, IncomeSourceRecord record, ParsedSource parsed, string path)
    {
        public StagedEntity Entity { get; } = entity;
        public IncomeSourceRecord Record { get; } = record;
        public ParsedSource Parsed { get; } = parsed;
        public string Path { get; } = path;
        public ItemId Id { get; set; }
        public Mode Mode { get; set; }
        public Currency Currency { get; set; }
        /// <summary>Whether the actor changes it: known for a source this import creates or replaces, asked once otherwise.</summary>
        public bool? Writable { get; set; }

        public bool WasEnded { get; set; }

        /// <summary>The source's module row as stored; null for a new one.</summary>
        public IncomeSource? Stored { get; set; }

        public IReadOnlyList<IncomePhase> StoredPhases { get; set; } = [];

        /// <summary>The end the source has once applied: an unchanged one keeps the stored one.</summary>
        public LocalDate? EndedOn => Mode == Mode.Unchanged ? Stored!.EndedOn : Parsed.EndedOn;
    }

    /// <summary>
    /// A phase of the file as validation decided it; <see cref="Stored"/> is the row it resolved to. A new one has its id
    /// already, so that the rules tell two new phases apart.
    /// </summary>
    private sealed class PhasePlan(StagedEntity entity, IncomePhaseRecord record, ParsedPhase parsed, string path, SourcePlan source, Mode mode, IncomePhase? stored)
    {
        private readonly Guid id = stored?.Id ?? Guid.CreateVersion7();

        public StagedEntity Entity { get; } = entity;
        public IncomePhaseRecord Record { get; } = record;
        public string Path { get; } = path;
        public SourcePlan Source { get; } = source;
        public Mode Mode { get; } = mode;
        public IncomePhase? Stored { get; } = stored;
        public bool Writes => Mode != Mode.Unchanged;

        /// <summary>The phase as stored once applied, before any ending cuts it; on the source's real id once it is applied.</summary>
        public IncomePhase Value => Mode == Mode.Unchanged
            ? Stored!
            : new IncomePhase(id, Source.Id, parsed.Label, parsed.StartsOn, parsed.EndsOn, parsed.NetAmount, parsed.Every,
                parsed.GrossAmount, parsed.MaxCycles, parsed.Note, Stored?.Version ?? 0);
    }

    private readonly List<SourcePlan> sourcePlans = [];
    private readonly List<PhasePlan> phasePlans = [];
    private IReadOnlyDictionary<string, SectionCounts> counts = new Dictionary<string, SectionCounts>();

    public string Module => "income";
    public IReadOnlyList<string> Sections => [SourcesSection, PhasesSection];
    public int Order => 30;

    public IReadOnlyList<ImportIssue> ValidateBatch(string section, JsonElement entities, int firstIndex)
    {
        var issues = new List<ImportIssue>();
        var standIn = ImportSectionKit.StandIns();
        var position = 0;
        foreach (var entity in entities.EnumerateArray())
        {
            var path = NidDataJson.Path(section, firstIndex + position++);
            if (section == SourcesSection)
            {
                if (NidDataJson.TryRead<IncomeSourceRecord>(entity, path, issues, out var source)
                    && ImportSectionKit.TryId(source.Id, path, issues)
                    && NoNullOwners(source, path, issues))
                    IncomeParsing.TrySource(source, path, standIn, today: null, issues, out _);
            }
            else if (NidDataJson.TryRead<IncomePhaseRecord>(entity, path, issues, out var phase) && ImportSectionKit.TryId(phase.Id, path, issues))
                IncomeParsing.TryPhase(phase, path, currency: null, issues, out _);
        }
        return issues;
    }

    public async Task<SectionOutcome> ValidateAllAsync(ImportContext context, IStagedBatches staged, CancellationToken ct)
    {
        sourcePlans.Clear();
        phasePlans.Clear();
        var issues = new List<ImportIssue>();
        var sourceTally = SectionCounts.Zero;
        var phaseTally = SectionCounts.Zero;
        var today = await calendar.TodayAsync(ct); // the household's, as the API decides it
        var readable = new Dictionary<ItemId, bool>(); // one policy check per item
        var canRead = ImportSectionKit.Readable(policy, context.Actor, readable, ct);
        IReadOnlyList<PersonId>? adults = null;        // read once, by the first replaced source

        // Sources: each resolved, and refused or planned, before any phase is read.
        var sourceRecords = ImportSectionKit.ReadAll<IncomeSourceRecord>(staged, SourcesSection, r => r.Id, issues, NoNullOwners,
            unread: id => context.FailedItems.Add((SourcesSection, id))); // its phases and one-offs are not reported again
        var knownSources = await sources.FindImportedAsync(sourceRecords.Select(r => r.Record.Id).ToList(), ct);
        var byFileId = new Dictionary<string, SourcePlan>(StringComparer.Ordinal);
        foreach (var (entity, record) in sourceRecords)
        {
            var path = NidDataJson.Path(SourcesSection, entity.Index);
            if (!IncomeParsing.TrySource(record, path, r => context.Persons.GetValueOrDefault(r), today, issues, out var parsed)
                || !Owners(parsed, path, issues))
            {
                context.FailedItems.Add((SourcesSection, record.Id));
                continue;
            }

            // Every importer's rows under this id: only ImportResolver's answer may reach an issue.
            var candidates = knownSources.GetValueOrDefault(record.Id)?
                .Select(k => new ImportCandidate(k.Item.Value, k.Hash, k.ImportedBy, [k.Item])).ToList();
            var resolution = await ImportResolver.ResolveAsync(context.Actor, candidates, canRead);
            if (resolution.OwnHidden)
            {
                issues.Add(ImportIssue.Error(ImportCodes.NotPermitted, $"'{record.Id}' was imported before as an income source you cannot see.", path));
                context.FailedItems.Add((SourcesSection, record.Id));
                continue;
            }

            var plan = new SourcePlan(entity, record, parsed, path);
            if (resolution.Row is { } row)
            {
                // A row the actor reads (ResolveAsync): nothing is created, so the creation rule does not apply.
                plan.Id = new ItemId(row.Id);
                var current = (await items.FindAsync(plan.Id, ct))!;
                plan.Stored = (await sources.FindAsync(plan.Id, ct))!;
                plan.StoredPhases = await phases.ForSourceAsync(plan.Id, ct);
                plan.Currency = SourceCheck.CurrencyOf(current);
                plan.WasEnded = current.ArchivedAt is not null;
                if (row.Hash == entity.Hash)
                    plan.Mode = Mode.Unchanged;
                else if (!context.Replace)
                {
                    issues.Add(ImportIssue.Error(ImportCodes.Conflict,
                        $"'{record.Id}' was imported before with other content; nothing is overwritten (import-format.md §8).", path));
                    sourceTally += new SectionCounts(0, 0, 1);
                    context.FailedItems.Add((SourcesSection, record.Id));
                    continue;
                }
                else
                {
                    var refusal = await ReplaceRefusalAsync(context.Actor, plan.Id, current, parsed, path,
                        async () => adults ??= await persons.AdultIdsAsync(ct), ct);
                    if (refusal is not null)
                    {
                        issues.Add(refusal);
                        context.FailedItems.Add((SourcesSection, record.Id));
                        continue;
                    }
                    plan.Mode = Mode.Replaced;
                    plan.Writable = true;
                }
            }
            else
            {
                if (!rules.CanCreate(context.Actor, parsed.Owners))
                {
                    issues.Add(ImportIssue.Error(ImportCodes.NotPermitted,
                        "An item is imported by one of its owners, unless every owner is a dependent.", path));
                    context.FailedItems.Add((SourcesSection, record.Id));
                    continue;
                }
                plan.Id = ItemId.New(); // planned; ApplyAsync replaces it with the real one
                plan.Mode = Mode.Created;
                plan.Currency = parsed.Currency;
                plan.Writable = true;
            }
            sourcePlans.Add(plan);
            byFileId[record.Id] = plan;
        }

        // Phases: each on a source of this file, resolved among that source's rows.
        var phaseRecords = ImportSectionKit.ReadAll<IncomePhaseRecord>(staged, PhasesSection, r => r.Id, issues);
        var knownPhases = await phases.FindImportedAsync(phaseRecords.Select(r => r.Record.Id).ToList(), ct);
        foreach (var (entity, record) in phaseRecords)
        {
            var path = NidDataJson.Path(PhasesSection, entity.Index);
            if (context.FailedItems.Contains((SourcesSection, record.Source)))
                continue; // its source is reported already
            if (!byFileId.TryGetValue(record.Source, out var source))
            {
                issues.Add(ImportIssue.Error(ImportCodes.UnresolvedReference, $"'{record.Source}' names no entity of income_sources.", path + ".source"));
                continue;
            }
            if (!IncomeParsing.TryPhase(record, path, source.Currency, issues, out var parsed))
                continue;

            var candidates = knownPhases.GetValueOrDefault(record.Id)?
                .Select(k => new ImportCandidate(k.Id, k.Hash, k.ImportedBy, [k.Source])).ToList();
            var resolution = await ImportResolver.ResolveAsync(context.Actor, candidates, canRead, belongs: c => c.Items[0] == source.Id);
            if (resolution.OwnHidden)
            {
                issues.Add(ImportIssue.Error(ImportCodes.NotPermitted, $"'{record.Id}' was imported before as a phase you cannot see.", path));
                continue;
            }

            var stored = resolution.Row is { } row ? source.StoredPhases.FirstOrDefault(p => p.Id == row.Id) : null;
            if (resolution.Row is { } found)
            {
                // Whatever the hash and the mode: a stored phase never moves to another source.
                if (found.Items[0] != source.Id || stored is null)
                {
                    issues.Add(ImportIssue.Error(ImportCodes.InvalidValue,
                        "A phase stays on its source: give the phase of the other source an id of its own.", path + ".source"));
                    continue;
                }
                if (found.Hash == entity.Hash)
                {
                    phasePlans.Add(new PhasePlan(entity, record, parsed, path, source, Mode.Unchanged, stored));
                    continue;
                }
                if (!context.Replace)
                {
                    issues.Add(ImportIssue.Error(ImportCodes.Conflict,
                        $"'{record.Id}' was imported before with other content; nothing is overwritten (import-format.md §8).", path));
                    phaseTally += new SectionCounts(0, 0, 1);
                    continue;
                }
            }

            // A write: an existing source needs the actor to change it; one this import creates is theirs by construction.
            if (!await WritableAsync(context.Actor, source, ct))
            {
                issues.Add(ImportIssue.Error(ImportCodes.NotPermitted, stored is null
                    ? "This adds a phase to an income source you cannot change."
                    : "This replaces a phase of an income source you cannot change.", path));
                continue;
            }
            phasePlans.Add(new PhasePlan(entity, record, parsed, path, source, stored is null ? Mode.Created : Mode.Replaced, stored));
        }

        // Spec §2: no overlap per (source, label) among what the source holds once applied. A stored phase the file does
        // not replace counts as stored (an unchanged one included); each phase written is checked against those and the
        // ones written before it in the file, so the second of two is the one refused.
        foreach (var source in sourcePlans)
        {
            var written = phasePlans.Where(p => p.Source == source && p.Writes).ToList();
            var replacedIds = written.Where(p => p.Stored is not null).Select(p => p.Stored!.Id).ToHashSet();
            var others = source.StoredPhases.Where(p => !replacedIds.Contains(p.Id)).ToList();
            foreach (var plan in written)
            {
                var d = plan.Value;
                if (!IncomeParsing.Check(PhaseRules.NoOverlap(d.Label, d.StartsOn, d.EndsOn, d.Every, d.MaxCycles, others), plan.Path, issues))
                {
                    phasePlans.Remove(plan);
                    continue;
                }
                others.Add(plan.Value);
            }
        }

        // Spec §5: an end, as close does, refuses a phase that would start on or after it.
        foreach (var source in sourcePlans.ToList())
            if (source.EndedOn is { } endedOn && Ending.Blocks(Resulting(source), endedOn))
            {
                // A source the file leaves as it is stays ended: the member at fault is the phase the file adds.
                var late = source.Mode == Mode.Unchanged
                    ? phasePlans.FirstOrDefault(p => p.Source == source && p.Writes && p.Value.StartsOn >= endedOn)
                    : null;
                issues.Add(ImportIssue.Error(IncomeCodes.InvalidSource, Ending.Blocked, late is null ? source.Path + ".ended_on" : late.Path + ".starts_on"));
                context.FailedItems.Add((SourcesSection, source.Record.Id));
                sourcePlans.Remove(source);
                phasePlans.RemoveAll(p => p.Source == source);
            }

        // Warnings, on what each source holds once applied (cut by its end): none at all, or a gap within 60 days.
        foreach (var source in sourcePlans)
            if (source.StoredPhases.Count == 0 && phaseRecords.All(p => p.Record.Source != source.Record.Id))
                issues.Add(ImportIssue.Warning(IncomeCodes.SourceWithoutPhase, "This income source has no phase: nothing is expected from it.", source.Path));
        foreach (var source in sourcePlans)
        {
            var resulting = source.EndedOn is { } endedOn ? Resulting(source).Select(p => Ending.Cut(p, endedOn)).ToList() : Resulting(source);
            foreach (var gap in IncomeMath.Gaps(resulting, today))
            {
                var at = phasePlans.FirstOrDefault(p => p.Source == source && p.Value.Label == gap.Label && IncomeMath.EffectiveEnd(p.Value) == gap.EndsOn);
                issues.Add(ImportIssue.Warning(IncomeCodes.GapSoon,
                    $"The '{gap.Label}' phase ends on {LocalDatePattern.Iso.Format(gap.EndsOn)} and no phase follows it.", at?.Path ?? source.Path));
            }
        }

        foreach (var source in sourcePlans)
        {
            sourceTally += Count(source.Mode);
            context.Items[(SourcesSection, source.Record.Id)] = source.Id;
            context.ItemCurrencies[source.Id] = source.Currency;
            if (source.Mode == Mode.Created)
                context.NewItems.Add(source.Id);
        }
        foreach (var plan in phasePlans)
            phaseTally += Count(plan.Mode);

        counts = new Dictionary<string, SectionCounts> { [SourcesSection] = sourceTally, [PhasesSection] = phaseTally };
        return new SectionOutcome(issues, counts);
    }

    public async Task<Result<IReadOnlyDictionary<string, SectionCounts>>> ApplyAsync(ImportContext context, IStagedBatches staged, CancellationToken ct)
    {
        foreach (var source in sourcePlans.Where(s => s.Mode == Mode.Created))
        {
            var parsed = source.Parsed;
            var made = await items.CreateAsync(new NewItem("income", "source", source.Record.Name, parsed.Visibility, parsed.Currency.Code,
                parsed.Owners.Select(o => new OwnerShare(o.PersonId, o.ShareBp)).ToList()), ct);
            if (!made.TryGetValue(out var id))
                return ImportSectionKit.FailureOf(made);

            var planned = source.Id;
            context.NewItems.Remove(planned);
            context.ItemCurrencies.Remove(planned);
            source.Id = id;
            context.Items[(SourcesSection, source.Record.Id)] = id;
            context.NewItems.Add(id);
            context.ItemCurrencies[id] = parsed.Currency;

            await sources.CreateAsync(ToSource(id, source), Mark(context, source.Record.Id, source.Entity), ct);
            // Ended in the same transaction; its one-offs are still written after it, being its history.
            if (parsed.EndedOn is not null && (await items.ArchiveAsync(id, clock.GetCurrentInstant(), ct)).TryGetFailure(out var failure))
                return Result.Failure<IReadOnlyDictionary<string, SectionCounts>>(failure!);
        }

        foreach (var source in sourcePlans.Where(s => s.Mode == Mode.Replaced))
        {
            var parsed = source.Parsed;
            var item = await items.ReplaceAsync(source.Id, source.Record.Name, parsed.Visibility,
                parsed.Owners.Select(o => new OwnerShare(o.PersonId, o.ShareBp)).ToList(), ct);
            if (item.TryGetFailure(out var failure))
                return Result.Failure<IReadOnlyDictionary<string, SectionCounts>>(failure!);

            await sources.ReplaceAsync(ToSource(source.Id, source), Mark(context, source.Record.Id, source.Entity), ct);
            // An ended_on added ends the source, one removed reopens it; its phases are cut below.
            var archive = (source.WasEnded, parsed.EndedOn is not null) switch
            {
                (false, true) => await items.ArchiveAsync(source.Id, clock.GetCurrentInstant(), ct),
                (true, false) => await items.UnarchiveAsync(source.Id, ct),
                _ => Result.Success(),
            };
            if (archive.TryGetFailure(out failure))
                return Result.Failure<IReadOnlyDictionary<string, SectionCounts>>(failure!);
        }

        try
        {
            // In place first, then added: Value reads the source's real id now that it is applied.
            foreach (var plan in phasePlans.Where(p => p.Mode == Mode.Replaced))
                await phases.ReplaceAsync(Ended(plan.Value, plan.Source), Mark(context, plan.Record.Id, plan.Entity), ct);
            foreach (var plan in phasePlans.Where(p => p.Mode == Mode.Created))
                await phases.AddAsync(Ended(plan.Value, plan.Source), Mark(context, plan.Record.Id, plan.Entity), ct);

            // As close: a source this import ends, or whose phases it writes, has every phase still running on its end cut there.
            foreach (var source in sourcePlans.Where(s => s.EndedOn is not null && (s.Mode != Mode.Unchanged || phasePlans.Any(p => p.Source == s && p.Writes))))
                foreach (var cut in Ending.Cuts(await phases.ForSourceAsync(source.Id, ct), source.EndedOn!.Value))
                    await phases.UpdateAsync(cut, ct);
        }
        catch (PhaseConflictException e)
        {
            return Result.Failure<IReadOnlyDictionary<string, SectionCounts>>(new Failure(IncomeCodes.PhaseConflict, e.Message, null));
        }

        return Result.Success(counts);
    }

    /// <summary>
    /// 2C1 spec §5: every source the reader can read, ended ones included, by name then file id, and their phases by
    /// source, label, start and file id. Each source gets its file id through <see cref="ExportContext.NameItem"/>, for the
    /// one-offs of the Core's <c>flows</c>, which runs after this one.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<JsonElement>>> ExportAsync(ExportContext context, CancellationToken ct)
    {
        var ids = await visible.ForAsync(context.Reader, VisibilityScope.Me, includeArchived: true, ct);
        var stored = await sources.ReadAllAsync(ids.ToList(), ct); // only the items that are sources
        var heads = new Dictionary<ItemId, ItemAccess>();
        foreach (var s in stored)
            if (await items.FindAsync(s.Source.ItemId, ct) is { } item)
                heads[s.Source.ItemId] = item;
        var exported = stored.Where(s => heads.ContainsKey(s.Source.ItemId)).ToList();
        var sourceIds = FileIds.Assign("inc",
            exported.Select(s => new KeyedRow(s.Source.ItemId.Value, s.Import?.Ref, s.Import?.ImportedBy)).ToList(), context.Reader);

        var sourceRecords = new List<IncomeSourceRecord>();
        foreach (var s in exported
                     .OrderBy(s => heads[s.Source.ItemId].DisplayName, StringComparer.Ordinal)
                     .ThenBy(s => sourceIds[s.Source.ItemId.Value], StringComparer.Ordinal))
        {
            var head = heads[s.Source.ItemId];
            var fileId = sourceIds[s.Source.ItemId.Value];
            context.Expose(s.Source.ItemId);
            context.NameItem(s.Source.ItemId, fileId);
            sourceRecords.Add(new IncomeSourceRecord
            {
                Id = fileId,
                Name = head.DisplayName,
                Kind = IncomeWire.Of(s.Source.Kind),
                PayerLabel = s.Source.PayerLabel,
                Note = s.Source.Note,
                Currency = head.Currency!,
                Ownership = head.Owners
                    .Select(w => new ShareRecord { Person = context.PersonRef(w.PersonId), ShareBp = w.ShareBp })
                    .OrderBy(w => w.Person, StringComparer.Ordinal).ToList(),
                Visibility = head.Visibility == Visibility.Private ? "private" : "shared",
                EndedOn = s.Source.EndedOn is { } e ? LocalDatePattern.Iso.Format(e) : null,
            });
        }

        var storedPhases = exported.Count == 0 ? [] : await phases.ReadAllAsync(exported.Select(s => s.Source.ItemId).ToList(), ct);
        var phaseIds = FileIds.Assign("ip",
            storedPhases.Select(p => new KeyedRow(p.Phase.Id, p.Import?.Ref, p.Import?.ImportedBy)).ToList(), context.Reader);
        var phaseRecords = storedPhases
            .Select(s => s.Phase)
            .Select(p => new IncomePhaseRecord
            {
                Id = phaseIds[p.Id],
                Source = sourceIds[p.Source.Value],
                Label = p.Label,
                StartsOn = LocalDatePattern.Iso.Format(p.StartsOn),
                EndsOn = p.EndsOn is { } e ? LocalDatePattern.Iso.Format(e) : null,
                NetAmount = new MoneyRecord { Amount = p.NetAmount.Amount, Currency = p.NetAmount.Currency.Code },
                Every = new IncomeDurationRecord { Count = p.Every.Count, Unit = p.Every.Unit == DurationUnit.Year ? "year" : "month" },
                GrossAmount = p.GrossAmount is { } g ? new MoneyRecord { Amount = g.Amount, Currency = g.Currency.Code } : null,
                MaxCycles = p.MaxCycles,
                Note = p.Note,
            })
            .OrderBy(r => r.Source, StringComparer.Ordinal).ThenBy(r => r.Label, StringComparer.Ordinal)
            .ThenBy(r => r.StartsOn, StringComparer.Ordinal).ThenBy(r => r.Id, StringComparer.Ordinal);

        return new Dictionary<string, IReadOnlyList<JsonElement>>
        {
            [SourcesSection] = sourceRecords.Select(r => JsonSerializer.SerializeToElement(r, NidDataJson.Options)).ToList(),
            [PhasesSection] = phaseRecords.Select(r => JsonSerializer.SerializeToElement(r, NidDataJson.Options)).ToList(),
        };
    }

    /// <summary>What the source holds once applied: its stored phases, each the file replaces as the file has it, and the new ones.</summary>
    private IReadOnlyList<IncomePhase> Resulting(SourcePlan source)
    {
        var written = phasePlans.Where(p => p.Source == source && p.Writes).ToList();
        var replaced = written.Where(p => p.Stored is not null).ToDictionary(p => p.Stored!.Id);
        return source.StoredPhases.Select(p => replaced.TryGetValue(p.Id, out var plan) ? plan.Value : p)
            .Concat(written.Where(p => p.Stored is null).Select(p => p.Value))
            .ToList();
    }

    /// <summary>A phase written on a source that ends is cut on its end, as <c>close</c> cuts it.</summary>
    private static IncomePhase Ended(IncomePhase phase, SourcePlan source)
        => source.EndedOn is { } endedOn ? Ending.Cut(phase, endedOn) : phase;

    private async Task<bool> WritableAsync(PersonId actor, SourcePlan source, CancellationToken ct)
        => source.Writable ??= await ImportSectionKit.CanAsync(policy, actor, source.Id, AccessKind.Write, ct);

    private bool Owners(ParsedSource parsed, string path, List<ImportIssue> issues)
    {
        if (!rules.ValidateOwners(parsed.Visibility, parsed.Owners).TryGetFailure(out var failure))
            return true;
        issues.Add(ImportIssue.Error(failure!.Code, failure.Message, path + ".ownership"));
        return false;
    }

    /// <summary>The ownership list holds no null element (<see cref="ImportSectionKit.NoNulls"/>).</summary>
    private static bool NoNullOwners(IncomeSourceRecord record, string path, List<ImportIssue> issues)
        => ImportSectionKit.NoNulls(record.Ownership, path + ".ownership", issues);

    /// <summary>
    /// 1F3b spec §3.2 and 2C1 spec §5: replacing is a write, on the item as it is now, that never takes it from another
    /// adult, nor changes its currency. The first refusal, or null.
    /// </summary>
    private Task<ImportIssue?> ReplaceRefusalAsync(
        PersonId actor, ItemId id, ItemAccess current, ParsedSource parsed, string path,
        Func<Task<IReadOnlyList<PersonId>>> adults, CancellationToken ct)
        => ImportSectionKit.ReplaceRefusalAsync(policy, rules, actor, id, current, parsed.Visibility, parsed.Owners,
            parsed.Currency.Code, "income source", path, adults, ct);

    private static SectionCounts Count(Mode mode) => mode switch
    {
        Mode.Created => new SectionCounts(1, 0, 0),
        Mode.Unchanged => new SectionCounts(0, 1, 0),
        _ => new SectionCounts(0, 0, 0, 1),
    };

    private static ImportMark Mark(ImportContext context, string fileId, StagedEntity entity) => new(fileId, entity.Hash, context.Actor);

    private static IncomeSource ToSource(ItemId id, SourcePlan source)
        => new(id, source.Parsed.Kind, source.Record.PayerLabel, source.Record.Note, source.Parsed.EndedOn);
}
