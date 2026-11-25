using System.Text.Json;
using System.Text.RegularExpressions;
using Nid.Assets.Contracts;
using Nid.Assets.Domain;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using NodaTime.Text;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Assets.Application.Import;

/// <summary>
/// The section <c>assets</c> (#186): an asset of the file, as an item with its record. Writes go through the same
/// <see cref="IItemRegistry"/> and <see cref="IAssetRecordStore"/> as the API; the field rules are <see cref="AssetRules"/>'s.
/// Its valuations are the Core's <c>valuations</c>, which run after this section and find the asset through
/// <see cref="ImportContext.Items"/>; "a value is always present" is checked on the file alone: a purchase price, or a valuation naming
/// the asset in <c>item</c>, whatever is stored already.
/// </summary>
public sealed partial class AssetsImportSection(
    IItemRegistry items, IAssetRecordStore assets, IAccessPolicy policy, IItemRules rules, IHouseholdCalendar calendar,
    IVisibleItems visible, IPersonRegistry persons, IClock clock) : IImportSection
{
    public const string Section = "assets";
    private const string ValuationsSection = "valuations"; // the Core's: its entities name their asset in `item`

    private enum Mode { Created, Unchanged, Replaced }

    private sealed record Parsed(
        AssetDetails Details, Currency Currency, Visibility Visibility, IReadOnlyList<Owner> Owners, ShareLevel ShareLevel);

    private sealed class Plan(StagedEntity entity, AssetImportRecord record, Parsed parsed, string path)
    {
        public StagedEntity Entity { get; } = entity;
        public AssetImportRecord Record { get; } = record;
        public Parsed Parsed { get; } = parsed;
        public string Path { get; } = path;
        public ItemId Id { get; set; }
        public Mode Mode { get; set; }
        public bool WasClosed { get; set; }
        public ImportMark Mark => new(Record.Id, Entity.Hash, ImportedBy);
        public PersonId ImportedBy { get; set; }
    }

    private readonly List<Plan> plans = [];
    private IReadOnlyDictionary<string, SectionCounts> counts = new Dictionary<string, SectionCounts>();

    public string Module => "assets";
    public IReadOnlyList<string> Sections => [Section];
    public int Order => 5; // after the persons, before the subscriptions that link to an asset (#648) and the Core's facts (90)

    public IReadOnlyList<ImportIssue> ValidateBatch(string section, JsonElement entities, int firstIndex)
    {
        var issues = new List<ImportIssue>();
        var standIn = ImportSectionKit.StandIns();
        var position = 0;
        foreach (var entity in entities.EnumerateArray())
        {
            var path = NidDataJson.Path(Section, firstIndex + position++);
            if (NidDataJson.TryRead(entity, AssetsFileJson.Records.AssetImportRecord, path, issues, out var record)
                && ImportSectionKit.TryId(record.Id, path, issues)
                && ImportSectionKit.NoNulls(record.Ownership, path + ".ownership", issues))
                TryParse(record, path, standIn, LocalDate.MaxIsoValue, issues, out _);
        }
        return issues;
    }

    public async Task<SectionOutcome> ValidateAllAsync(ImportContext context, IStagedBatches staged, CancellationToken ct)
    {
        plans.Clear();
        var issues = new List<ImportIssue>();
        var tally = SectionCounts.Zero;
        var today = await calendar.TodayAsync(ct);
        var canRead = ImportSectionKit.Readable(policy, context.Actor, new Dictionary<ItemId, bool>(), ct); // one policy check per item
        IReadOnlyList<PersonId>? adults = null;                                                           // read once, by the first replaced asset

        // The assets some valuation of the file names: a value is a purchase price or one of them.
        var valued = staged.Section(ValuationsSection)
            .Select(v => v.Value.ValueKind == JsonValueKind.Object && v.Value.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null)
            .OfType<string>().ToHashSet(StringComparer.Ordinal);

        var records = ImportSectionKit.ReadAll(staged, Section, AssetsFileJson.Records.AssetImportRecord, r => r.Id, issues,
            (r, path, found) => ImportSectionKit.NoNulls(r.Ownership, path + ".ownership", found), unread: id => context.FailedItems.Add((Section, id)));
        var known = await assets.FindImportedAsync(records.Select(r => r.Record.Id).ToList(), ct);
        foreach (var (entity, record) in records)
        {
            var path = NidDataJson.Path(Section, entity.Index);
            if (!TryParse(record, path, r => context.Persons.GetValueOrDefault(r), today, issues, out var parsed)
                || !Owners(parsed, path, issues))
            {
                context.FailedItems.Add((Section, record.Id)); // its valuations are not reported again
                continue;
            }
            if (!AssetRules.HasValue(parsed.Details, valued.Contains(record.Id) ? 1 : 0))
            {
                issues.Add(ImportIssue.Error(AssetsCodes.ValueRequired, "An asset has a purchase price or at least one valuation in the file.", path));
                context.FailedItems.Add((Section, record.Id));
                continue;
            }

            var plan = new Plan(entity, record, parsed, path);
            var candidates = known.GetValueOrDefault(record.Id)?
                .Select(k => new ImportCandidate(k.Item.Value, k.Hash, k.ImportedBy, [k.Item])).ToList();
            var resolution = await ImportResolver.ResolveAsync(context.Actor, candidates, canRead);
            if (resolution.OwnHidden)
            {
                issues.Add(ImportIssue.Error(ImportCodes.NotPermitted, $"'{record.Id}' was imported before as an asset you cannot see.", path));
                context.FailedItems.Add((Section, record.Id));
                continue;
            }

            var currency = parsed.Currency;
            if (resolution.Row is { } row)
            {
                plan.Id = new ItemId(row.Id);
                plan.ImportedBy = row.ImportedBy ?? context.Actor;
                var current = await items.FindAsync(plan.Id, ct);
                plan.WasClosed = current?.ArchivedAt is not null;
                if (row.Hash == entity.Hash)
                {
                    plan.Mode = Mode.Unchanged;
                    if (current?.Currency is { } stored && Currency.TryCreate(stored, out var storedCurrency))
                        currency = storedCurrency;
                }
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
                    var refusal = await ImportSectionKit.ReplaceRefusalAsync(policy, rules, context.Actor, plan.Id, current, parsed.Visibility,
                        parsed.ShareLevel, parsed.Owners, parsed.Currency.Code, "asset", path, async () => adults ??= await persons.AdultIdsAsync(ct), ct);
                    if (refusal is not null)
                    {
                        issues.Add(refusal);
                        context.FailedItems.Add((Section, record.Id));
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
                    context.FailedItems.Add((Section, record.Id));
                    continue;
                }
                plan.Id = ItemId.New(); // planned; ApplyAsync replaces it with the real one
                plan.ImportedBy = context.Actor;
                plan.Mode = Mode.Created;
            }
            plans.Add(plan);
            context.Items[(Section, record.Id)] = plan.Id;
            context.ItemCurrencies[plan.Id] = currency;
            if (plan.Mode == Mode.Created)
                context.NewItems.Add(plan.Id);
        }

        foreach (var plan in plans)
            tally += plan.Mode switch
            {
                Mode.Created => new SectionCounts(1, 0, 0),
                Mode.Unchanged => new SectionCounts(0, 1, 0),
                _ => new SectionCounts(0, 0, 0, 1),
            };
        counts = new Dictionary<string, SectionCounts> { [Section] = tally };
        return new SectionOutcome(issues, counts);
    }

    public async Task<Result<IReadOnlyDictionary<string, SectionCounts>>> ApplyAsync(ImportContext context, IStagedBatches staged, CancellationToken ct)
    {
        foreach (var plan in plans.Where(p => p.Mode != Mode.Unchanged))
        {
            var parsed = plan.Parsed;
            var owners = parsed.Owners.Select(o => new OwnerShare(o.PersonId, o.ShareBp)).ToList();
            if (plan.Mode == Mode.Created)
            {
                var made = await items.CreateAsync(
                    new NewItem("assets", "asset", plan.Record.Name, parsed.Visibility, parsed.Currency.Code, owners, parsed.ShareLevel), ct);
                if (!made.TryGetValue(out var id))
                    return ImportSectionKit.FailureOf(made);
                context.NewItems.Remove(plan.Id);
                context.ItemCurrencies.Remove(plan.Id);
                plan.Id = id;
                context.Items[(Section, plan.Record.Id)] = id;
                context.NewItems.Add(id);
                context.ItemCurrencies[id] = parsed.Currency;
                await assets.AddAsync(id, parsed.Details, ct, plan.Mark);
            }
            else
            {
                var item = await items.ReplaceAsync(plan.Id, plan.Record.Name, parsed.Visibility, owners, ct, parsed.ShareLevel);
                if (item.TryGetFailure(out var failure))
                    return Result.Failure<IReadOnlyDictionary<string, SectionCounts>>(failure!);
                await assets.ReplaceAsync(plan.Id, parsed.Details, plan.Mark, ct);
            }

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

    /// <summary>Every asset the reader can read, archived ones included, by name then file id. Each is named for the Core's valuations.</summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<JsonElement>>> ExportAsync(ExportContext context, CancellationToken ct)
    {
        var ids = (await visible.ForAsync(context.Reader, VisibilityScope.Me, includeArchived: true, ct)).ToList();
        var stored = await assets.FindManyAsync(ids, ct);
        var heads = await items.FindManyAsync(stored.Select(a => a.ItemId).ToList(), ct);
        var exported = stored.Where(a => heads.ContainsKey(a.ItemId)).ToList();
        var marks = await assets.MarksAsync(exported.Select(a => a.ItemId).ToList(), ct);
        var fileIds = FileIds.Assign("asset",
            exported.Select(a => marks.TryGetValue(a.ItemId.Value, out var m) ? new KeyedRow(a.ItemId.Value, m.Ref, m.ImportedBy) : new KeyedRow(a.ItemId.Value, null, null)).ToList(),
            context.Reader);

        var records = new List<AssetImportRecord>();
        foreach (var a in exported
                     .OrderBy(a => heads[a.ItemId].DisplayName, StringComparer.Ordinal)
                     .ThenBy(a => fileIds[a.ItemId.Value], StringComparer.Ordinal))
        {
            var head = heads[a.ItemId];
            var d = a.Details;
            context.Expose(a.ItemId);
            context.NameItem(a.ItemId, fileIds[a.ItemId.Value]);
            records.Add(new AssetImportRecord
            {
                Id = fileIds[a.ItemId.Value],
                Name = head.DisplayName,
                Kind = d.Kind,
                Currency = head.Currency!,
                PurchasePrice = d.PurchasePrice,
                PurchasedOn = d.PurchasedOn is { } on ? LocalDatePattern.Iso.Format(on) : null,
                Note = d.Note,
                WeightGrams = d.WeightGrams,
                Fineness = d.Fineness,
                RegisteredOn = d.RegisteredOn is { } reg ? LocalDatePattern.Iso.Format(reg) : null,
                MileageKm = d.MileageKm,
                MileageOn = d.MileageOn is { } kmOn ? LocalDatePattern.Iso.Format(kmOn) : null,
                FiscalHp = d.FiscalHp,
                Energy = d.Energy,
                DepreciationRate = d.DepreciationRate,
                Visibility = head.Visibility == Visibility.Private ? "private" : "shared",
                ShareLevel = ImportSectionKit.ShareLevelText(head),
                Ownership = head.Owners
                    .Select(w => new ShareRecord { Person = context.PersonRef(w.PersonId), ShareBp = w.ShareBp })
                    .OrderBy(w => w.Person, StringComparer.Ordinal).ToList(),
                Closed = head.ArchivedAt is not null,
            });
        }
        return new Dictionary<string, IReadOnlyList<JsonElement>>
        {
            [Section] = records.Select(r => JsonSerializer.SerializeToElement(r, AssetsFileJson.Records.AssetImportRecord)).ToList(),
        };
    }

    private static LocalDate? OptionalDate(string? text, string path, List<ImportIssue> issues, ref bool ok)
    {
        if (text is null)
            return null;
        var date = LocalDatePattern.Iso.Parse(text);
        if (date.Success)
            return date.Value;
        ok = Invalid(issues, path, "A date is written YYYY-MM-DD.");
        return null;
    }

    private bool Owners(Parsed parsed, string path, List<ImportIssue> issues)
    {
        if (!rules.ValidateOwners(parsed.Visibility, parsed.Owners).TryGetFailure(out var failure))
            return true;
        issues.Add(ImportIssue.Error(failure!.Code, failure.Message, path + ".ownership"));
        return false;
    }

    /// <summary>Local rules: <see cref="AssetCheck"/>'s and <see cref="AssetRules"/>', their API field names become the file's.</summary>
    private static bool TryParse(AssetImportRecord r, string path, Func<string, ResolvedPerson?> person, LocalDate today, List<ImportIssue> issues, out Parsed parsed)
    {
        parsed = null!;
        var ok = true;
        if (!Currency.TryCreate(r.Currency, out var currency))
            ok = Invalid(issues, path + ".currency", "A currency is a three-letter code.");

        var visibility = Visibility.Shared;
        if (r.Visibility == "private")
            visibility = Visibility.Private;
        else if (r.Visibility != "shared")
            ok = Invalid(issues, path + ".visibility", "Visibility is shared or private.");
        if (ImportSectionKit.ShareLevelRefusal(r.ShareLevel, visibility, out var level) is { } refused)
            ok = Invalid(issues, path + ".share_level", refused);

        var owners = new List<Owner>();
        for (var j = 0; j < r.Ownership.Count; j++)
            if (person(r.Ownership[j].Person) is { } owner)
                owners.Add(new Owner(owner.Id, owner.Kind, r.Ownership[j].ShareBp));
            else
            {
                issues.Add(ImportIssue.Error(ImportCodes.UnresolvedReference, $"'{r.Ownership[j].Person}' is not in persons.", $"{path}.ownership[{j}].person"));
                ok = false;
            }

        LocalDate? purchasedOn = null;
        if (r.PurchasedOn is not null)
        {
            var date = LocalDatePattern.Iso.Parse(r.PurchasedOn);
            if (date.Success)
                purchasedOn = date.Value;
            else
                ok = Invalid(issues, path + ".purchased_on", "A date is written YYYY-MM-DD.");
        }

        var registeredOn = OptionalDate(r.RegisteredOn, path + ".registered_on", issues, ref ok);
        var mileageOn = OptionalDate(r.MileageOn, path + ".mileage_on", issues, ref ok);
        var details = new AssetDetails(r.Kind, r.PurchasePrice, purchasedOn, r.Note, r.WeightGrams, r.Fineness,
            registeredOn, r.MileageKm, mileageOn, r.FiscalHp, r.Energy, r.DepreciationRate);
        var failure = AssetCheck.Name(r.Name, r.Note) ?? AssetRules.Check(details, today);
        if (failure is not null)
        {
            var field = failure.Metadata is { } m && m.TryGetValue("field", out var f) && f is string name ? name : FieldErrors.Of(failure)?.FirstOrDefault()?.Path;
            issues.Add(ImportIssue.Error(failure.Code, failure.Message, field is null ? path : path + "." + Snake(field)));
            ok = false;
        }

        if (ok)
            parsed = new Parsed(details, currency, visibility, owners, level);
        return ok;
    }

    private static string Snake(string field) => UpperCase().Replace(field, m => "_" + char.ToLowerInvariant(m.Value[0]));

    [GeneratedRegex("[A-Z]")]
    private static partial Regex UpperCase();

    private static bool Invalid(List<ImportIssue> issues, string path, string message)
    {
        issues.Add(ImportIssue.Error(ImportCodes.InvalidValue, message, path));
        return false;
    }
}
