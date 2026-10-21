using System.Globalization;
using Nid.Core.Contracts;
using Nid.Subscriptions.Contracts;
using Nid.Subscriptions.Domain;
using NodaTime;

namespace Nid.Subscriptions.Application;

/// <summary>
/// Spec §4.1: every block of the overview from the visible subscriptions and one rate table. The month's figures follow the
/// list (every visible subscription); the blocks ahead (months, upcoming, watch) read the active ones only.
/// </summary>
public static class OverviewMath
{
    private const int UpcomingDays = 30; // P4: today and the next 29 days
    private const int LongCycleSoonDays = 90; // spec §4.1: today + 90 included

    public static SubscriptionsOverview Build(
        IReadOnlyList<BuiltSubscription> set, IReadOnlyList<PersonInfo> persons, Currency reference, RateTable rates, LocalDate today)
    {
        var missing = new List<Finding>();
        var estimate = false;
        ConvertedTotal Convert(IEnumerable<CostPiece> pieces)
        {
            var converted = FxConversion.Convert(pieces, rates, reference);
            missing.AddRange(converted.Missing);
            estimate |= converted.IsEstimate;
            return converted;
        }
        MoneyView? View(IEnumerable<CostPiece> pieces) => Convert(pieces).Total is { } m ? MoneyView.From(m) : null;

        var window = CostWindow.Resolve(today, null, null);
        var year = CostWindow.Year(today);
        var nextMonth = window.Start.PlusMonths(1);
        var timelines = set.Select(b => b.Timeline).ToList();
        var active = set.Where(b => b.Timeline.ExpiresOn() is not { } end || end >= today).ToList();

        // The list's figures, by the list's rules.
        var summaries = set.Select(b => SubscriptionViews.Summarise(
            b.Input, b.Timeline, b.DisplayName, b.Provider, window,
            Conversion.Of(b.Timeline.CashCostPieces(window), b.Timeline.AmortizedCostPieces(window), reference, rates), b.Owners,
            FxConversion.Convert(b.Timeline.AmortizedCostPieces(year), rates, reference))).ToList();
        estimate |= summaries.Any(s => s.ConvertedIsEstimate);

        var monthlyAmortized = View(HouseholdCost.AmortizedPieces(timelines, window));
        var annualAmortized = View(HouseholdCost.AmortizedPieces(timelines, year));
        var monthCash = View(HouseholdCost.CashPieces(timelines, window));
        var monthCashRemaining = View(HouseholdCost.CashPieces(timelines, CostWindow.Resolve(today, today, nextMonth)));

        var ahead = ChargeCycles.HouseholdCharges(active).ToList();
        var months = Enumerable.Range(0, 12).Select(i =>
        {
            var start = nextMonth.PlusMonths(i);
            var inMonth = ahead.Where(x => x.Charge.Date >= start && x.Charge.Date < start.PlusMonths(1)).ToList();
            bool Recurring((BuiltSubscription Sub, Charge Charge) x)
                => ChargeCycles.PhaseOf(x.Sub.Input, x.Charge) is { } phase && !ChargeCycles.LongerThanAMonth(phase.Every);
            return new MonthView(
                start.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                View(inMonth.Where(Recurring).Select(x => Piece(x.Charge))),
                View(inMonth.Where(x => !Recurring(x)).Select(x => Piece(x.Charge))));
        }).ToList();

        var upcoming = ahead
            .Where(x => x.Charge.Date >= today && x.Charge.Date < today.PlusDays(UpcomingDays))
            .OrderBy(x => x.Charge.Date).ThenBy(x => x.Sub.DisplayName, StringComparer.Ordinal)
            .Select(x => new ChargeLineView(x.Charge.Date, x.Sub.Input.Subscription.Id, x.Sub.DisplayName, x.Sub.Provider,
                MoneyView.From(x.Charge.Price), View([Piece(x.Charge)])))
            .ToList();

        var byPerson = ListGrouping.Group(ListGroup.Person, summaries).ToDictionary(g => g.Key);
        var zero = View([]);
        var people = persons.OrderBy(p => p.Kind).Select(p =>
        {
            var owned = active.Select(b => b.Owners).Where(o => o.Any(x => x.PersonId == p.Id.Value)).ToList();
            return new PersonShareView(p.Id.Value,
                byPerson.TryGetValue(p.Id.Value.ToString(), out var group) ? group.AmortizedCostConverted : zero,
                owned.Count(o => o.Count == 1), owned.Count(o => o.Count > 1));
        }).ToList();

        var colors = set.SelectMany(b => b.Provider?.Labels.Take(1) ?? []).DistinctBy(l => l.Key).ToDictionary(l => l.Key, l => l.Color);
        var byLabel = ListGrouping.Group(ListGroup.Label, summaries)
            .Select(g => new LabelShareView(g.Key, g.Name, colors.GetValueOrDefault(g.Key), g.AmortizedCostConverted)).ToList();

        var watch = Watch(active, summaries, today, View).OrderBy(w => w.Date is null).ThenBy(w => w.Date)
            .ThenBy(w => w.Code, StringComparer.Ordinal).ToList();

        var shared = HouseholdCost.Combine(timelines, window).Shared;
        var findings = HouseholdCost.Divergences(shared).Concat(missing.DistinctBy(f => f.Reference)).Select(FindingView.From).ToList();

        return new SubscriptionsOverview(reference.Code, estimate, monthlyAmortized, annualAmortized, monthCash, monthCashRemaining,
            active.Count, set.Count - active.Count, months, people, byLabel, upcoming, watch, findings);
    }

    /// <summary>Every piece <see cref="Build"/> converts, for one rate query: the cash of this month and the twelve after, and the amortized cost of the month and the year.</summary>
    public static IEnumerable<CostPiece> RatePieces(IReadOnlyList<BuiltSubscription> set, LocalDate today)
    {
        var window = CostWindow.Resolve(today, null, null);
        var cash = CostWindow.Resolve(today, window.Start, window.Start.PlusMonths(13)); // upcoming (30 days) and watch (90 days) fall inside
        var year = CostWindow.Year(today);
        return set.SelectMany(b => b.Timeline.CashCostPieces(cash)
            .Concat(b.Timeline.AmortizedCostPieces(window)).Concat(b.Timeline.AmortizedCostPieces(year)));
    }

    private static IEnumerable<WatchView> Watch(
        IReadOnlyList<BuiltSubscription> active, IReadOnlyList<SubscriptionSummary> summaries, LocalDate today,
        Func<IEnumerable<CostPiece>, MoneyView?> view)
    {
        foreach (var b in active)
        {
            var id = b.Input.Subscription.Id.Value;
            if (!b.Input.Subscription.AutoRenew && b.Timeline.ExpiresOn() is { } end && end > today && end <= today.PlusMonths(12))
                yield return new WatchView(OverviewCodes.EndsWithoutRenewal, [id], end, null);
            foreach (var charge in ChargeCycles.FirstLongCycleCharges(b.Input, b.Timeline, today, today.PlusDays(LongCycleSoonDays)))
                yield return new WatchView(OverviewCodes.LongCycleChargeSoon, [id], charge.Date, view([Piece(charge)]));
        }

        var activeIds = active.Select(b => b.Input.Subscription.Id).ToHashSet();
        foreach (var provider in summaries.Where(s => activeIds.Contains(s.Id) && s.Provider is not null).GroupBy(s => s.Provider!.Key)
                     .Where(g => g.Count() >= 2))
        {
            // The same rule as a provider group of the list: its subtotal, null when a member has none.
            var group = ListGrouping.Group(ListGroup.Provider, provider.ToList()).Single();
            yield return new WatchView(OverviewCodes.SameProvider, group.ItemIds, null, group.AmortizedCostConverted);
        }
    }

    private static CostPiece Piece(Charge charge) => new(charge.Date, charge.Price.Currency, charge.Price.Amount, charge.IsProjected);
}
