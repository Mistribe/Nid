using System.Text.Json;
using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using NodaTime.Text;
using UnambitiousFx.Functional;

namespace Nid.Investments.Application.Import;

/// <summary>
/// The section <c>envelopes</c> (2B1b spec §4). Writes go through the same <see cref="IItemRegistry"/> and
/// <see cref="IEnvelopeStore"/> as the API; rules come from the Core and <see cref="EnvelopeRules"/>, never from here.
/// A file id resolves only among rows the importer can see (<see cref="ImportResolver"/>): another adult's envelope the
/// importer cannot see answers exactly as an unused id.
/// </summary>
public sealed class EnvelopesImportSection(
    IItemRegistry items, IEnvelopeStore envelopes, IAccessPolicy policy, IItemRules rules, IHouseholdCalendar calendar,
    IVisibleItems visible, IPersonRegistry persons, IFactImports facts, IClock clock) : IImportSection
{
    public const string Section = "envelopes";

    /// <summary>The fact sections whose dates bound a closing (spec §4); owned by the Core.</summary>
    private static readonly (string Section, string Date)[] FactDates = [("valuations", "as_of"), ("flows", "occurred_on")];

    private readonly List<(StagedEntity Entity, EnvelopeRecord Record)> created = [];
    private readonly List<(StagedEntity Entity, EnvelopeRecord Record, ItemId Id, bool WasClosed)> replaced = [];
    private IReadOnlyDictionary<string, SectionCounts> counts = new Dictionary<string, SectionCounts>();

    public string Module => "investments";
    public IReadOnlyList<string> Sections => [Section];
    public int Order => 20;

    public IReadOnlyList<ImportIssue> ValidateBatch(string section, JsonElement entities, int firstIndex)
    {
        var issues = new List<ImportIssue>();
        var standIn = ImportSectionKit.StandIns();
        var position = 0;
        foreach (var entity in entities.EnumerateArray())
        {
            var path = NidDataJson.Path(Section, firstIndex + position++);
            if (NidDataJson.TryRead<EnvelopeRecord>(entity, path, issues, out var record)
                && ImportSectionKit.TryId(record.Id, path, issues)
                && NoNullOwners(record, path, issues))
                EnvelopeParsing.TryEnvelope(record, path, standIn, today: null, issues, out _);
        }
        return issues;
    }

    public async Task<SectionOutcome> ValidateAllAsync(ImportContext context, IStagedBatches staged, CancellationToken ct)
    {
        created.Clear();
        replaced.Clear();
        var issues = new List<ImportIssue>();
        var tally = SectionCounts.Zero;
        var today = await calendar.TodayAsync(ct); // the household's, as the fact sections and the API decide it
        var records = ImportSectionKit.ReadAll<EnvelopeRecord>(staged, Section, r => r.Id, issues, NoNullOwners,
            unread: id => context.FailedItems.Add((Section, id))); // its facts are not reported again
        var known = await envelopes.FindImportedAsync(records.Select(r => r.Record.Id).ToList(), ct);
        var latestFacts = LatestFactDates(staged);
        var readable = new Dictionary<ItemId, bool>(); // one policy check per item
        IReadOnlyList<PersonId>? adults = null;        // read once, by the first replaced envelope

        foreach (var (entity, record) in records)
        {
            var path = NidDataJson.Path(Section, entity.Index);
            if (!EnvelopeParsing.TryEnvelope(record, path, r => context.Persons.GetValueOrDefault(r), today, issues, out var parsed)
                || !Owners(parsed, path, issues)
                || !ClosesAfter(parsed, latestFacts.TryGetValue(record.Id, out var latestInFile) ? latestInFile : null, "in the file", path, issues))
            {
                context.FailedItems.Add((Section, record.Id));
                continue;
            }

            // Every importer's rows under this id: only ImportResolver's answer may reach an issue.
            var candidates = known.GetValueOrDefault(record.Id)?
                .Select(k => new ImportCandidate(k.Item.Value, k.Hash, k.ImportedBy, [k.Item])).ToList();
            var resolution = await ImportResolver.ResolveAsync(context.Actor, candidates,
                ImportSectionKit.Readable(policy, context.Actor, readable, ct));
            if (resolution.OwnHidden)
            {
                issues.Add(ImportIssue.Error(ImportCodes.NotPermitted, $"'{record.Id}' was imported before as an envelope you cannot see.", path));
                context.FailedItems.Add((Section, record.Id));
                continue;
            }

            ItemId id;
            Currency currency;
            if (resolution.Row is { } row)
            {
                // A row the actor reads (ResolveAsync): nothing is created, so the creation rule does not apply.
                id = new ItemId(row.Id);
                var current = (await items.FindAsync(id, ct))!;
                currency = Currency.TryCreate(current.Currency, out var stored) ? stored : parsed.Currency;
                if (row.Hash == entity.Hash)
                    tally += new SectionCounts(0, 1, 0);
                else if (!context.Replace)
                {
                    issues.Add(ImportIssue.Error(ImportCodes.Conflict,
                        $"'{record.Id}' was imported before with other content; nothing is overwritten (import-format.md §8).", path));
                    tally += new SectionCounts(0, 0, 1);
                    context.FailedItems.Add((Section, record.Id));
                    continue;
                }
                else
                {
                    var refusal = await ReplaceRefusalAsync(context.Actor, id, current, parsed, path,
                        async () => adults ??= await persons.AdultIdsAsync(ct), ct);
                    if (refusal is not null)
                    {
                        issues.Add(refusal);
                        context.FailedItems.Add((Section, record.Id));
                        continue;
                    }

                    // Controller ruling (fix round 1): as the API's close, not before the envelope's stored current values
                    // and flows either; one this file replaces still counts.
                    if (parsed.ClosedOn is not null && !ClosesAfter(parsed, await LatestStoredAsync(id, ct), "stored", path, issues))
                    {
                        context.FailedItems.Add((Section, record.Id));
                        continue;
                    }
                    replaced.Add((entity, record, id, current.ArchivedAt is not null));
                    tally += new SectionCounts(0, 0, 0, 1);
                }
            }
            else
            {
                if (!rules.CanCreate(context.Actor, parsed.Owners))
                {
                    issues.Add(ImportIssue.Error(ImportCodes.NotPermitted,
                        "An item is imported by one of its owners, unless every owner is a dependent.", path));
                    context.FailedItems.Add((Section, record.Id));
                    continue;
                }
                id = ItemId.New(); // planned; ApplyAsync replaces it with the real one
                currency = parsed.Currency;
                context.NewItems.Add(id);
                created.Add((entity, record));
                tally += new SectionCounts(1, 0, 0);
            }

            context.Items[(Section, record.Id)] = id;
            context.ItemCurrencies[id] = currency;
        }

        counts = new Dictionary<string, SectionCounts> { [Section] = tally };
        return new SectionOutcome(issues, counts);
    }

    public async Task<Result<IReadOnlyDictionary<string, SectionCounts>>> ApplyAsync(ImportContext context, IStagedBatches staged, CancellationToken ct)
    {
        var issues = new List<ImportIssue>();
        foreach (var (entity, record) in created)
        {
            var path = NidDataJson.Path(Section, entity.Index);
            if (!EnvelopeParsing.TryEnvelope(record, path, r => context.Persons.GetValueOrDefault(r), null, issues, out var parsed))
                return ImportSectionKit.Drift(issues);

            var made = await items.CreateAsync(new NewItem("investments", "envelope", record.Name, parsed.Visibility, parsed.Currency.Code,
                parsed.Owners.Select(o => new OwnerShare(o.PersonId, o.ShareBp)).ToList()), ct);
            if (!made.TryGetValue(out var id))
                return ImportSectionKit.FailureOf(made);

            var planned = context.Items[(Section, record.Id)];
            context.NewItems.Remove(planned);
            context.ItemCurrencies.Remove(planned);
            context.Items[(Section, record.Id)] = id;
            context.NewItems.Add(id);
            context.ItemCurrencies[id] = parsed.Currency;

            await envelopes.CreateAsync(ToEnvelope(id, record, parsed), new ImportMark(record.Id, entity.Hash, context.Actor), ct);
            // Closed in the same transaction; its facts are still written after it, being its history (spec §4).
            if (parsed.ClosedOn is not null && (await items.ArchiveAsync(id, clock.GetCurrentInstant(), ct)).TryGetFailure(out var failure))
                return Result.Failure<IReadOnlyDictionary<string, SectionCounts>>(failure!);
        }

        foreach (var (entity, record, id, wasClosed) in replaced)
        {
            var path = NidDataJson.Path(Section, entity.Index);
            if (!EnvelopeParsing.TryEnvelope(record, path, r => context.Persons.GetValueOrDefault(r), null, issues, out var parsed))
                return ImportSectionKit.Drift(issues);

            var item = await items.ReplaceAsync(id, record.Name, parsed.Visibility,
                parsed.Owners.Select(o => new OwnerShare(o.PersonId, o.ShareBp)).ToList(), ct);
            if (item.TryGetFailure(out var failure))
                return Result.Failure<IReadOnlyDictionary<string, SectionCounts>>(failure!);

            await envelopes.ReplaceAsync(ToEnvelope(id, record, parsed), new ImportMark(record.Id, entity.Hash, context.Actor), ct);
            // A closed_on added closes the envelope, one removed reopens it; the closing value of 0 is a fact of the file.
            var archive = (wasClosed, parsed.ClosedOn is not null) switch
            {
                (false, true) => await items.ArchiveAsync(id, clock.GetCurrentInstant(), ct),
                (true, false) => await items.UnarchiveAsync(id, ct),
                _ => Result.Success(),
            };
            if (archive.TryGetFailure(out failure))
                return Result.Failure<IReadOnlyDictionary<string, SectionCounts>>(failure!);
        }

        return Result.Success(counts);
    }

    /// <summary>
    /// 2B1b spec §6: every envelope the reader can read, closed ones included, by name then file id. Each gets its file
    /// id through <see cref="ExportContext.NameItem"/>, for the fact sections that run after this one.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<JsonElement>>> ExportAsync(ExportContext context, CancellationToken ct)
    {
        var ids = await visible.ForAsync(context.Reader, VisibilityScope.Me, includeArchived: true, ct);
        var stored = await envelopes.ReadAllAsync(ids.ToList(), ct); // only the items that are envelopes
        var heads = new Dictionary<ItemId, ItemAccess>();
        foreach (var e in stored)
            if (await items.FindAsync(e.Envelope.ItemId, ct) is { } item)
                heads[e.Envelope.ItemId] = item;
        var exported = stored.Where(e => heads.ContainsKey(e.Envelope.ItemId)).ToList();
        var fileIds = FileIds.Assign("env",
            exported.Select(e => new KeyedRow(e.Envelope.ItemId.Value, e.Import?.Ref, e.Import?.ImportedBy)).ToList(), context.Reader);

        var records = new List<EnvelopeRecord>();
        foreach (var e in exported
                     .OrderBy(e => heads[e.Envelope.ItemId].DisplayName, StringComparer.Ordinal)
                     .ThenBy(e => fileIds[e.Envelope.ItemId.Value], StringComparer.Ordinal))
        {
            var head = heads[e.Envelope.ItemId];
            var fileId = fileIds[e.Envelope.ItemId.Value];
            context.Expose(e.Envelope.ItemId);
            context.NameItem(e.Envelope.ItemId, fileId);
            records.Add(new EnvelopeRecord
            {
                Id = fileId,
                Name = head.DisplayName,
                Kind = EnvelopeWire.Of(e.Envelope.Kind),
                Institution = e.Envelope.Institution,
                AccountHint = e.Envelope.AccountHint,
                Note = e.Envelope.Note,
                OpenedOn = e.Envelope.OpenedOn is { } o ? LocalDatePattern.Iso.Format(o) : null,
                ClosedOn = e.Envelope.ClosedOn is { } c ? LocalDatePattern.Iso.Format(c) : null,
                Currency = head.Currency!,
                ValuationCadence = EnvelopeWire.Of(e.Envelope.Cadence),
                StaleAfterDays = e.Envelope.StaleAfterDays,
                Ownership = head.Owners
                    .Select(w => new ShareRecord { Person = context.PersonRef(w.PersonId), ShareBp = w.ShareBp })
                    .OrderBy(w => w.Person, StringComparer.Ordinal).ToList(),
                Visibility = head.Visibility == Visibility.Private ? "private" : "shared",
            });
        }

        return new Dictionary<string, IReadOnlyList<JsonElement>>
        {
            [Section] = records.Select(r => JsonSerializer.SerializeToElement(r, NidDataJson.Options)).ToList(),
        };
    }

    /// <summary>
    /// File id of an item → the latest date among the staged values and flows naming it, of those whose date parses.
    /// Anything malformed is the fact section's to report.
    /// </summary>
    private static Dictionary<string, LocalDate> LatestFactDates(IStagedBatches staged)
    {
        var latest = new Dictionary<string, LocalDate>(StringComparer.Ordinal);
        foreach (var (section, member) in FactDates)
            foreach (var entity in staged.Section(section))
            {
                if (entity.Value.ValueKind != JsonValueKind.Object
                    || !entity.Value.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.String
                    || !entity.Value.TryGetProperty(member, out var date) || date.ValueKind != JsonValueKind.String)
                    continue;
                var parse = LocalDatePattern.Iso.Parse(date.GetString()!);
                if (!parse.Success)
                    continue;
                var key = item.GetString()!;
                if (!latest.TryGetValue(key, out var known) || parse.Value > known)
                    latest[key] = parse.Value;
            }
        return latest;
    }

    private bool Owners(ParsedEnvelope parsed, string path, List<ImportIssue> issues)
    {
        if (!rules.ValidateOwners(parsed.Visibility, parsed.Owners).TryGetFailure(out var failure))
            return true;
        issues.Add(ImportIssue.Error(failure!.Code, failure.Message, path + ".ownership"));
        return false;
    }

    /// <summary>
    /// The rule of <c>close</c>: not before the envelope's latest value or flow (<paramref name="where"/>: in the file, or
    /// stored), the same day accepted.
    /// </summary>
    private static bool ClosesAfter(ParsedEnvelope parsed, LocalDate? latest, string where, string path, List<ImportIssue> issues)
    {
        if (parsed.ClosedOn is not { } closedOn || latest is not { } last || closedOn >= last)
            return true;
        issues.Add(ImportIssue.Error(InvestmentsCodes.InvalidEnvelope,
            $"A close cannot predate this envelope's latest value or flow {where} ({LocalDatePattern.Iso.Format(last)}).", path + ".closed_on"));
        return false;
    }

    /// <summary>The latest date among the envelope's current values and flows, null when it has none.</summary>
    private async Task<LocalDate?> LatestStoredAsync(ItemId id, CancellationToken ct)
    {
        var dates = (await facts.ValuationsOfAsync([id], ct)).Select(v => v.Valuation.AsOf)
            .Concat((await facts.FlowsOfAsync([id], ct)).Select(f => f.Flow.OccurredOn)).ToList();
        return dates.Count == 0 ? null : dates.Max();
    }

    /// <summary>The ownership list holds no null element (<see cref="ImportSectionKit.NoNulls"/>).</summary>
    private static bool NoNullOwners(EnvelopeRecord record, string path, List<ImportIssue> issues)
        => ImportSectionKit.NoNulls(record.Ownership, path + ".ownership", issues);

    /// <summary>
    /// 1F3b spec §3.2 and 2B1b spec §4: replacing is a write, on the item as it is now, that never takes it from another
    /// adult, nor changes its currency. The first refusal, or null.
    /// </summary>
    private async Task<ImportIssue?> ReplaceRefusalAsync(
        PersonId actor, ItemId id, ItemAccess current, ParsedEnvelope parsed, string path,
        Func<Task<IReadOnlyList<PersonId>>> adults, CancellationToken ct)
    {
        if (!await ImportSectionKit.CanAsync(policy, actor, id, AccessKind.Write, ct))
            return ImportIssue.Error(ImportCodes.NotPermitted, "This replaces an envelope you cannot change.", path);
        if (rules.DropsAnotherAdultOwner(actor, current.Owners, parsed.Owners))
            return ImportIssue.Error(ImportCodes.NotPermitted, "This replacement removes another adult from the owners.", path + ".ownership");
        if (rules.TakesReadAway(actor, current.Visibility, current.Owners, parsed.Visibility, parsed.Owners, await adults()))
            return ImportIssue.Error(ImportCodes.NotPermitted,
                "This replacement hides the envelope from another adult who sees it today.", path + ".visibility");
        if (current.Currency != parsed.Currency.Code)
            return ImportIssue.Error(CoreFactCodes.CurrencyMismatch,
                $"This envelope is in {current.Currency}: a replacement keeps its currency.", path + ".currency");
        return null;
    }

    private static Envelope ToEnvelope(ItemId id, EnvelopeRecord record, ParsedEnvelope parsed)
        => new(id, parsed.Kind, record.Institution, record.AccountHint, parsed.OpenedOn, parsed.Cadence, record.StaleAfterDays, record.Note, parsed.ClosedOn);
}
