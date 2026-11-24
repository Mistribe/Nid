using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Core.Domain.Projection;

/// <summary>Spec §4.4: a purchase scenario the projection applies from grid month <c>Month</c>, with its simulated loans and the tax it adds.</summary>
public sealed record ScenarioPurchase(int Month, PropertyPurchase Document, IReadOnlyList<SimulatedLoan> Loans, decimal MonthlyTaxIncrease);

/// <summary>Retirement spec §2: a scenario's pension from a person's switch month <c>FromMonth</c>, in place of their own (<c>Own</c>, null without one).</summary>
public sealed record ScenarioPension(int FromMonth, decimal MonthlyPension, IReadOnlyList<decimal>? Own);

/// <summary>
/// Spec §4: a projection scenario's events turned into the engine's monthly arrays, from what was read for it: the inputs read with the
/// scenario's assumptions and events, what RealEstate projected of its repayments and sales, its purchases and its retirements.
/// </summary>
public static class ScenarioEvents
{
    /// <param name="inputs">The engine's inputs read with the scenario's assumptions, departures and RealEstate events.</param>
    /// <param name="baseRealEstate">The base's real estate (no event): month 0's loan payments, from which the freed payments count.</param>
    /// <param name="penalties">Each repayment's penalty, by event.</param>
    /// <param name="growth">The real estate class's yearly growth of the scenario's assumptions, for the purchased homes.</param>
    public static ProjectionInputs Apply(
        ProjectionInputs inputs, LocalDate today, int months, IReadOnlyList<RealEstateMonth> baseRealEstate, RealEstateProjection realEstate,
        IReadOnlyDictionary<Guid, decimal> penalties, IReadOnlyList<ScenarioPurchase> purchases, decimal growth,
        IReadOnlyList<ScenarioPension> pensions, IReadOnlyList<ScheduledChange> changes, IReadOnlyList<ProjectionMilestone> milestones)
    {
        var thisMonth = today.ToYearMonth();
        var adjustments = new decimal[months + 1];
        var cashFlows = new decimal[months + 1];
        var withdrawals = new List<SliceWithdrawal>();

        // Spec §4.4, as ScenarioCalculator.Project: the down payment leaves its envelopes in the purchase's month; from then each month
        // costs its loans' due payments and insurance, the charges and the tax increase, less the rent it brings or saves.
        foreach (var bought in purchases)
        {
            var purchase = bought.Document;
            withdrawals.AddRange(purchase.Funding.Select(f => new SliceWithdrawal(bought.Month, f.Item, f.Amount)));
            var rent = purchase.Use == PurchaseUse.Rental ? purchase.ExpectedRent * (12 - purchase.VacancyMonths) / 12 : purchase.CurrentRent;
            var monthlyCost = purchase.MonthlyCharges - rent + bought.MonthlyTaxIncrease;
            var rows = bought.Loans.SelectMany(l => l.Rows).ToList();
            for (var k = bought.Month; k <= months; k++)
            {
                var due = thisMonth.PlusMonths(k);
                adjustments[k] -= rows.Where(r => r.DueDate.ToYearMonth() == due).Sum(r => r.Payment + r.Insurance) + monthlyCost;
            }
        }

        // Spec §4.2: what the loans took and the penalty leave the savings in the repayment's month; nothing when no loan took any.
        foreach (var repayment in realEstate.Repayments.Where(r => r.Applied > 0m))
            cashFlows[repayment.Month] -= repayment.Applied + penalties.GetValueOrDefault(repayment.Event);
        // Spec §4.3: the proceeds enter in the sale's month; its charges less its rent are saved from the month after.
        foreach (var sale in realEstate.Sales)
        {
            cashFlows[sale.Month] += sale.Proceeds;
            for (var k = sale.Month + 1; k <= months; k++) adjustments[k] += sale.MonthlyNet;
        }
        // Retirement spec §3: the sold property's rent leaves the passive income and its costs the expenses, from the month after the sale.
        var passive = inputs.PassiveIncome?.ToArray();
        var expenseAdjustments = new decimal[months + 1];
        if (passive is not null && inputs.Rates is { } resolvedRates)
            foreach (var sale in realEstate.Sales)
                for (var k = sale.Month + 1; k <= months; k++)
                {
                    passive[k] -= sale.MonthlyRent * resolvedRates.Income[k];
                    expenseAdjustments[k] -= sale.MonthlyCosts * resolvedRates.Inflation[k];
                }

        // Spec §4.4: the purchased homes and their loans' balances join the real estate (value and debt; their payments are in the
        // monthly cost, so they are not freed payments).
        var yearly = (double)growth;
        var grid = inputs.RealEstate.Count == 0
            ? Enumerable.Range(0, months + 1).Select(k => new RealEstateMonth(thisMonth.PlusMonths(k), 0m, 0m, 0m)).ToList()
            : inputs.RealEstate.ToList();
        var property = grid.Select((m, k) =>
        {
            var value = 0m;
            var debt = 0m;
            var end = m.Month.ToDateInterval().End;
            foreach (var bought in purchases.Where(b => b.Month <= k))
            {
                value += bought.Document.Price * Decimals.FromDouble(Math.Pow(1 + yearly, (k - bought.Month) / 12.0));
                debt += bought.Document.Loans.Zip(bought.Loans, (l, s) => s.Rows.LastOrDefault(r => r.DueDate <= end) is { } row ? row.Balance : l.Amount).Sum();
            }
            return m with { PropertyValue = m.PropertyValue + value, Debt = m.Debt + debt };
        }).ToList();
        // Spec §3.3: the payments freed are counted from today's, before any event. RealEstate applies an event of this month (or
        // taken today) to month 0, so the scenario's own month 0 may already be without a sold loan's payment or with a reduced one.
        if (baseRealEstate.Count > 0)
            property[0] = property[0] with { LoanPayments = baseRealEstate[0].LoanPayments };

        // Retirement spec §2: from the person's switch month the scenario's pension replaces the person's own (added when none). A pension
        // co-owned by several adults has no holder: it has no own series, so the scenario's is added on top of it.
        var income = inputs.Income.ToArray();
        var pensionSeries = inputs.Pensions?.ToArray();
        if (inputs.Rates is { } rates)
            foreach (var pension in pensions)
                for (var k = Math.Max(pension.FromMonth, 1); k <= months; k++)
                {
                    var delta = pension.MonthlyPension * rates.Income[k] - (pension.Own?[k] ?? 0m);
                    income[k] += delta;
                    if (pensionSeries is not null) pensionSeries[k] += delta;
                }

        return inputs with
        {
            Income = income, Pensions = pensionSeries,
            RealEstate = property, MonthlyAdjustments = adjustments, CashFlows = cashFlows, Withdrawals = withdrawals, Changes = changes,
            PassiveIncome = passive, ExpenseAdjustments = expenseAdjustments,
            Milestones = [.. inputs.Milestones, .. milestones],
        };
    }
}
