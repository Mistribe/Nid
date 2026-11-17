using System.Text.Json;
using Nid.Childcare.Contracts;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using NodaTime.Text;
using UnambitiousFx.Functional;

namespace Nid.Childcare.Application.Import;

/// <summary>
/// #94: the section <c>childcare_nannies</c>, a nanny at home and its months, the Goals pattern: created, unchanged, a conflict or
/// replaced (its months with it). The month rules are the API's (<see cref="NannyCheck.Month"/>).
/// </summary>
public sealed class NannyImportSection(
    IItemRegistry items, INannyStore store, IAccessPolicy policy, IItemRules rules, IVisibleItems visible, IPersonRegistry persons, IClock clock) : IImportSection
{
    public const string Section = "childcare_nannies";

    private static readonly YearMonthPattern MonthPattern = YearMonthPattern.CreateWithInvariantCulture("uuuu-MM");

    private enum Mode { Created, Unchanged, Replaced }

    private sealed record Parsed(Visibility Visibility, ShareLevel ShareLevel, IReadOnlyList<Owner> Owners, IReadOnlyList<(YearMonth Month, decimal Paid, decimal Cmg, decimal? Hours)> Months);

    private sealed class Plan(StagedEntity entity, NannyRecord record, Parsed parsed)
    {
        public StagedEntity Entity { get; } = entity;
        public NannyRecord Record { get; } = record;
        public Parsed Parsed { get; } = parsed;
        public ItemId Id { get; set; }
        public Mode Mode { get; set; }
        public bool WasClosed { get; set; }
    }

    private readonly List<Plan> plans = [];
    private IReadOnlyDictionary<string, SectionCounts> counts = new Dictionary<string, SectionCounts>();

    public string Module => "childcare";
    public IReadOnlyList<string> Sections => [Section];
    public int Order => 106;

    public IReadOnlyList<ImportIssue> ValidateBatch(string section, JsonElement entities, int firstIndex)
    {
        var issues = new List<ImportIssue>();
        var standIn = ImportSectionKit.StandIns();
        var position = 0;
        foreach (var entity in entities.EnumerateArray())
        {
            var path = NidDataJson.Path(Section, firstIndex + position++);
            if (NidDataJson.TryRead(entity, ChildcareFileJson.Records.NannyRecord, path, issues, out var record)
                && ImportSectionKit.TryId(record.Id, path, issues) && NoNulls(record, path, issues))
                TryParse(record, path, standIn, today: null, issues, out _);
        }
        return issues;
    }

    public async Task<SectionOutcome> ValidateAllAsync(ImportContext context, IStagedBatches staged, CancellationToken ct)
    {
        plans.Clear();
        var issues = new List<ImportIssue>();
        var tally = SectionCounts.Zero;
        var canRead = ImportSectionKit.Readable(policy, context.Actor, new Dictionary<ItemId, bool>(), ct);
        IReadOnlyList<PersonId>? adults = null;

        var records = ImportSectionKit.ReadAll(staged, Section, ChildcareFileJson.Records.NannyRecord, r => r.Id, issues, NoNulls);
        var known = await store.FindImportedAsync(records.Select(r => r.Record.Id).ToList(), ct);
        foreach (var (entity, record) in records)
        {
            var path = NidDataJson.Path(Section, entity.Index);
            if (!TryParse(record, path, r => context.Persons.GetValueOrDefault(r), context.Today, issues, out var parsed))
                continue;
            if (rules.ValidateOwners(parsed.Visibility, parsed.Owners).TryGetFailure(out var owners))
            {
                issues.Add(ImportIssue.Error(owners!.Code, owners.Message, path + ".ownership"));
                continue;
            }

            var plan = new Plan(entity, record, parsed);
            var candidates = known.GetValueOrDefault(record.Id)?.Select(k => new ImportCandidate(k.Item.Value, k.Hash, k.ImportedBy, [k.Item])).ToList();
            var resolution = await ImportResolver.ResolveAsync(context.Actor, candidates, canRead);
            if (resolution.OwnHidden)
            {
                issues.Add(ImportIssue.Error(ImportCodes.NotPermitted, $"'{record.Id}' was imported before as a nanny you cannot see.", path));
                continue;
            }
            if (resolution.Row is { } row)
            {
                plan.Id = new ItemId(row.Id);
                var current = (await items.FindAsync(plan.Id, ct))!;
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
                    var refusal = await ImportSectionKit.ReplaceRefusalAsync(policy, rules, context.Actor, plan.Id, current, parsed.Visibility,
                        parsed.ShareLevel, parsed.Owners, currency: null, "nanny", path, async () => adults ??= await persons.AdultIdsAsync(ct), ct);
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
                    issues.Add(ImportIssue.Error(ImportCodes.NotPermitted, "An item is imported by one of its owners, unless every owner is a dependent.", path));
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
        foreach (var plan in plans.Where(p => p.Mode != Mode.Unchanged))
        {
            var parsed = plan.Parsed;
            var owners = parsed.Owners.Select(o => new OwnerShare(o.PersonId, o.ShareBp)).ToList();
            var mark = new ImportMark(plan.Record.Id, plan.Entity.Hash, context.Actor);
            if (plan.Mode == Mode.Created)
            {
                var made = await items.CreateAsync(new NewItem(ChildcareModule.Key, ChildcareKinds.Nanny, plan.Record.Name.Trim(), parsed.Visibility, null, owners, parsed.ShareLevel), ct);
                if (!made.TryGetValue(out var id))
                    return ImportSectionKit.FailureOf(made);
                plan.Id = id;
                context.Items[(Section, plan.Record.Id)] = id;
                await store.CreateAsync(id, ct, mark);
            }
            else
            {
                var item = await items.ReplaceAsync(plan.Id, plan.Record.Name.Trim(), parsed.Visibility, owners, ct, parsed.ShareLevel);
                if (item.TryGetFailure(out var failure))
                    return Result.Failure<IReadOnlyDictionary<string, SectionCounts>>(failure!);
            }
            await store.ReplaceImportedAsync(plan.Id, parsed.Months.Select(m => new NannyMonth(plan.Id, m.Month, m.Paid, m.Cmg, m.Hours)).ToList(), mark, ct);

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

    /// <summary>Every nanny the reader can read, closed ones included, by name then file id; its months oldest first.</summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<JsonElement>>> ExportAsync(ExportContext context, CancellationToken ct)
    {
        var ids = await store.FindManyAsync((await visible.ForAsync(context.Reader, VisibilityScope.Me, includeArchived: true, ct)).ToList(), ct);
        var heads = await items.FindManyAsync(ids, ct);
        var marks = await store.ImportMarksAsync(heads.Keys.ToList(), ct);
        var fileIds = FileIds.Assign("nanny",
            heads.Keys.Select(id => new KeyedRow(id.Value, marks.GetValueOrDefault(id)?.Ref, marks.GetValueOrDefault(id)?.ImportedBy)).ToList(), context.Reader);
        var months = (await store.MonthsOfAsync(heads.Keys.ToList(), ct)).ToLookup(m => m.ItemId);

        var records = new List<NannyRecord>();
        foreach (var head in heads.Values.OrderBy(h => h.DisplayName, StringComparer.Ordinal).ThenBy(h => fileIds[h.Id.Value], StringComparer.Ordinal))
        {
            context.Expose(head.Id);
            records.Add(new NannyRecord
            {
                Id = fileIds[head.Id.Value],
                Name = head.DisplayName,
                Visibility = head.Visibility == Visibility.Private ? "private" : "shared",
                ShareLevel = ImportSectionKit.ShareLevelText(head),
                Ownership = head.Owners.Select(w => new ShareRecord { Person = context.PersonRef(w.PersonId), ShareBp = w.ShareBp })
                    .OrderBy(w => w.Person, StringComparer.Ordinal).ToList(),
                Closed = head.ArchivedAt is not null,
                Months = months[head.Id].Select(m => new NannyMonthRecord { Month = NannyViews.Format(m.Month), Paid = Exact(m.Paid), Cmg = Exact(m.Cmg), Hours = m.Hours is { } h ? Exact(h) : null }).ToList(),
            });
        }
        return new Dictionary<string, IReadOnlyList<JsonElement>>
        {
            [Section] = records.Select(r => JsonSerializer.SerializeToElement(r, ChildcareFileJson.Records.NannyRecord)).ToList(),
        };
    }

    /// <summary>A stored amount without its trailing zeros, as it was entered.</summary>
    private static decimal Exact(decimal value) => value / 1.000000000000000000000000000000000m;

    private static bool NoNulls(NannyRecord record, string path, List<ImportIssue> issues)
        => ImportSectionKit.NoNulls(record.Ownership, path + ".ownership", issues) & ImportSectionKit.NoNulls(record.Months, path + ".months", issues);

    /// <summary><paramref name="today"/> is null on receipt: the months are then not checked against it.</summary>
    private static bool TryParse(NannyRecord r, string path, Func<string, ResolvedPerson?> person, LocalDate? today, List<ImportIssue> issues, out Parsed parsed)
    {
        parsed = null!;
        var ok = true;
        foreach (var error in ChildcareCheck.Name(r.Name))
            ok = Error(issues, error.Code, error.Message, path + ".name");

        var visibility = r.Visibility switch { "private" => Visibility.Private, "shared" => Visibility.Shared, _ => (Visibility?)null };
        if (visibility is null)
            ok = Error(issues, ImportCodes.InvalidValue, "Visibility is shared or private.", path + ".visibility");
        if (ImportSectionKit.ShareLevelRefusal(r.ShareLevel, visibility ?? Visibility.Shared, out var level) is { } refused)
            ok = Error(issues, ImportCodes.InvalidValue, refused, path + ".share_level");

        var owners = new List<Owner>();
        for (var j = 0; j < r.Ownership.Count; j++)
            if (person(r.Ownership[j].Person) is { } owner)
                owners.Add(new Owner(owner.Id, owner.Kind, r.Ownership[j].ShareBp));
            else
                ok = Error(issues, ImportCodes.UnresolvedReference, $"'{r.Ownership[j].Person}' is not in persons.", $"{path}.ownership[{j}].person");

        var months = new List<(YearMonth, decimal, decimal, decimal?)>();
        for (var j = 0; j < r.Months.Count; j++)
        {
            var at = $"{path}.months[{j}]";
            var m = r.Months[j];
            var parsedMonth = MonthPattern.Parse(m.Month);
            if (!parsedMonth.Success)
            {
                ok = Error(issues, ImportCodes.InvalidValue, "A month is written YYYY-MM.", at + ".month");
                continue;
            }
            if (months.Any(x => x.Item1 == parsedMonth.Value))
                ok = Error(issues, ImportCodes.InvalidValue, "A nanny has one entry per month.", at + ".month");
            // On receipt, the far future stands for today: only the amounts are checked.
            foreach (var error in NannyCheck.Month(parsedMonth.Value, m.Paid, m.Cmg, m.Hours, today ?? LocalDate.MaxIsoValue))
                ok = Error(issues, error.Code, error.Message, $"{at}.{error.Path}");
            months.Add((parsedMonth.Value, m.Paid, m.Cmg, m.Hours));
        }

        if (ok)
            parsed = new Parsed(visibility!.Value, level, owners, months);
        return ok;
    }

    private static bool Error(List<ImportIssue> issues, string code, string message, string path)
    {
        issues.Add(ImportIssue.Error(code, message, path));
        return false;
    }
}
