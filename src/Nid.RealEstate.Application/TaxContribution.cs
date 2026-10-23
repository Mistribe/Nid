using Nid.Core.Contracts;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.RealEstate.Application;

/// <summary>
/// Rental 2044 spec §3–§4, over the visible rental properties: one line per property and form line — 211 the rents received in the
/// year until today (bare when recorded with their charges), then the tenancies' rents for the months starting after today; 221 to 229
/// the year's expenses by category; 222 the 20 €; 250 the interest, insurance and fees of the visible loans linked to it — for the
/// properties with activity in the year; a line per expense « À vérifier »; and each property's cost price, for Pinel.
/// </summary>
public sealed class RentalTax(IVisibleItems visible, IItemRegistry items, IPropertyStore properties, ILoanStore loans, IFlows flows) : ITaxContribution
{
    /// <summary>Spec §3: the form line a flow's category feeds, <see cref="TaxLineCodes.RentalToCheck"/> for works and other, null out of the form.</summary>
    public static string? LineOf(string? category) => category switch
    {
        PropertyCategories.Rent => TaxLineCodes.Rental211,
        PropertyCategories.ManagementFees => TaxLineCodes.Rental221,
        "insurance" => TaxLineCodes.Rental223,
        "maintenance" => TaxLineCodes.Rental224,
        "property_tax" => TaxLineCodes.Rental227,
        "condo_fees" => TaxLineCodes.Rental229,
        PropertyCategories.Works or "other" => TaxLineCodes.RentalToCheck,
        _ => null,
    };

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
        // Line 250 by property: a loan finances one property, for its whole amount.
        var interest = new Dictionary<ItemId, decimal>();
        foreach (var loan in linked)
        {
            if (!accesses.TryGetValue(loan.ItemId, out var item) || !TaxLineCodes.InEuros(item, lines, TaxLineCodes.Rental2044))
                continue;
            var terms = LoanReading.Terms(LoanViews.CurrencyOf(item), loan.Contract, loan.Fees, loan.Repayments);
            var paid = LoanReading.Schedule(terms, loan).Rows.Where(r => range.Contains(r.DueDate)).Sum(r => r.Interest + r.Insurance);
            var fees = loan.Fees.Select(f => f.Fee).Where(f => range.Contains(f.OnDate)).ToList();
            paid += fees.Where(f => f.Kind is FeeKind.File or FeeKind.Guarantee or FeeKind.Renegotiation).Sum(f => f.Amount);
            var notCounted = fees.Where(f => f.Kind is FeeKind.EarlyRepaymentPenalty or FeeKind.Other).Sum(f => f.Amount);
            if (notCounted != 0m)
                lines.Add(new TaxLine(TaxLineCodes.RentalFeeNotCounted, item.Id, item.DisplayName, null, notCounted));
            interest[loan.PropertyId!.Value] = interest.GetValueOrDefault(loan.PropertyId!.Value) + paid;
        }

        foreach (var property in found)
        {
            if (!accesses.TryGetValue(property.ItemId, out var item) || !TaxLineCodes.InEuros(item, lines, TaxLineCodes.Rental2044))
                continue;
            var d = property.Details;
            var tenancies = property.Tenancies.Select(t => t.Tenancy).ToList();
            var facts = byItem!.GetValueOrDefault(property.ItemId, []);
            var rents = facts.Where(f => f.Kind == FlowKind.Income && f.Category == PropertyCategories.Rent).ToList();
            // Rents recorded up to the end of the current month; the months after, and the current one until its rent is recorded, from the tenancies.
            var thisMonth = today.With(DateAdjusters.StartOfMonth);
            decimal received = 0m, removed = 0m;
            var matched = new HashSet<(int Tenancy, LocalDate Month)>();
            var chargeMonths = facts.Where(f => f.Category == PropertyCategories.RecoverableCharges).Select(f => f.OccurredOn.With(DateAdjusters.StartOfMonth)).ToHashSet();
            foreach (var rent in rents.Where(f => f.OccurredOn < thisMonth.PlusMonths(1)))
            {
                // Spec §4: a rent recorded before the split — one flow per tenancy, or one for the month — exactly the rents and charges
                // due counts bare; a tenancy matches one flow of the month at most. No flow is changed.
                var month = rent.OccurredOn.With(DateAdjusters.StartOfMonth);
                var amount = rent.Amount.Amount;
                // A month with its charges recorded apart was recorded the new way, and a flow equal to a tenancy's bare rent is that rent: both count as is.
                var legacy = !chargeMonths.Contains(month)
                    && !tenancies.Any(t => PropertyMath.RentIn(t, month, charges: false)?.Amount == amount);
                var single = Enumerable.Range(0, tenancies.Count).Where(i => legacy && !matched.Contains((i, month)))
                    .Select(i => (Index: i, Full: PropertyMath.RentIn(tenancies[i], month)?.Amount, Bare: PropertyMath.RentIn(tenancies[i], month, charges: false)?.Amount))
                    .FirstOrDefault(t => t.Full == amount && t.Full != t.Bare);
                decimal Due(bool charges) => tenancies.Sum(t => PropertyMath.RentIn(t, month, charges)?.Amount ?? 0m);
                decimal counted;
                if (single.Full is not null)
                {
                    matched.Add((single.Index, month));
                    counted = single.Bare!.Value;
                }
                else
                    counted = legacy && amount == Due(true) && Due(true) != Due(false) ? Due(false) : amount;
                received += counted;
                removed += amount - counted;
            }
            var expected = 0m;
            for (var month = range.Start; month < range.EndBefore!.Value; month = month.PlusMonths(1))
            {
                var next = month.PlusMonths(1);
                if (month > thisMonth || (month == thisMonth && !rents.Any(f => f.OccurredOn >= month && f.OccurredOn < next)))
                    expected += tenancies.Where(t => PropertyMath.Covers(t, month)).Sum(t => t.RentMonthly);
            }

            var amounts = new Dictionary<string, decimal> { [TaxLineCodes.Rental211] = received + expected };
            // shortcut: the planned costs still to come this year are not projected; only the expenses recorded count.
            foreach (var flow in facts.Where(f => f.Kind == FlowKind.Expense))
            {
                if (LineOf(flow.Category) is not { } code)
                    continue;
                if (code == TaxLineCodes.RentalToCheck)
                    lines.Add(new TaxLine(code, item.Id, item.DisplayName, null, flow.Amount.Amount, flow.OccurredOn, Category: flow.Category, Note: flow.Note));
                else
                    amounts[code] = amounts.GetValueOrDefault(code) + flow.Amount.Amount;
            }
            amounts[TaxLineCodes.Rental250] = interest.GetValueOrDefault(property.ItemId);
            // Spec §2: a property is in the year's form with a rent, a deductible expense or interest; the others have no 20 €.
            if (amounts.Values.Any(a => a != 0m))
            {
                amounts[TaxLineCodes.Rental222] = 20m;
                lines.AddRange(amounts.Select(a => new TaxLine(a.Key, item.Id, item.DisplayName, null, a.Value)));
            }
            if (removed != 0m)
                lines.Add(new TaxLine(TaxLineCodes.RentalChargesRemoved, item.Id, item.DisplayName, null, removed));
            lines.Add(new TaxLine(TaxLineCodes.RentalProperty, item.Id, item.DisplayName, null,
                d.PurchasePrice + d.NotaryFees + d.AgencyFees + d.OtherAcquisitionCosts, d.PurchaseDate, d.SurfaceM2));
        }
        return Result.Success<IReadOnlyList<TaxLine>>(lines);
    }
}
