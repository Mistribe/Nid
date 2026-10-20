using Nid.Core.Contracts;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.RealEstate.Application;

/// <summary>
/// Tax estimate spec §6, over the visible rental properties: the rents received in the year until today, then the tenancies' rents
/// for the months starting after today; the year's expenses but energy; the interest and insurance of the year's payments of the
/// visible loans linked to them; and each property's cost price, for the 20 € per property and Pinel.
/// </summary>
public sealed class RentalTax(IVisibleItems visible, IItemRegistry items, IPropertyStore properties, ILoanStore loans, IFlows flows) : ITaxContribution
{
    public async Task<Result<IReadOnlyList<TaxLine>>> TaxLinesAsync(PersonId reader, VisibilityScope scope, int year, LocalDate today, CancellationToken ct)
    {
        var ids = await visible.ForAsync(reader, scope, includeArchived: true, ct);
        var found = (await properties.FindManyAsync(ids, ct)).Where(p => p.Details.Type == PropertyType.Rental).ToList();
        var propertyIds = found.Select(p => p.ItemId).ToList();
        var linked = (await loans.FindLinkedAsync(propertyIds, ct)).Where(l => ids.Contains(l.ItemId)).ToList();
        var accesses = await items.FindManyAsync([.. propertyIds, .. linked.Select(l => l.ItemId)], ct);
        var range = TaxLineCodes.Year(year);
        var read = await flows.ListManyAsync(propertyIds, range, new HashSet<FlowKind> { FlowKind.Income, FlowKind.Expense }, ct);
        if (read.TryGetFailure(out var failure)) return Result.Failure<IReadOnlyList<TaxLine>>(failure!);
        read.TryGetValue(out var byItem);

        var lines = new List<TaxLine>();
        foreach (var property in found)
        {
            if (!accesses.TryGetValue(property.ItemId, out var item) || !TaxLineCodes.InEuros(item, lines))
                continue;
            var d = property.Details;
            var facts = byItem!.GetValueOrDefault(property.ItemId, []);
            var rents = facts.Where(f => f.Kind == FlowKind.Income && f.Category == PropertyCategories.Rent).ToList();
            // Rents recorded up to the end of the current month; the months after, and the current one until its rent is recorded, from the tenancies.
            var thisMonth = today.With(DateAdjusters.StartOfMonth);
            var received = rents.Where(f => f.OccurredOn < thisMonth.PlusMonths(1)).Sum(f => f.Amount.Amount);
            var expected = 0m;
            for (var month = range.Start; month < range.EndBefore!.Value; month = month.PlusMonths(1))
            {
                var next = month.PlusMonths(1);
                if (month > thisMonth || (month == thisMonth && !rents.Any(f => f.OccurredOn >= month && f.OccurredOn < next)))
                    expected += property.Tenancies.Select(t => t.Tenancy).Where(t => PropertyMath.Covers(t, month)).Sum(t => t.RentMonthly);
            }
            lines.Add(new TaxLine(TaxLineCodes.Rent, item.Id, item.DisplayName, null, received + expected));
            // shortcut: the planned costs still to come this year are not projected; only the expenses recorded count.
            lines.Add(new TaxLine(TaxLineCodes.RentalCharges, item.Id, item.DisplayName, null,
                facts.Where(f => f.Kind == FlowKind.Expense && f.Category != "energy").Sum(f => f.Amount.Amount)));
            lines.Add(new TaxLine(TaxLineCodes.RentalProperty, item.Id, item.DisplayName, null,
                d.PurchasePrice + d.NotaryFees + d.AgencyFees + d.OtherAcquisitionCosts, d.PurchaseDate, d.SurfaceM2));
        }

        foreach (var loan in linked)
        {
            if (!accesses.TryGetValue(loan.ItemId, out var item) || !TaxLineCodes.InEuros(item, lines))
                continue;
            var terms = LoanReading.Terms(LoanViews.CurrencyOf(item), loan.Contract, loan.Fees, loan.Repayments);
            var interest = LoanReading.Schedule(terms, loan).Rows.Where(r => range.Contains(r.DueDate)).Sum(r => r.Interest + r.Insurance);
            lines.Add(new TaxLine(TaxLineCodes.RentalInterest, item.Id, item.DisplayName, null, interest));
        }
        return Result.Success<IReadOnlyList<TaxLine>>(lines);
    }
}
