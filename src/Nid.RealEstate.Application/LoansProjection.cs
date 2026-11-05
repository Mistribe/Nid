using Nid.Core.Contracts;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;

namespace Nid.RealEstate.Application;

/// <summary>Projection spec §4.2: an early repayment read for the projection, at grid month <c>Month</c>.</summary>
internal sealed record LoanRepaymentEvent(Guid Event, int Month, LocalDate On, decimal Amount, SplitRule Split, ItemId? Loan, RepaymentEffect Effect);

/// <summary>Projection spec §4.3: a sale that settles <c>Property</c>'s loans on <c>On</c>, at grid month <c>Month</c>.</summary>
internal sealed record LoanSaleEvent(Guid Event, int Month, LocalDate On, ItemId Property);

/// <summary>
/// The loans' months, ends, the repayments' outcomes and, per sale, the balance (reference currency) its loans were settled by and
/// their payments of the month after the sale, which stop with it.
/// </summary>
internal sealed record ProjectedLoans(
    IReadOnlyList<(decimal? Debt, decimal? Payments)> Months, IReadOnlyList<LoanEnd> Ends,
    IReadOnlyList<Nid.Core.Contracts.RepaymentOutcome> Repayments, IReadOnlyDictionary<Guid, (decimal Balance, decimal Payments)> Settled);

internal static partial class LoansOverviewMath
{
    /// <summary>
    /// Scenario spec §4.2 step 3 and projection spec §4.2–§4.3, §5.2: the running loans' debt (<see cref="Months"/>' rule, D8) and
    /// payments (D3, insurance included) of each month from this one, <paramref name="months"/> + 1 of them, after the events, in
    /// date order (a repayment before a sale on the same day). A figure is null when a rate is missing; a loan in a currency of
    /// <paramref name="missing"/> is left out of the figures but keeps its end.
    /// <para>
    /// A repayment applies <see cref="Simulate"/>'s rule at its date — <see cref="LoanSplit"/> over the running loans in the
    /// reference currency (D12) not yet sold, each part through <see cref="WithRepayment"/> — the amount capped at their balance
    /// (the simulation refuses it). A part the engine refuses is not applied. A sale settles its property's loans still owing at
    /// its date by their balance then: their debt is 0 from its month, and so are their payments, but one due in that month by
    /// the sale's date, which is paid; their end is the sale's date.
    /// </para>
    /// </summary>
    internal static ProjectedLoans ProjectWithEvents(
        IReadOnlyList<OverviewLoan> set, IReadOnlySet<Currency> missing, Currency reference, RateTable rates, LocalDate today, int months,
        IReadOnlyList<LoanRepaymentEvent> repayments, IReadOnlyList<LoanSaleEvent> sales)
    {
        var convert = new Converter(reference, rates, today);
        var running = set.Select(l => new Reading(l, today)).Where(r => !r.Ended).ToList();
        var touched = new HashSet<int>();
        var sold = new Dictionary<int, LoanSaleEvent>();
        var outcomes = new Nid.Core.Contracts.RepaymentOutcome[repayments.Count];
        var settled = new Dictionary<Guid, (decimal Balance, decimal Payments)>();

        var events = repayments.Select((e, i) => (e.On, Order: 0, Index: i))
            .Concat(sales.Select((e, i) => (e.On, Order: 1, Index: i)))
            .OrderBy(e => e.On).ThenBy(e => e.Order).ThenBy(e => e.Index);
        foreach (var (_, order, index) in events)
        {
            if (order == 0)
                outcomes[index] = Repay(repayments[index]);
            else
                Settle(sales[index]);
        }

        Nid.Core.Contracts.RepaymentOutcome Repay(LoanRepaymentEvent e)
        {
            var eligible = Enumerable.Range(0, running.Count)
                .Where(i => running[i].Currency == reference && !sold.ContainsKey(i)).ToList();
            if (e.Split == SplitRule.Chosen && !eligible.Any(i => running[i].Source.Item.Id == e.Loan))
                return new(e.Event, e.Month, 0m);
            var at = eligible.ToDictionary(i => i, i => LoanMath.At(running[i].Terms, running[i].Schedule, e.On));
            var candidates = eligible.Select(i => new SplitCandidate(running[i].Source.Item.Id.Value, at[i].CurrentRate ?? 0m, at[i].Balance)).ToList();
            var ceiling = e.Split == SplitRule.Chosen
                ? candidates.Single(c => c.LoanId == e.Loan!.Value.Value).Balance
                : candidates.Sum(c => c.Balance);
            var amount = Math.Min(e.Amount, ceiling);
            if (amount <= 0m)
                return new(e.Event, e.Month, 0m);
            var applied = 0m;
            foreach (var part in LoanSplit.Split(candidates, amount, e.Split, e.Loan?.Value).Where(p => p.Amount > 0m))
            {
                var i = eligible.Single(x => running[x].Source.Item.Id.Value == part.LoanId);
                var after = WithRepayment(running[i], new EarlyRepayment(e.On, part.Amount, e.Effect), today);
                if (!after.TryGetValue(out var pair))
                    continue;
                running[i] = pair.After;
                touched.Add(i);
                applied += part.Amount;
            }
            return new(e.Event, e.Month, applied);
        }

        void Settle(LoanSaleEvent e)
        {
            decimal balance = 0m, payments = 0m;
            var next = e.On.ToYearMonth().PlusMonths(1);
            for (var i = 0; i < running.Count; i++)
            {
                var r = running[i];
                if (r.Source.Loan.PropertyId != e.Property || sold.ContainsKey(i) || LoanReading.EndOf(r.Terms, r.Schedule) <= e.On)
                    continue;
                sold[i] = e;
                if (missing.Contains(r.Currency))
                    continue;
                balance += convert.Of(LoanMath.At(r.Terms, r.Schedule, e.On).Balance, r.Currency) ?? 0m;
                payments += convert.Of(r.Payments.GetValueOrDefault(next), r.Currency) ?? 0m;
            }
            settled[e.Event] = (balance, payments);
        }

        var thisMonth = today.ToYearMonth();
        var counted = Enumerable.Range(0, running.Count).Where(i => !missing.Contains(running[i].Currency)).ToList();
        var balances = counted.ToDictionary(i => i, i => Balances(running[i], thisMonth, running[i].LastMonth(today)));
        decimal Debt(int i, int k)
            => sold.TryGetValue(i, out var sale) && k >= sale.Month ? 0m : k < balances[i].Count ? balances[i][k] : 0m;
        decimal Payment(int i, int k)
        {
            var month = thisMonth.PlusMonths(k);
            if (sold.TryGetValue(i, out var sale))
            {
                if (k > sale.Month)
                    return 0m;
                // The month's payment (D3: the first one due in it) is paid only when it falls due by the sale.
                if (k == sale.Month && running[i].Schedule.Rows.FirstOrDefault(x => x.DueDate.ToYearMonth() == month) is { } row && row.DueDate > sale.On)
                    return 0m;
            }
            return running[i].Payments.GetValueOrDefault(month);
        }
        var projected = Enumerable.Range(0, months + 1).Select(k => (
                SumOrNull(counted.Select(i => convert.Of(Debt(i, k), running[i].Currency))),
                SumOrNull(counted.Select(i => convert.Of(Payment(i, k), running[i].Currency)))))
            .ToList();

        // Projection spec §3.5: each running loan's last payment, the due date of its schedule's last row; after a repayment, its end
        // (a repayment that settles it is dated after its last row); after a sale, the sale's date. A loan in a currency without a
        // rate is in: its end needs no rate.
        var ends = new List<LoanEnd>();
        for (var i = 0; i < running.Count; i++)
        {
            var r = running[i];
            LocalDate? on = sold.TryGetValue(i, out var sale) ? sale.On
                : touched.Contains(i) ? LoanReading.EndOf(r.Terms, r.Schedule)
                : r.Schedule.Rows.Count > 0 ? r.Schedule.Rows[^1].DueDate : null;
            if (on is { } end)
                ends.Add(new LoanEnd(r.Source.Item.Id, r.Source.Item.DisplayName, end, r.Source.Loan.PropertyId));
        }
        return new ProjectedLoans(projected, ends, outcomes, settled);
    }
}
