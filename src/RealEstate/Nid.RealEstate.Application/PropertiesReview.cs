using System.Globalization;
using Nid.Core.Contracts;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.RealEstate.Application;

/// <summary>
/// Issue #12: the yearly review's properties, every visible one held in the period (archived ones for their flows strictly before their
/// archive day): the expenses by category as the costs page counts them, the rents received, and <see cref="PropertyMath.Rental"/>'s
/// yields over the period.
/// </summary>
public sealed class PropertiesReview(
    IVisibleItems visible, IItemRegistry items, IPropertyStore properties, IFlows flows, IHouseholdCalendar calendar,
    IHouseholdSettings household, IFxRates fx) : IPropertiesReview
{
    private static readonly StringComparer French = StringComparer.Create(new CultureInfo("fr-FR"), true);

    public async Task<Result<PropertiesReviewPart>> ReviewAsync(
        PersonId reader, VisibilityScope scope, LocalDate start, LocalDate end, CancellationToken ct)
    {
        var found = await properties.FindManyAsync(await visible.ForAsync(reader, scope, includeArchived: true, ct), ct);
        var accesses = await items.FindManyAsync(found.Select(p => p.ItemId).ToList(), ct);
        var read = found.Where(p => accesses.ContainsKey(p.ItemId)).Select(p => (Property: p, Item: accesses[p.ItemId])).ToList();
        var listed = await flows.ListManyAsync(read.Select(x => x.Property.ItemId).ToList(), PropertyReading.Everything, null, ct);
        if (listed.TryGetFailure(out var failure))
            return Result.Failure<PropertiesReviewPart>(failure!);
        listed.TryGetValue(out var flowLists);
        var zone = await calendar.ZoneAsync(ct);
        var reference = await household.ReferenceCurrencyAsync(ct);
        var foreign = read.Select(x => LoanViews.CurrencyOf(x.Item)).Where(c => c != reference).Distinct().ToList();
        // The reference's own quote is needed too: rates are per USD. Without a foreign property the table is never looked up.
        var rates = foreign.Count == 0 ? RateTable.Empty(end, 0) : await fx.LoadAsync([.. foreign, reference], end, end, ct);

        var missing = new SortedSet<string>(StringComparer.Ordinal);
        decimal? Convert(decimal amount, Currency from)
        {
            if (from == reference || amount == 0m) return amount;
            if (rates.TryConvert(amount, from, reference, end, projected: false, out var converted, out _))
                return Math.Round(converted, 2, MidpointRounding.AwayFromZero);
            missing.Add(from.Code); // the property's currency, as the other parts name it
            return null;
        }

        var rows = new List<PropertyReviewRow>();
        foreach (var (property, item) in read.OrderBy(x => x.Item.DisplayName, French).ThenBy(x => x.Item.Id.Value))
        {
            // An archived property counts strictly before its archive day; one bought after the period, or archived by Start, is left out.
            var last = DashboardParts.ArchiveDay(item, zone) is { } day && day <= end ? day.PlusDays(-1) : end;
            var details = property.Details;
            if (last <= start || details.PurchaseDate > last) continue;
            var currency = LoanViews.CurrencyOf(item);
            var own = flowLists![property.ItemId];
            var byCategory = PropertiesCostsMath.PaidByCategory(own, start, last, a => Convert(a, currency))
                .Select(c => new PropertyCostCategory(c.Category, c.Amount)).ToList();
            var rental = PropertyMath.Rental(details, own, property.Tenancies.Select(t => (t.Id, t.RevisedFrom, t.Tenancy)).ToList(),
                PropertyMath.CostPrice(details, own, last), start, last);
            rows.Add(new PropertyReviewRow(item.Id, item.DisplayName, Sum(byCategory.Select(c => c.Amount)), byCategory,
                Convert(rental.RentReceived, currency), Percent(rental.GrossYield), Percent(rental.NetYieldBeforeTax)));
        }
        return Result.Success(new PropertiesReviewPart(Sum(rows.Select(r => r.Costs)), Sum(rows.Select(r => r.Rents)), rows, missing.ToList()));
    }

    private static decimal? Percent(decimal? fraction) => fraction is { } f ? Math.Round(f * 100m, 1, MidpointRounding.AwayFromZero) : null;

    /// <summary>0 with nothing to sum, null on the first unknown amount.</summary>
    private static decimal? Sum(IEnumerable<decimal?> amounts)
    {
        var total = 0m;
        foreach (var amount in amounts)
        {
            if (amount is not { } value) return null;
            total += value;
        }
        return total;
    }
}
