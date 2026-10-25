using System.Text.Json;
using Nid.Core.Contracts;
using Nid.Goals.Contracts;
using Nid.Investments.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using NodaTime.Text;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Goals.Application.Import;

/// <summary>
/// The section <c>goals</c> (savings goals spec D16): a goal, its links to the file's envelopes and its progress entries.
/// Writes go through the same <see cref="IItemRegistry"/> and <see cref="IGoalStore"/> as the API; the field rules are
/// <see cref="GoalCheck"/>'s, except that a target date in the past is accepted (an import restores the past). Runs after the
/// investments sections: an envelope id of the file resolves through <see cref="ImportContext.Items"/>.
/// </summary>
public sealed class GoalsImportSection(
    IItemRegistry items, IGoalStore goals, IEnvelopeFunding envelopes, IAccessPolicy policy, IItemRules rules,
    IHouseholdCalendar calendar, IVisibleItems visible, IPersonRegistry persons, IClock clock) : IImportSection
{
    public const string Section = "goals";

    private enum Mode { Created, Unchanged, Replaced }

    /// <summary>A goal record, parsed; <paramref name="Funding"/> is filled once its envelopes resolve.</summary>
    private sealed record Parsed(
        string Kind, int? Months, LocalDate? TargetDate, Visibility Visibility, IReadOnlyList<Owner> Owners,
        IReadOnlyList<(LocalDate On, decimal Amount)> Entries);

    private sealed class Plan(StagedEntity entity, GoalRecord record, Parsed parsed, string path)
    {
        public StagedEntity Entity { get; } = entity;
        public GoalRecord Record { get; } = record;
        public Parsed Parsed { get; } = parsed;
        public string Path { get; } = path;
        public ItemId Id { get; set; }
        public Mode Mode { get; set; }
        public bool WasClosed { get; set; }
        public Goal? Stored { get; set; }
        public List<FundingShare> Funding { get; } = [];
    }

    private readonly List<Plan> plans = [];
    private IReadOnlyDictionary<string, SectionCounts> counts = new Dictionary<string, SectionCounts>();

    public string Module => "goals";
    public IReadOnlyList<string> Sections => [Section];
    public int Order => 100;

    public IReadOnlyList<ImportIssue> ValidateBatch(string section, JsonElement entities, int firstIndex)
    {
        var issues = new List<ImportIssue>();
        var standIn = ImportSectionKit.StandIns();
        var position = 0;
        foreach (var entity in entities.EnumerateArray())
        {
            var path = NidDataJson.Path(Section, firstIndex + position++);
            if (NidDataJson.TryRead(entity, GoalsFileJson.Records.GoalRecord, path, issues, out var record)
                && ImportSectionKit.TryId(record.Id, path, issues)
                && NoNulls(record, path, issues))
                TryParse(record, path, standIn, today: null, issues, out _);
        }
        return issues;
    }

    public async Task<SectionOutcome> ValidateAllAsync(ImportContext context, IStagedBatches staged, CancellationToken ct)
    {
        plans.Clear();
        var issues = new List<ImportIssue>();
        var tally = SectionCounts.Zero;
        var today = await calendar.TodayAsync(ct);
        var readable = new Dictionary<ItemId, bool>(); // one policy check per item
        var canRead = ImportSectionKit.Readable(policy, context.Actor, readable, ct);
        IReadOnlyList<PersonId>? adults = null;        // read once, by the first replaced goal

        var records = ImportSectionKit.ReadAll(staged, Section, GoalsFileJson.Records.GoalRecord, r => r.Id, issues, NoNulls);
        var known = await goals.FindImportedAsync(records.Select(r => r.Record.Id).ToList(), ct);
        foreach (var (entity, record) in records)
        {
            var path = NidDataJson.Path(Section, entity.Index);
            if (!TryParse(record, path, r => context.Persons.GetValueOrDefault(r), today, issues, out var parsed)
                || !Owners(parsed, path, issues))
                continue;

            var plan = new Plan(entity, record, parsed, path);
            if (!TryLinks(plan, context, issues))
                continue;

            var candidates = known.GetValueOrDefault(record.Id)?
                .Select(k => new ImportCandidate(k.Item.Value, k.Hash, k.ImportedBy, [k.Item])).ToList();
            var resolution = await ImportResolver.ResolveAsync(context.Actor, candidates, canRead);
            if (resolution.OwnHidden)
            {
                issues.Add(ImportIssue.Error(ImportCodes.NotPermitted, $"'{record.Id}' was imported before as a goal you cannot see.", path));
                continue;
            }

            if (resolution.Row is { } row)
            {
                plan.Id = new ItemId(row.Id);
                var current = (await items.FindAsync(plan.Id, ct))!;
                plan.Stored = (await goals.FindAsync(plan.Id, ct))!;
                plan.WasClosed = current.ArchivedAt is not null;
                if (row.Hash == entity.Hash)
                    plan.Mode = Mode.Unchanged;
                else if (!context.Replace)
                {
                    issues.Add(ImportIssue.Error(ImportCodes.Conflict,
                        $"'{record.Id}' was imported before with other content; nothing is overwritten (import-format.md §8).", path));
                    tally += new SectionCounts(0, 0, 1);
                    continue;
                }
                else
                {
                    if (plan.Stored.Kind != parsed.Kind)
                    {
                        issues.Add(ImportIssue.Error(ImportCodes.InvalidValue, "A goal keeps its kind: give the other kind an id of its own.", path + ".kind"));
                        continue;
                    }
                    var refusal = await ImportSectionKit.ReplaceRefusalAsync(policy, rules, context.Actor, plan.Id, current, parsed.Visibility,
                        parsed.Owners, currency: null, "goal", path, async () => adults ??= await persons.AdultIdsAsync(ct), ct);
                    if (refusal is not null)
                    {
                        issues.Add(refusal);
                        continue;
                    }
                    plan.Mode = Mode.Replaced;
                }
            }
            else
            {
                if (!rules.CanCreate(context.Actor, parsed.Owners))
                {
                    issues.Add(ImportIssue.Error(ImportCodes.NotPermitted,
                        "An item is imported by one of its owners, unless every owner is a dependent.", path));
                    continue;
                }
                plan.Id = ItemId.New(); // planned; ApplyAsync replaces it with the real one
                plan.Mode = Mode.Created;
            }
            plans.Add(plan);
        }

        foreach (var plan in plans)
        {
            tally += plan.Mode switch
            {
                Mode.Created => new SectionCounts(1, 0, 0),
                Mode.Unchanged => new SectionCounts(0, 1, 0),
                _ => new SectionCounts(0, 0, 0, 1),
            };
            context.Items[(Section, plan.Record.Id)] = plan.Id;
        }
        counts = new Dictionary<string, SectionCounts> { [Section] = tally };
        return new SectionOutcome(issues, counts);
    }

    public async Task<Result<IReadOnlyDictionary<string, SectionCounts>>> ApplyAsync(ImportContext context, IStagedBatches staged, CancellationToken ct)
    {
        var today = await calendar.TodayAsync(ct);
        foreach (var plan in plans.Where(p => p.Mode != Mode.Unchanged))
        {
            var parsed = plan.Parsed;
            var owners = parsed.Owners.Select(o => new OwnerShare(o.PersonId, o.ShareBp)).ToList();
            var mark = new ImportMark(plan.Record.Id, plan.Entity.Hash, context.Actor);
            var kept = new List<Funding>(); // D10c: links to envelopes the importer does not see stay as they are
            if (plan.Mode == Mode.Created)
            {
                var made = await items.CreateAsync(new NewItem("goals", parsed.Kind, plan.Record.Name, parsed.Visibility, null, owners), ct);
                if (!made.TryGetValue(out var id))
                    return ImportSectionKit.FailureOf(made);
                plan.Id = id;
                context.Items[(Section, plan.Record.Id)] = id;
                await goals.CreateAsync(ToGoal(plan, 0), ct, mark);
            }
            else
            {
                var item = await items.ReplaceAsync(plan.Id, plan.Record.Name, parsed.Visibility, owners, ct);
                if (item.TryGetFailure(out var failure))
                    return Result.Failure<IReadOnlyDictionary<string, SectionCounts>>(failure!);
                await goals.ReplaceAsync(ToGoal(plan, plan.Stored!.Version), mark, ct);

                var stored = await goals.FundingOfAsync([plan.Id], ct);
                var read = await GoalCheck.SeenAsync(envelopes, context.Actor, stored.Select(f => f.Envelope).ToList(), today, ct);
                if (!read.TryGetValue(out var seen))
                    return ImportSectionKit.FailureOf(read);
                kept.AddRange(stored.Where(f => !seen!.ContainsKey(f.Envelope)));
                foreach (var entry in (await goals.EntriesOfAsync([plan.Id], ct)).Where(e => parsed.Entries.All(p => p.On != e.On)))
                    await goals.DeleteEntryAsync(plan.Id, entry.On, ct);
            }

            // The envelopes' real ids: an envelope created by this import was planned at validation.
            var links = plan.Record.Funding.Select(f => new Funding(plan.Id, context.Items[("envelopes", f.Envelope)], f.ShareBp));
            await goals.ReplaceFundingAsync(plan.Id, links.Concat(kept).ToList(), ct);
            foreach (var (on, amount) in parsed.Entries)
                await goals.PutEntryAsync(new GoalEntry(plan.Id, on, amount), ct);

            var archive = (plan.WasClosed, plan.Record.Closed) switch
            {
                (false, true) => await items.ArchiveAsync(plan.Id, clock.GetCurrentInstant(), ct),
                (true, false) => await items.UnarchiveAsync(plan.Id, ct),
                _ => Result.Success(),
            };
            if (archive.TryGetFailure(out var archiveFailure))
                return Result.Failure<IReadOnlyDictionary<string, SectionCounts>>(archiveFailure!);
        }
        return Result.Success(counts);
    }

    /// <summary>
    /// Every goal the reader can read, closed ones included, by name then file id. A link to an envelope the reader's export
    /// does not hold (another adult's private one) is left out, never named.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<JsonElement>>> ExportAsync(ExportContext context, CancellationToken ct)
    {
        var ids = await visible.ForAsync(context.Reader, VisibilityScope.Me, includeArchived: true, ct);
        var stored = await goals.ReadAllAsync(ids.ToList(), ct);
        var heads = await items.FindManyAsync(stored.Select(x => x.Goal.ItemId).ToList(), ct);
        var exported = stored.Where(g => heads.ContainsKey(g.Goal.ItemId)).ToList();
        var fileIds = FileIds.Assign("goal",
            exported.Select(g => new KeyedRow(g.Goal.ItemId.Value, g.Import?.Ref, g.Import?.ImportedBy)).ToList(), context.Reader);
        var goalIds = exported.Select(g => g.Goal.ItemId).ToList();
        var funding = (await goals.FundingOfAsync(goalIds, ct)).ToLookup(f => f.Goal);
        var entries = (await goals.EntriesOfAsync(goalIds, ct)).ToLookup(e => e.Goal);

        var records = new List<GoalRecord>();
        foreach (var g in exported
                     .OrderBy(g => heads[g.Goal.ItemId].DisplayName, StringComparer.Ordinal)
                     .ThenBy(g => fileIds[g.Goal.ItemId.Value], StringComparer.Ordinal))
        {
            var head = heads[g.Goal.ItemId];
            context.Expose(g.Goal.ItemId);
            records.Add(new GoalRecord
            {
                Id = fileIds[g.Goal.ItemId.Value],
                Name = head.DisplayName,
                Kind = g.Goal.Kind,
                TargetAmount = g.Goal.TargetAmount,
                Months = g.Goal.Months,
                TargetDate = g.Goal.TargetDate is { } d ? LocalDatePattern.Iso.Format(d) : null,
                Note = g.Goal.Note,
                Visibility = head.Visibility == Visibility.Private ? "private" : "shared",
                Ownership = head.Owners
                    .Select(w => new ShareRecord { Person = context.PersonRef(w.PersonId), ShareBp = w.ShareBp })
                    .OrderBy(w => w.Person, StringComparer.Ordinal).ToList(),
                Closed = head.ArchivedAt is not null,
                Funding = funding[g.Goal.ItemId]
                    .Where(f => context.ItemFileIds.ContainsKey(f.Envelope))
                    .Select(f => new GoalFundingRecord { Envelope = context.ItemFileIds[f.Envelope], ShareBp = f.ShareBp })
                    .OrderBy(f => f.Envelope, StringComparer.Ordinal).ToList(),
                Entries = entries[g.Goal.ItemId]
                    .Select(e => new GoalEntryRecord { On = LocalDatePattern.Iso.Format(e.On), Amount = e.Amount })
                    .OrderBy(e => e.On, StringComparer.Ordinal).ToList(),
            });
        }
        return new Dictionary<string, IReadOnlyList<JsonElement>>
        {
            [Section] = records.Select(r => JsonSerializer.SerializeToElement(r, GoalsFileJson.Records.GoalRecord)).ToList(),
        };
    }

    /// <summary>Resolves the links to envelopes of the file and applies the share rules of D2 (the 100 % cap of D10 is not checked: an import restores what the file says, reads flag over_allocated); false when the goal is refused.</summary>
    private static bool TryLinks(Plan plan, ImportContext context, List<ImportIssue> issues)
    {
        var ok = true;
        for (var j = 0; j < plan.Record.Funding.Count; j++)
        {
            var link = plan.Record.Funding[j];
            if (context.FailedItems.Contains(("envelopes", link.Envelope)))
                return false; // reported with its envelope
            if (!context.Items.TryGetValue(("envelopes", link.Envelope), out var envelope))
            {
                issues.Add(ImportIssue.Error(ImportCodes.UnresolvedReference, $"'{link.Envelope}' names no entity of envelopes.",
                    $"{plan.Path}.funding[{j}].envelope"));
                ok = false;
            }
            else
                plan.Funding.Add(new FundingShare(envelope, link.ShareBp));
        }
        return ok && Check(GoalCheck.Shares(plan.Funding), plan.Path, issues);
    }

    private bool Owners(Parsed parsed, string path, List<ImportIssue> issues)
    {
        if (!rules.ValidateOwners(parsed.Visibility, parsed.Owners).TryGetFailure(out var failure))
            return true;
        issues.Add(ImportIssue.Error(failure!.Code, failure.Message, path + ".ownership"));
        return false;
    }

    private static bool NoNulls(GoalRecord record, string path, List<ImportIssue> issues)
        => ImportSectionKit.NoNulls(record.Ownership, path + ".ownership", issues)
           & ImportSectionKit.NoNulls(record.Funding, path + ".funding", issues)
           & ImportSectionKit.NoNulls(record.Entries, path + ".entries", issues);

    /// <summary>
    /// Local rules, <see cref="GoalCheck"/>'s: its API field names become the file's. <paramref name="today"/> is null on receipt
    /// (a batch alone does not ask the calendar): entries are then not checked against it.
    /// </summary>
    private static bool TryParse(GoalRecord r, string path, Func<string, ResolvedPerson?> person, LocalDate? today, List<ImportIssue> issues, out Parsed parsed)
    {
        parsed = null!;
        var months = GoalCheck.Months(r.Kind, r.Months);
        // A past target date is accepted: an import restores the past.
        var ok = Check(GoalCheck.Fields(r.Name, r.Kind, r.TargetAmount, months, null, r.Note, LocalDate.MinIsoValue), path, issues);

        var visibility = Visibility.Shared;
        if (r.Visibility == "private")
            visibility = Visibility.Private;
        else if (r.Visibility != "shared")
            ok = Invalid(issues, path + ".visibility", "Visibility is shared or private.");

        var owners = new List<Owner>();
        for (var j = 0; j < r.Ownership.Count; j++)
            if (person(r.Ownership[j].Person) is { } owner)
                owners.Add(new Owner(owner.Id, owner.Kind, r.Ownership[j].ShareBp));
            else
            {
                issues.Add(ImportIssue.Error(ImportCodes.UnresolvedReference, $"'{r.Ownership[j].Person}' is not in persons.", $"{path}.ownership[{j}].person"));
                ok = false;
            }

        LocalDate? targetDate = null;
        if (r.TargetDate is not null)
        {
            var date = LocalDatePattern.Iso.Parse(r.TargetDate);
            if (date.Success)
                targetDate = date.Value;
            else
                ok = Invalid(issues, path + ".target_date", "A date is written YYYY-MM-DD.");
        }

        var entries = new List<(LocalDate, decimal)>();
        for (var j = 0; j < r.Entries.Count; j++)
        {
            var entryPath = $"{path}.entries[{j}]";
            var date = LocalDatePattern.Iso.Parse(r.Entries[j].On);
            if (!date.Success)
                ok = Invalid(issues, entryPath + ".on", "A date is written YYYY-MM-DD.");
            else if (today is { } t && date.Value > t)
            {
                issues.Add(ImportIssue.Error(GoalsCodes.ProgressInFuture, "A progress entry is not dated in the future.", entryPath + ".on"));
                ok = false;
            }
            else if (entries.Any(e => e.Item1 == date.Value))
                ok = Invalid(issues, entryPath + ".on", "A goal has one entry per date.");
            else
                entries.Add((date.Value, r.Entries[j].Amount));

            if (r.Entries[j].Amount < 0)
            {
                issues.Add(ImportIssue.Error(GoalsCodes.InvalidTarget, "An amount reached is zero or more.", entryPath + ".amount"));
                ok = false;
            }
            else
                ok &= Check(GoalCheck.Amount(r.Entries[j].Amount, "amount"), entryPath, issues);
        }

        if (ok)
            parsed = new Parsed(r.Kind, months, targetDate, visibility, owners, entries);
        return ok;
    }

    /// <summary>A refusal of <see cref="GoalCheck"/>, at the file member its field names.</summary>
    private static bool Check(Result result, string path, List<ImportIssue> issues)
    {
        if (!result.TryGetFailure(out var failure))
            return true;
        var field = failure!.Metadata is { } m && m.TryGetValue("field", out var f) && f is string name ? name
            : FieldErrors.Of(failure)?.FirstOrDefault()?.Path;
        var member = field is null ? "" : "." + field.Replace("targetAmount", "target_amount").Replace("targetDate", "target_date")
            .Replace("shareBp", "share_bp").Replace("envelopeId", "envelope");
        issues.Add(ImportIssue.Error(failure.Code, failure.Message, path + member));
        return false;
    }

    private static bool Invalid(List<ImportIssue> issues, string path, string message)
    {
        issues.Add(ImportIssue.Error(ImportCodes.InvalidValue, message, path));
        return false;
    }

    private static Goal ToGoal(Plan plan, uint version)
    {
        var parsed = plan.Parsed;
        return new Goal(plan.Id, parsed.Kind, plan.Record.TargetAmount, parsed.Months, parsed.TargetDate, plan.Record.Note, version);
    }
}
