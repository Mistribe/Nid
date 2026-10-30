using System.Globalization;
using Nid.Core.Contracts;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.RealEstate.Application;

/// <summary>
/// 5C spec §6.1, over the reader's visible loans and properties: a loan's instalment with its insurance, a planned cost's
/// occurrences, and the rent of each month a tenancy covers (a credit, one per property and month).
/// </summary>
public sealed class RealEstateExpectedCharges(IVisibleItems visible, IItemRegistry items, IPropertyStore properties, ILoanStore loans) : IExpectedCharges
{
    private static string Day(LocalDate on) => on.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public async Task<Result<IReadOnlyList<ExpectedCharge>>> ChargesAsync(PersonId reader, LocalDate from, LocalDate to, CancellationToken ct)
    {
        var ids = await visible.ForAsync(reader, VisibilityScope.Me, ct);
        var owing = await loans.FindManyAsync(ids, ct);
        var found = await properties.FindManyAsync(ids, ct);
        var accesses = await items.FindManyAsync([.. owing.Select(l => l.ItemId), .. found.Select(p => p.ItemId)], ct); // batched reads spec §3
        var charges = new List<ExpectedCharge>();

        foreach (var loan in owing)
        {
            if (!accesses.TryGetValue(loan.ItemId, out var item))
                continue;
            var terms = LoanReading.Terms(LoanViews.CurrencyOf(item), loan.Contract, loan.Fees, loan.Repayments);
            foreach (var row in LoanReading.Schedule(terms, loan).Rows.Where(r => r.DueDate >= from && r.DueDate <= to && r.Payment + r.Insurance != 0m))
                charges.Add(new ExpectedCharge("realestate", loan.ItemId, item.DisplayName, ChargeKind.LoanInstalment, Day(row.DueDate), row.DueDate,
                    PropertyFacts.MoneyOf(row.Payment + row.Insurance, terms.Currency), false, 5, loan.Lender is { } lender ? [lender] : []));
        }

        foreach (var property in found)
        {
            if (!accesses.TryGetValue(property.ItemId, out var item))
                continue;
            var currency = LoanViews.CurrencyOf(item);
            foreach (var (id, cost, _) in property.Costs.Where(c => c.Cost.Amount != 0m))
                foreach (var on in PropertyMath.DueBetween(cost, from, to.PlusDays(1)))
                    charges.Add(new ExpectedCharge("realestate", property.ItemId, item.DisplayName, ChargeKind.PropertyCost, $"cost:{id:N}:{Day(on)}", on,
                        PropertyFacts.MoneyOf(cost.Amount, currency), false, 5, [cost.Category]));

            // A revised lease is two tenancies in the month it changes: their parts add up to the month's rent.
            for (var month = new LocalDate(from.Year, from.Month, 1); month <= to; month = month.PlusMonths(1))
            {
                var rents = property.Tenancies
                    .Select(t => (t.Tenancy.Label, Rent: PropertyMath.RentIn(t.Tenancy, month)))
                    .Where(r => r.Rent is { Amount: not 0m }).ToList();
                if (rents.Count == 0)
                    continue;
                var due = rents.Min(r => r.Rent!.Value.From);
                if (due >= from && due <= to)
                    charges.Add(new ExpectedCharge("realestate", property.ItemId, item.DisplayName, ChargeKind.Rent,
                        $"rent:{month.ToString("yyyy-MM", CultureInfo.InvariantCulture)}", due,
                        PropertyFacts.MoneyOf(rents.Sum(r => r.Rent!.Value.Amount), currency), true, 10, [rents[0].Label]));
            }
        }
        return Result.Success<IReadOnlyList<ExpectedCharge>>(charges);
    }
}
