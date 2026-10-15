using System.Globalization;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.RealEstate.Contracts;
using UnambitiousFx.Functional;

namespace Nid.RealEstate.Application.Import;

public sealed partial class RealEstateImportSection
{
    /// <summary>A property of the file; <see cref="Stored"/> is its row as stored, for one this import does not create.</summary>
    private sealed class PropertyPlan(StagedEntity entity, PropertyRecord record, ParsedProperty parsed, string path) : ItemPlan(entity, record.Id, path)
    {
        public PropertyRecord Record { get; } = record;
        public ParsedProperty Parsed { get; } = parsed;
        public StoredProperty? Stored { get; set; }

        /// <summary>The fields the property has once applied: an unchanged one keeps what is stored (an edit made in the app since).</summary>
        public PropertyDetails Details => Mode == Mode.Unchanged ? Stored!.Details : Parsed.Details;
    }

    private readonly List<PropertyPlan> propertyPlans = [];
    private readonly List<RowPlan<RecurringCost>> costPlans = [];
    private readonly List<RowPlan<Tenancy>> tenancyPlans = [];

    private async Task ValidatePropertiesAsync(IStagedBatches staged, List<ImportIssue> issues, CancellationToken ct)
    {
        propertyPlans.Clear();
        costPlans.Clear();
        tenancyPlans.Clear();

        // Properties: each resolved, and refused or planned, before any cost or tenancy is read.
        var records = ImportSectionKit.ReadAll<PropertyRecord>(staged, PropertiesSection, r => r.Id, issues,
            (r, path, found) => NoNullOwners(r.Ownership, path, found), unread: id => context.FailedItems.Add((PropertiesSection, id)));
        var known = await imports.FindAsync(EstateTable.Property, records.Select(r => r.Record.Id).ToList(), ct);
        foreach (var (entity, record) in records)
        {
            var path = NidDataJson.Path(PropertiesSection, entity.Index);
            if (!EstateParsing.TryProperty(record, path, Person, today, issues, out var parsed) || !Owners(parsed.Visibility, parsed.Owners, path, issues))
            {
                context.FailedItems.Add((PropertiesSection, record.Id)); // its costs, tenancies, loans and facts are not reported again
                continue;
            }
            var plan = new PropertyPlan(entity, record, parsed, path);
            if (await ResolveItemAsync(plan, PropertiesSection, "property", known.GetValueOrDefault(record.Id), parsed.Visibility, parsed.Owners,
                    parsed.Currency, issues, ct))
                propertyPlans.Add(plan);
        }
        // Locked in id order before they are read, as the screens lock one (PropertyCheck.LockAsync): at commit, validation runs on the
        // commit's transaction, so no app write lands between the tenancy check and the apply. A plain validate has no transaction:
        // each lock ends with its statement.
        var existing = propertyPlans.Where(p => p.Mode != Mode.Created).Select(p => p.Id).OrderBy(i => i.Value).ToList();
        foreach (var id in existing)
            await properties.LockAsync(id, ct);
        var stored = await properties.FindManyAsync(existing, ct);
        foreach (var plan in propertyPlans.Where(p => p.Mode != Mode.Created))
            plan.Stored = stored.Single(s => s.ItemId == plan.Id);
        var byFileId = propertyPlans.ToDictionary(p => p.FileId, StringComparer.Ordinal);

        var costRecords = ImportSectionKit.ReadAll<PropertyRecurringCostRecord>(staged, CostsSection, r => r.Id, issues);
        var knownCosts = await imports.FindAsync(EstateTable.RecurringCost, costRecords.Select(r => r.Record.Id).ToList(), ct);
        foreach (var (entity, record) in costRecords)
        {
            var path = NidDataJson.Path(CostsSection, entity.Index);
            if (Parent(byFileId, PropertiesSection, record.Property, path + ".property", issues) is not { } property
                || !EstateParsing.TryCost(record, path, property.Currency, issues, out var cost))
                continue;
            if (await ResolveRowAsync(CostsSection, "planned cost", entity, record.Id, path, property, "property", knownCosts,
                    (property.Stored?.Costs ?? []).Select(c => c.Id), issues, ct) is { } row)
                costPlans.Add(new RowPlan<RecurringCost>(entity, record.Id, path, property, row.Mode, row.Id, row.ImportedBy, cost));
        }

        var tenancyRecords = ImportSectionKit.ReadAll<TenancyRecord>(staged, TenanciesSection, r => r.Id, issues);
        var knownTenancies = await imports.FindAsync(EstateTable.Tenancy, tenancyRecords.Select(r => r.Record.Id).ToList(), ct);
        foreach (var (entity, record) in tenancyRecords)
        {
            var path = NidDataJson.Path(TenanciesSection, entity.Index);
            if (Parent(byFileId, PropertiesSection, record.Property, path + ".property", issues) is not { } property
                || !EstateParsing.TryTenancy(record, path, property.Currency, issues, out var tenancy))
                continue;
            if (await ResolveRowAsync(TenanciesSection, "tenancy", entity, record.Id, path, property, "property", knownTenancies,
                    (property.Stored?.Tenancies ?? []).Select(t => t.Id), issues, ct) is { } row)
                tenancyPlans.Add(new RowPlan<Tenancy>(entity, record.Id, path, property, row.Mode, row.Id, row.ImportedBy, tenancy));
        }

        // Spec 3F1 §2, the screens' rules over what each property holds once applied: no tenancy before its purchase, none
        // overlapping another. A stored tenancy the file does not rewrite counts as stored; each one written is checked against
        // those and the ones written before it in the file, so the second of two is the one refused.
        foreach (var property in propertyPlans.ToList())
        {
            var written = tenancyPlans.Where(t => t.Parent == property && t.Writes).ToList();
            var rewritten = written.Select(t => t.Id).ToHashSet();
            var others = (property.Stored?.Tenancies ?? []).Where(t => !rewritten.Contains(t.Id)).Select(t => t.Tenancy).ToList();
            var purchase = property.Details.PurchaseDate;
            if (others.Any(t => t.StartsOn < purchase))
            {
                // As the property's PATCH: a purchase the file moves after a tenancy it keeps.
                issues.Add(ImportIssue.Error(PropertyCodes.InvalidPropertyField, "A tenancy of this property starts before this purchase date.",
                    property.Path + ".purchase_date"));
                FailProperty(property);
                continue;
            }
            foreach (var plan in written)
            {
                if (!EstateParsing.Check(PropertyRules.TenancyFields(plan.Value, purchase) ?? PropertyRules.Overlap(plan.Value, others), plan.Path, issues))
                {
                    tenancyPlans.Remove(plan);
                    continue;
                }
                others.Add(plan.Value);
            }
        }

        foreach (var plan in propertyPlans)
            Register(plan, PropertiesSection);
        foreach (var plan in costPlans)
            Tally(CostsSection, Of(plan.Mode));
        foreach (var plan in tenancyPlans)
            Tally(TenanciesSection, Of(plan.Mode));
    }

    private void FailProperty(PropertyPlan property)
    {
        context.FailedItems.Add((PropertiesSection, property.FileId));
        propertyPlans.Remove(property);
        costPlans.RemoveAll(c => c.Parent == property);
        tenancyPlans.RemoveAll(t => t.Parent == property);
    }

    private async Task<Result> ApplyPropertiesAsync(CancellationToken ct)
    {
        foreach (var plan in propertyPlans.Where(p => p.Mode == Mode.Created))
        {
            var p = plan.Parsed;
            var made = await items.CreateAsync(new NewItem("realestate", "property", plan.Record.Name, p.Visibility, p.Currency.Code, OwnerShares(p.Owners)), ct);
            if (made.TryGetFailure(out var failure))
                return Result.Failure(failure!);
            made.TryGetValue(out var id);
            Applied(plan, id, PropertiesSection);
            await properties.CreateAsync(id, p.Details, ct);
            await imports.MarkAsync(EstateTable.Property, id.Value, plan.Mark, ct);
        }

        foreach (var plan in propertyPlans.Where(p => p.Mode == Mode.Replaced))
        {
            var p = plan.Parsed;
            var replaced = await items.ReplaceAsync(plan.Id, plan.Record.Name, p.Visibility, OwnerShares(p.Owners), ct);
            if (!replaced.IsSuccess)
                return replaced;
            await properties.UpdateAsync(plan.Id, p.Details, ct);
            await imports.MarkAsync(EstateTable.Property, plan.Id.Value, plan.Mark, ct);
        }

        // In place first, then added: a replaced tenancy may free the period a new one takes. Parent.Id is real by now.
        foreach (var plan in costPlans.Where(c => c.Mode == Mode.Replaced))
            await properties.UpdateCostAsync(plan.Parent.Id, plan.Id, plan.Value, ct);
        foreach (var plan in costPlans.Where(c => c.Mode == Mode.Created))
            await properties.AddCostAsync(plan.Parent.Id, plan.Id, plan.Value, ct);
        foreach (var plan in tenancyPlans.Where(t => t.Mode == Mode.Replaced))
            await properties.UpdateTenancyAsync(plan.Parent.Id, plan.Id, plan.Value, ct);
        foreach (var plan in tenancyPlans.Where(t => t.Mode == Mode.Created))
            await properties.AddTenancyAsync(plan.Parent.Id, plan.Id, plan.Value, ct);
        foreach (var plan in costPlans.Where(c => c.Writes))
            await imports.MarkAsync(EstateTable.RecurringCost, plan.Id, plan.Mark, ct);
        foreach (var plan in tenancyPlans.Where(t => t.Writes))
            await imports.MarkAsync(EstateTable.Tenancy, plan.Id, plan.Mark, ct);
        return Result.Success();
    }

    /// <summary>The readable properties, their planned costs and tenancies; answers each exported property's file id, by item id.</summary>
    private async Task<IReadOnlyDictionary<Guid, string>> ExportPropertiesAsync(ExportContext export, IReadOnlyCollection<ItemId> ids,
        IReadOnlyDictionary<ItemId, ItemAccess> heads, Dictionary<string, IReadOnlyList<JsonElement>> sections, CancellationToken ct)
    {
        var stored = (await properties.FindManyAsync(ids, ct)).Where(p => heads.ContainsKey(p.ItemId)).ToList(); // only the items that are properties
        var exported = stored.Select(p => p.ItemId).ToList();
        var marks = await imports.MarksAsync(EstateTable.Property, exported, ct);
        var fileIds = FileIds.Assign("prop", stored.Select(p => Keyed(p.ItemId.Value, marks)).ToList(), export.Reader);
        var currencies = stored.ToDictionary(p => p.ItemId.Value, p => heads[p.ItemId].Currency!);

        var records = new List<PropertyRecord>();
        foreach (var p in stored.OrderBy(p => heads[p.ItemId].DisplayName, StringComparer.Ordinal).ThenBy(p => fileIds[p.ItemId.Value], StringComparer.Ordinal))
        {
            var head = heads[p.ItemId];
            var id = fileIds[p.ItemId.Value];
            var currency = currencies[p.ItemId.Value];
            var d = p.Details;
            export.Expose(p.ItemId);
            export.NameItem(p.ItemId, id);
            records.Add(new PropertyRecord
            {
                Id = id, Name = head.DisplayName, PropertyType = LoanWire.Of(d.Type), Currency = currency, PurchaseDate = Iso(d.PurchaseDate),
                PurchasePrice = Money(d.PurchasePrice, currency), NotaryFees = Money(d.NotaryFees, currency), AgencyFees = Money(d.AgencyFees, currency),
                OtherAcquisitionCosts = Money(d.OtherAcquisitionCosts, currency),
                SurfaceM2 = d.SurfaceM2 is { } surface ? decimal.Parse(LoanReading.Amount(surface), CultureInfo.InvariantCulture) : null,
                LocationLabel = d.LocationLabel, ValuationCadence = LoanWire.Of(d.Cadence), StaleAfterDays = d.StaleAfterDays,
                RentalStartsOn = Iso(d.RentalStartsOn), Note = d.Note, Ownership = Shares(export, head), Visibility = LoanViews.Wire(head.Visibility),
            });
        }
        sections[PropertiesSection] = Elements(records);

        var costMarks = await imports.MarksAsync(EstateTable.RecurringCost, exported, ct);
        var costs = stored.SelectMany(p => p.Costs.Select(c => (Property: p.ItemId.Value, Row: c))).ToList();
        var costIds = FileIds.Assign("cost", costs.Select(c => Keyed(c.Row.Id, costMarks)).ToList(), export.Reader);
        sections[CostsSection] = Elements(costs
            .Select(c => new PropertyRecurringCostRecord
            {
                Id = costIds[c.Row.Id], Property = fileIds[c.Property], Category = c.Row.Cost.Category,
                Amount = Money(c.Row.Cost.Amount, currencies[c.Property]),
                Every = new PeriodRecord { Count = c.Row.Cost.Every.Count, Unit = LoanWire.Of(c.Row.Cost.Every.Unit) },
                StartsOn = Iso(c.Row.Cost.StartsOn), EndsOn = Iso(c.Row.Cost.EndsOn), Note = c.Row.Cost.Note,
            })
            .OrderBy(r => r.Property, StringComparer.Ordinal).ThenBy(r => r.StartsOn, StringComparer.Ordinal).ThenBy(r => r.Id, StringComparer.Ordinal));

        var tenancyMarks = await imports.MarksAsync(EstateTable.Tenancy, exported, ct);
        var tenancies = stored.SelectMany(p => p.Tenancies.Select(t => (Property: p.ItemId.Value, Row: t))).ToList();
        var tenancyIds = FileIds.Assign("ten", tenancies.Select(t => Keyed(t.Row.Id, tenancyMarks)).ToList(), export.Reader);
        sections[TenanciesSection] = Elements(tenancies
            .Select(t => new TenancyRecord
            {
                Id = tenancyIds[t.Row.Id], Property = fileIds[t.Property], Label = t.Row.Tenancy.Label, StartsOn = Iso(t.Row.Tenancy.StartsOn),
                EndsOn = Iso(t.Row.Tenancy.EndsOn), RentMonthly = Money(t.Row.Tenancy.RentMonthly, currencies[t.Property]),
                ChargesMonthly = Money(t.Row.Tenancy.ChargesMonthly, currencies[t.Property]), Deposit = Money(t.Row.Tenancy.Deposit, currencies[t.Property]),
                Note = t.Row.Tenancy.Note,
            })
            .OrderBy(r => r.Property, StringComparer.Ordinal).ThenBy(r => r.StartsOn, StringComparer.Ordinal).ThenBy(r => r.Id, StringComparer.Ordinal));

        return fileIds;
    }
}
