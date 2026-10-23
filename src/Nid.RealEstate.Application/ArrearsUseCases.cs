using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;
using static Nid.RealEstate.Application.LoanReading;

namespace Nid.RealEstate.Application;

/// <summary>Rental arrears spec §5: the months still due of a property, and the reminder letter's content.</summary>
public sealed record GetPropertyArrearsQuery(ItemId ItemId) : IQuery<PropertyArrearsView>, IItemScoped
{
    public AccessKind Access => AccessKind.Read;
}

/// <summary>A month still due, <c>Month</c> is <c>yyyy-mm</c>; <c>InLetter</c>: the letter chases it.</summary>
public sealed record ArrearsMonthView(string Month, string Due, string Imputed, string Remaining, bool Overdue, bool InLetter);

/// <summary>
/// The letter goes to the chain of the most recent overdue month and lists that chain's overdue months, from its own account;
/// <c>LeavesOutEarlierTenant</c>: an earlier tenant's overdue months are not in it; <c>FormerTenant</c>: its lease has ended.
/// <c>Missing</c> lists the identity fields the letter cannot name.
/// </summary>
public sealed record ArrearsLetterView(
    string Total, IReadOnlyList<string> Months, string? PostalAddress, string? TenantNames, string? LandlordName, string? LandlordAddress,
    IReadOnlyList<string> Missing, bool LeavesOutEarlierTenant, bool FormerTenant);

public sealed record PropertyArrearsView(
    ItemId Property, string PropertyName, string Currency, string Total, string OverdueTotal, IReadOnlyList<ArrearsMonthView> Months, ArrearsLetterView? Letter)
    : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => [Property];
}

[RequestHandler<GetPropertyArrearsQuery, PropertyArrearsView>]
public sealed class GetPropertyArrearsHandler(IPropertyStore properties, IItemRegistry items, IFlows flows, IHouseholdCalendar calendar)
    : IRequestHandler<GetPropertyArrearsQuery, PropertyArrearsView>
{
    public async ValueTask<Result<PropertyArrearsView>> HandleAsync(GetPropertyArrearsQuery query, CancellationToken ct = default)
    {
        var loaded = await PropertyCheck.LoadAsync(properties, items, query.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<PropertyArrearsView>(failure!);
        loaded.TryGetValue(out var found);
        var listed = await flows.ListAsync(found.Property.ItemId, PropertyReading.Everything, null, ct);
        if (listed.TryGetFailure(out failure))
            return Result.Failure<PropertyArrearsView>(failure!);
        listed.TryGetValue(out var all);
        var stored = found.Property.Tenancies;
        var today = await calendar.TodayAsync(ct);
        var months = RentArrears.Of(stored.Select(t => (t.Id, t.RevisedFrom, t.Tenancy)).ToList(), all!, today);
        var overdue = months.Where(m => m.Overdue).ToList();

        ArrearsLetterView? letter = null;
        Guid? chain = null;
        if (overdue.Count > 0)
        {
            chain = overdue[^1].Chain; // the most recent row: by month, then the later tenant
            var inLetter = overdue.Where(m => m.Chain == chain).ToList();
            // A revision never changes the parties: the identity is the chain's last link's, the one the screen edits.
            var lease = stored.FirstOrDefault(t => t.Id == chain)?.Tenancy;
            var postal = found.Property.Details.PostalAddress;
            var missing = new List<string>();
            if (postal is null) missing.Add("postal_address");
            if (lease?.TenantNames is null) missing.Add("tenant_names");
            if (lease?.LandlordName is null) missing.Add("landlord_name");
            if (lease?.LandlordAddress is null) missing.Add("landlord_address");
            letter = new ArrearsLetterView(Amount(inLetter.Sum(m => m.Remaining)), inLetter.Select(m => ReceiptMonths.Wire(m.Month)).ToList(), postal,
                lease?.TenantNames, lease?.LandlordName, lease?.LandlordAddress, missing, inLetter.Count < overdue.Count,
                lease?.EndsOn is { } ended && ended <= today);
        }

        return Result.Success(new PropertyArrearsView(
            found.Property.ItemId, found.Item.DisplayName, LoanViews.CurrencyOf(found.Item).Code, Amount(months.Sum(m => m.Remaining)),
            Amount(overdue.Sum(m => m.Remaining)),
            months.Select(m => new ArrearsMonthView(ReceiptMonths.Wire(m.Month), Amount(m.Due), Amount(m.Imputed), Amount(m.Remaining), m.Overdue,
                m.Overdue && m.Chain == chain)).ToList(), letter));
    }
}

/// <summary>Rental arrears spec §5 (<c>get_rent_arrears</c>): the properties of the scope with months still due. No identity.</summary>
public sealed record GetRentArrearsQuery(VisibilityScope Scope) : IQuery<RentArrearsList>;

public sealed record RentArrearsMonthView(string Month, string Due, string Imputed, string Remaining, bool Overdue);

public sealed record RentArrearsItem(
    ItemId Property, string Name, string Currency, string Total, string OverdueTotal, IReadOnlyList<RentArrearsMonthView> Months);

public sealed record RentArrearsList(IReadOnlyList<RentArrearsItem> Items) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => Items.Select(i => i.Property);
}

[RequestHandler<GetRentArrearsQuery, RentArrearsList>]
public sealed class GetRentArrearsHandler(
    IVisibleItems visible, IItemRegistry items, IPropertyStore properties, IFlows flows, IHouseholdCalendar calendar, IActorAccessor actor)
    : IRequestHandler<GetRentArrearsQuery, RentArrearsList>
{
    public async ValueTask<Result<RentArrearsList>> HandleAsync(GetRentArrearsQuery query, CancellationToken ct = default)
    {
        var ids = await visible.ForAsync(actor.Current!.PersonId!.Value, query.Scope, ct);
        var today = await calendar.TodayAsync(ct);
        var found = await properties.FindManyAsync(ids.ToList(), ct);
        var accesses = await items.FindManyAsync(found.Select(p => p.ItemId).ToList(), ct);
        var result = new List<RentArrearsItem>();
        foreach (var p in found.Where(p => accesses.ContainsKey(p.ItemId) && p.Tenancies.Count > 0))
        {
            var listed = await flows.ListAsync(p.ItemId, PropertyReading.Everything, null, ct);
            if (listed.TryGetFailure(out var failure))
                return Result.Failure<RentArrearsList>(failure!);
            listed.TryGetValue(out var all);
            var months = RentArrears.Of(p.Tenancies.Select(t => (t.Id, t.RevisedFrom, t.Tenancy)).ToList(), all!, today);
            if (months.Count == 0)
                continue;
            var item = accesses[p.ItemId];
            result.Add(new RentArrearsItem(p.ItemId, item.DisplayName, LoanViews.CurrencyOf(item).Code, Amount(months.Sum(m => m.Remaining)),
                Amount(months.Where(m => m.Overdue).Sum(m => m.Remaining)),
                months.Select(m => new RentArrearsMonthView(ReceiptMonths.Wire(m.Month), Amount(m.Due), Amount(m.Imputed), Amount(m.Remaining), m.Overdue)).ToList()));
        }
        return Result.Success(new RentArrearsList(result.OrderBy(i => i.Name).ThenBy(i => i.Property.Value).ToList()));
    }
}
