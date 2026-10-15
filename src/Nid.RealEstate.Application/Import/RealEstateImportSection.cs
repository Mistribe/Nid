using System.Globalization;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.RealEstate.Contracts;
using NodaTime;
using NodaTime.Text;
using UnambitiousFx.Functional;

namespace Nid.RealEstate.Application.Import;

/// <summary>
/// Spec 3F1 §2: the real estate sections of <c>nid.data</c>. Writes go through the same <see cref="IItemRegistry"/>,
/// <see cref="IPropertyStore"/> and <see cref="ILoanStore"/> as the API, then each written row is marked
/// (<see cref="IRealEstateImports"/>); the rules are the screens', never restated (<see cref="EstateParsing"/>). A file id
/// resolves only among rows the importer can see (<see cref="ImportResolver"/>): another adult's row the importer cannot see
/// answers exactly as an unused id. Properties and loans are items: the Core's <c>valuations</c> and <c>flows</c>, which run
/// after this section, find them through <see cref="ImportContext.Items"/> and their file ids through <see cref="ExportContext.NameItem"/>.
/// </summary>
public sealed partial class RealEstateImportSection(
    IItemRegistry items, IPropertyStore properties, ILoanStore loans, IRealEstateImports imports, IAccessPolicy policy, IItemRules rules,
    IHouseholdCalendar calendar, IVisibleItems visible, IPersonRegistry persons) : IImportSection
{
    public const string PropertiesSection = "properties";
    public const string CostsSection = "property_recurring_costs";
    public const string TenanciesSection = "tenancies";

    private enum Mode { Created, Unchanged, Replaced }

    /// <summary>A property or a loan of the file as validation decided it; <see cref="Id"/> is planned for a new one until applied.</summary>
    private abstract class ItemPlan(StagedEntity entity, string fileId, string path)
    {
        public StagedEntity Entity { get; } = entity;
        public string FileId { get; } = fileId;
        public string Path { get; } = path;
        public ItemId Id { get; set; }
        public Mode Mode { get; set; }
        public Currency Currency { get; set; }

        /// <summary>Who the row is marked as imported by: the actor for a new one, its first importer for one replaced (1F3b).</summary>
        public PersonId ImportedBy { get; set; }

        /// <summary>Whether the actor changes it: known for an item this import creates or replaces, asked once otherwise.</summary>
        public bool? Writable { get; set; }

        public ImportMark Mark => new(FileId, Entity.Hash, ImportedBy);
    }

    /// <summary>A row of the file under a property or a loan (a planned cost, a tenancy, a phase…): <see cref="Id"/> is its stored id, or a new one.</summary>
    private sealed class RowPlan<T>(StagedEntity entity, string fileId, string path, ItemPlan parent, Mode mode, Guid id, PersonId importedBy, T value)
    {
        public string Path { get; } = path;
        public ItemPlan Parent { get; } = parent;
        public Mode Mode { get; } = mode;
        public Guid Id { get; } = id;
        public T Value { get; } = value;
        public bool Writes => Mode != Mode.Unchanged;
        public ImportMark Mark { get; } = new(fileId, entity.Hash, importedBy);
    }

    private Dictionary<string, SectionCounts> tally = [];
    private ImportContext context = null!;
    private Func<ItemId, Task<bool>> canRead = null!;
    private IReadOnlyList<PersonId>? adults;
    private LocalDate today;

    public string Module => "realestate";
    public IReadOnlyList<string> Sections =>
        [PropertiesSection, CostsSection, TenanciesSection, LoansSection, PhasesSection, InsurancesSection, FeesSection, EventsSection, BankScheduleSection];
    public int Order => 40;

    public IReadOnlyList<ImportIssue> ValidateBatch(string section, JsonElement entities, int firstIndex)
    {
        var issues = new List<ImportIssue>();
        var standIn = ImportSectionKit.StandIns();
        var position = 0;
        foreach (var entity in entities.EnumerateArray())
        {
            var path = NidDataJson.Path(section, firstIndex + position++);
            switch (section)
            {
                case PropertiesSection:
                    Read<PropertyRecord>(entity, path, issues, r => r.Id, r => NoNullOwners(r.Ownership, path, issues)
                        && EstateParsing.TryProperty(r, path, f => standIn(f), today: null, issues, out _));
                    break;
                case CostsSection:
                    Read<PropertyRecurringCostRecord>(entity, path, issues, r => r.Id, r => EstateParsing.TryCost(r, path, null, issues, out _));
                    break;
                case TenanciesSection:
                    Read<TenancyRecord>(entity, path, issues, r => r.Id, r => EstateParsing.TryTenancy(r, path, null, issues, out _));
                    break;
                case LoansSection:
                    Read<LoanRecord>(entity, path, issues, r => r.Id, r => NoNullOwners(r.Ownership, path, issues)
                        && EstateParsing.TryLoan(r, path, f => standIn(f), issues, out _));
                    break;
                case PhasesSection:
                    Read<LoanPhaseRecord>(entity, path, issues, r => r.Id, r => EstateParsing.TryPhase(r, path, null, issues, out _));
                    break;
                case InsurancesSection:
                    Read<LoanInsuranceRecord>(entity, path, issues, r => r.Id, r => EstateParsing.TryInsurance(r, path, null, issues, out _));
                    break;
                case FeesSection:
                    Read<LoanFeeRecord>(entity, path, issues, r => r.Id, r => EstateParsing.TryFee(r, path, null, issues, out _));
                    break;
                case EventsSection:
                    Read<LoanEventRecord>(entity, path, issues, r => r.Id, r => EstateParsing.TryEvent(r, path, null, issues, out _));
                    break;
                case BankScheduleSection:
                    Read<LoanBankScheduleRecord>(entity, path, issues, r => r.Id, r => EstateParsing.TryBankRow(r, path, issues, out _));
                    break;
            }
        }
        return issues;
    }

    public async Task<SectionOutcome> ValidateAllAsync(ImportContext context, IStagedBatches staged, CancellationToken ct)
    {
        this.context = context;
        today = await calendar.TodayAsync(ct); // the household's, as the API decides it
        canRead = ImportSectionKit.Readable(policy, context.Actor, new Dictionary<ItemId, bool>(), ct); // one policy check per item
        adults = null;
        tally = Sections.ToDictionary(s => s, _ => SectionCounts.Zero, StringComparer.Ordinal);
        var issues = new List<ImportIssue>();

        await ValidatePropertiesAsync(staged, issues, ct);
        await ValidateLoansAsync(staged, issues, ct); // after the properties: a loan names one of them
        return new SectionOutcome(issues, tally);
    }

    public async Task<Result<IReadOnlyDictionary<string, SectionCounts>>> ApplyAsync(ImportContext context, IStagedBatches staged, CancellationToken ct)
    {
        this.context = context;
        var applied = await ApplyPropertiesAsync(ct);
        if (applied.IsSuccess)
            applied = await ApplyLoansAsync(ct);
        return applied.IsSuccess ? Result.Success<IReadOnlyDictionary<string, SectionCounts>>(tally) : ImportSectionKit.FailureOf(applied);
    }

    /// <summary>
    /// Spec 3F1 §4: every property and loan the reader can read, by name then file id, with their parts. Each gets its file id
    /// through <see cref="ExportContext.NameItem"/>, for the Core's facts, which run after this section.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<JsonElement>>> ExportAsync(ExportContext context, CancellationToken ct)
    {
        var ids = (await visible.ForAsync(context.Reader, VisibilityScope.Me, includeArchived: true, ct)).ToList();
        var heads = await items.FindManyAsync(ids, ct);
        var sections = new Dictionary<string, IReadOnlyList<JsonElement>>(StringComparer.Ordinal);
        var propertyFileIds = await ExportPropertiesAsync(context, ids, heads, sections, ct);
        await ExportLoansAsync(context, ids, heads, propertyFileIds, sections, ct);
        return sections;
    }

    /// <summary>
    /// import-format.md §8 for a property or a loan: the row the importer sees under its file id — unchanged, a conflict, or
    /// replaced under the 1F3b rules — or a new item they may create (one of its owners, unless every owner is a dependent).
    /// False when refused, the issue added and the item marked failed for the sections after.
    /// </summary>
    private async Task<bool> ResolveItemAsync(ItemPlan plan, string section, string noun, IReadOnlyList<ImportedEstateRow>? known,
        Visibility visibility, IReadOnlyList<Owner> owners, Currency currency, List<ImportIssue> issues, CancellationToken ct)
    {
        // Every importer's rows under this id: only ImportResolver's answer may reach an issue.
        var candidates = known?.Select(k => new ImportCandidate(k.Id, k.Hash, k.ImportedBy, [k.Item])).ToList();
        var resolution = await ImportResolver.ResolveAsync(context.Actor, candidates, canRead);
        if (resolution.OwnHidden)
            return Refuse(issues, ImportIssue.Error(ImportCodes.NotPermitted, $"'{plan.FileId}' was imported before as {A(noun)} you cannot see.", plan.Path), section, plan.FileId);

        plan.Currency = currency;
        if (resolution.Row is { } row)
        {
            // A row the actor reads: nothing is created, so the creation rule does not apply.
            plan.Id = new ItemId(row.Id);
            plan.ImportedBy = row.ImportedBy ?? context.Actor;
            var current = await items.FindAsync(plan.Id, ct);
            if (row.Hash == plan.Entity.Hash)
            {
                plan.Mode = Mode.Unchanged;
                plan.Currency = current is null ? currency : LoanViews.CurrencyOf(current);
                return true;
            }
            if (!context.Replace)
            {
                Tally(section, new SectionCounts(0, 0, 1));
                return Refuse(issues, ImportIssue.Error(ImportCodes.Conflict,
                    $"'{plan.FileId}' was imported before with other content; nothing is overwritten (import-format.md §8).", plan.Path), section, plan.FileId);
            }
            var refusal = await ImportSectionKit.ReplaceRefusalAsync(policy, rules, context.Actor, plan.Id, current, visibility, owners,
                currency.Code, noun, plan.Path, async () => adults ??= await persons.AdultIdsAsync(ct), ct);
            if (refusal is not null)
                return Refuse(issues, refusal, section, plan.FileId);
            plan.Mode = Mode.Replaced;
            plan.Writable = true;
            return true;
        }

        if (!rules.CanCreate(context.Actor, owners))
            return Refuse(issues, ImportIssue.Error(ImportCodes.NotPermitted,
                "An item is imported by one of its owners, unless every owner is a dependent.", plan.Path), section, plan.FileId);
        plan.Id = ItemId.New(); // planned; ApplyAsync replaces it with the real one
        plan.ImportedBy = context.Actor;
        plan.Mode = Mode.Created;
        plan.Writable = true;
        return true;
    }

    /// <summary>
    /// import-format.md §8 for a row under a property or a loan (<paramref name="parentNoun"/>, also the file member naming it):
    /// unchanged, a conflict, replaced, or created; a write needs the actor to change the item. A stored row never moves to
    /// another item. Null when refused, the issue added.
    /// </summary>
    private async Task<(Mode Mode, Guid Id, PersonId ImportedBy)?> ResolveRowAsync(string section, string noun, StagedEntity entity, string fileId,
        string path, ItemPlan parent, string parentNoun, IReadOnlyDictionary<string, IReadOnlyList<ImportedEstateRow>> known,
        IEnumerable<Guid> storedIds, List<ImportIssue> issues, CancellationToken ct)
    {
        var candidates = known.GetValueOrDefault(fileId)?.Select(k => new ImportCandidate(k.Id, k.Hash, k.ImportedBy, [k.Item])).ToList();
        var resolution = await ImportResolver.ResolveAsync(context.Actor, candidates, canRead, belongs: c => c.Items[0] == parent.Id);
        if (resolution.OwnHidden)
        {
            issues.Add(ImportIssue.Error(ImportCodes.NotPermitted, $"'{fileId}' was imported before as {A(noun)} you cannot see.", path));
            return null;
        }

        var (mode, id, importedBy) = (Mode.Created, Guid.CreateVersion7(), context.Actor);
        if (resolution.Row is { } row)
        {
            if (row.Items[0] != parent.Id || !storedIds.Contains(row.Id))
            {
                issues.Add(ImportIssue.Error(ImportCodes.InvalidValue,
                    $"A {noun} stays on its {parentNoun}: give the {noun} of the other {parentNoun} an id of its own.", path + "." + parentNoun));
                return null;
            }
            if (row.Hash == entity.Hash)
                return (Mode.Unchanged, row.Id, row.ImportedBy ?? context.Actor);
            if (!context.Replace)
            {
                issues.Add(ImportIssue.Error(ImportCodes.Conflict,
                    $"'{fileId}' was imported before with other content; nothing is overwritten (import-format.md §8).", path));
                Tally(section, new SectionCounts(0, 0, 1));
                return null;
            }
            (mode, id, importedBy) = (Mode.Replaced, row.Id, row.ImportedBy ?? context.Actor);
        }

        // A write: an existing item needs the actor to change it; one this import creates is theirs by construction.
        if (!(parent.Writable ??= await ImportSectionKit.CanAsync(policy, context.Actor, parent.Id, AccessKind.Write, ct)))
        {
            issues.Add(ImportIssue.Error(ImportCodes.NotPermitted, mode == Mode.Created
                ? $"This adds {A(noun)} to a {parentNoun} you cannot change."
                : $"This replaces {A(noun)} of a {parentNoun} you cannot change.", path));
            return null;
        }
        return (mode, id, importedBy);
    }

    /// <summary>The item a row names, among this file's: null when it names none (reported) or one already refused (reported once, there).</summary>
    private TPlan? Parent<TPlan>(IReadOnlyDictionary<string, TPlan> plans, string section, string fileId, string path, List<ImportIssue> issues)
        where TPlan : ItemPlan
    {
        if (context.FailedItems.Contains((section, fileId)))
            return null;
        if (plans.TryGetValue(fileId, out var plan))
            return plan;
        issues.Add(ImportIssue.Error(ImportCodes.UnresolvedReference, $"'{fileId}' names no entity of {section}.", path));
        return null;
    }

    /// <summary>An item validated: counted, and named for the sections after (the Core's facts).</summary>
    private void Register(ItemPlan plan, string section)
    {
        Tally(section, Of(plan.Mode));
        context.Items[(section, plan.FileId)] = plan.Id;
        context.ItemCurrencies[plan.Id] = plan.Currency;
        if (plan.Mode == Mode.Created)
            context.NewItems.Add(plan.Id);
    }

    /// <summary>A created item's real id replaces the planned one, for the sections after this one.</summary>
    private void Applied(ItemPlan plan, ItemId id, string section)
    {
        context.NewItems.Remove(plan.Id);
        context.ItemCurrencies.Remove(plan.Id);
        plan.Id = id;
        context.Items[(section, plan.FileId)] = id;
        context.NewItems.Add(id);
        context.ItemCurrencies[id] = plan.Currency;
    }

    private bool Refuse(List<ImportIssue> issues, ImportIssue issue, string section, string fileId)
    {
        issues.Add(issue);
        context.FailedItems.Add((section, fileId));
        return false;
    }

    private void Tally(string section, SectionCounts delta) => tally[section] = tally.GetValueOrDefault(section, SectionCounts.Zero) + delta;

    private static SectionCounts Of(Mode mode) => mode switch
    {
        Mode.Created => new SectionCounts(1, 0, 0),
        Mode.Unchanged => new SectionCounts(0, 1, 0),
        _ => new SectionCounts(0, 0, 0, 1),
    };

    private bool Owners(Visibility visibility, IReadOnlyList<Owner> owners, string path, List<ImportIssue> issues)
    {
        if (!rules.ValidateOwners(visibility, owners).TryGetFailure(out var failure))
            return true;
        issues.Add(ImportIssue.Error(failure!.Code, failure.Message, path + ".ownership"));
        return false;
    }

    /// <summary>The ownership list holds no null element (<see cref="ImportSectionKit.NoNulls"/>).</summary>
    private static bool NoNullOwners(IReadOnlyList<ShareRecord> ownership, string path, List<ImportIssue> issues)
        => ImportSectionKit.NoNulls(ownership, path + ".ownership", issues);

    private ResolvedPerson? Person(string fileRef) => context.Persons.GetValueOrDefault(fileRef);

    private static IReadOnlyList<OwnerShare> OwnerShares(IReadOnlyList<Owner> owners) => owners.Select(o => new OwnerShare(o.PersonId, o.ShareBp)).ToList();

    private static string A(string noun) => (noun[0] is 'a' or 'e' or 'i' or 'o' or 'u' ? "an " : "a ") + noun;

    private static void Read<T>(JsonElement entity, string path, List<ImportIssue> issues, Func<T, string> id, Func<T, bool> check) where T : class
    {
        if (NidDataJson.TryRead<T>(entity, path, issues, out var record) && ImportSectionKit.TryId(id(record), path, issues))
            check(record);
    }

    private static KeyedRow Keyed(Guid id, IReadOnlyDictionary<Guid, ImportMark> marks)
        => marks.TryGetValue(id, out var mark) ? new KeyedRow(id, mark.Ref, mark.ImportedBy) : new KeyedRow(id, null, null);

    /// <summary>An amount as the file writes it: two decimals, invariant (every stored amount has at most two), in its item's currency.</summary>
    private static MoneyRecord Money(decimal amount, string currency)
        => new() { Amount = decimal.Parse(LoanReading.Amount(amount), CultureInfo.InvariantCulture), Currency = currency };

    private static MoneyRecord? Money(decimal? amount, string currency) => amount is { } value ? Money(value, currency) : null;

    private static string Iso(LocalDate date) => LocalDatePattern.Iso.Format(date);

    private static string? Iso(LocalDate? date) => date is { } value ? Iso(value) : null;

    private static IReadOnlyList<ShareRecord> Shares(ExportContext export, ItemAccess head)
        => head.Owners.Select(o => new ShareRecord { Person = export.PersonRef(o.PersonId), ShareBp = o.ShareBp })
            .OrderBy(o => o.Person, StringComparer.Ordinal).ToList();

    private static IReadOnlyList<JsonElement> Elements<T>(IEnumerable<T> records)
        => records.Select(r => JsonSerializer.SerializeToElement(r, NidDataJson.Options)).ToList();
}
