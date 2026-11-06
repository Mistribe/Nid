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
        IReadOnlyList<BuiltSubscription> set, IReadOnlyList<PersonInfo> persons, Currency reference, RateTable rates, LocalDate today, InflationSeries inflation)
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
            FxConversion.Convert(b.Timeline.AmortizedCostPieces(year), rates, reference),
            SpendingHistory.Spent(b.Timeline, reference, rates, today))).ToList();
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
                View(inMonth.Where(Recurring).Select(x => SpendingHistory.Piece(x.Charge))),
                View(inMonth.Where(x => !Recurring(x)).Select(x => SpendingHistory.Piece(x.Charge))));
        }).ToList();

        var upcoming = ahead
            .Where(x => x.Charge.Date >= today && x.Charge.Date < today.PlusDays(UpcomingDays))
            .OrderBy(x => x.Charge.Date).ThenBy(x => x.Sub.DisplayName, StringComparer.Ordinal)
            .Select(x => new ChargeLineView(x.Charge.Date, x.Sub.Input.Subscription.Id, x.Sub.DisplayName, x.Sub.Provider,
                MoneyView.From(x.Charge.Price), View([SpendingHistory.Piece(x.Charge)])))
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

        // #395: the providers by what their subscriptions, ended ones included, have cost: the list's provider groups, each charge once.
        var spentOf = SpendingHistory.SpentOf(set, reference, rates);
        var providers = summaries.Where(s => s.Provider is not null).GroupBy(s => s.Provider!.Key)
            .Select(g => (Provider: g.First().Provider!, Group: ListGrouping.Group(ListGroup.Provider, g.ToList(), spentOf).Single()))
            .Where(p => p.Group.SpentTotal is not null)
            .OrderByDescending(p => decimal.Parse(p.Group.SpentTotal!.Amount, CultureInfo.InvariantCulture))
            .ThenBy(p => p.Provider.Name, StringComparer.Ordinal)
            .Take(TopProviders)
            .Select(p => new TopProviderView(p.Provider, p.Group.SpentTotal, p.Group.SpentPartial)).ToList();

        var years = SpendingHistory.Years(set, today, View);
        var savings = Savings(set, today, reference, rates);
        var rises = Rises(set, today, reference, rates, inflation);

        var shared = HouseholdCost.Combine(timelines, window).Shared;
        var findings = HouseholdCost.Divergences(shared).Concat(missing.DistinctBy(f => f.Reference)).Select(FindingView.From).ToList();

        return new SubscriptionsOverview(reference.Code, estimate, monthlyAmortized, annualAmortized, monthCash, monthCashRemaining,
            active.Count, set.Count - active.Count, months, people, byLabel, upcoming, watch, findings, years, providers, savings, rises);
    }

    private const int TopProviders = 5;

    /// <summary>#397: over the last 12 months, what each ended subscription without successor no longer charged.</summary>
    private static SavingsView Savings(IReadOnlyList<BuiltSubscription> set, LocalDate today, Currency reference, RateTable rates)
    {
        var items = set.Select(b => (Sub: b, Forgone: SpendingHistory.Forgone(b, set, today)))
            .Where(x => x.Forgone is not null)
            .Select(x => (x.Sub, x.Forgone!.Value.End, Pieces: x.Forgone.Value.Pieces.Where(p => p.ChargeDate > today.PlusMonths(-12)).ToList()))
            .Where(x => x.Pieces.Count > 0)
            .ToList();
        var total = FxConversion.Convert(items.SelectMany(x => x.Pieces), rates, reference, partial: true);
        return new SavingsView(total.Total is { } t ? MoneyView.From(t) : null, total.Missing.Count > 0,
            items.Select(x => (x, Amount: FxConversion.Convert(x.Pieces, rates, reference, partial: true).Total))
                .OrderByDescending(x => x.Amount?.Amount ?? 0m).ThenBy(x => x.x.Sub.DisplayName, StringComparer.Ordinal)
                .Select(x => new SavedItemView(x.x.Sub.Input.Subscription.Id, x.x.Sub.DisplayName, x.x.Sub.Provider, x.x.End,
                    x.Amount is { } m ? MoneyView.From(m) : null)).ToList());
    }

    /// <summary>#398: the dearer phases of the last 12 months, latest first; the total leaves the promotions ending out.</summary>
    private static PriceRisesView Rises(IReadOnlyList<BuiltSubscription> set, LocalDate today, Currency reference, RateTable rates, InflationSeries inflation)
    {
        var rises = SpendingHistory.Rises(set, today).OrderByDescending(r => r.To.StartsOn)
            .ThenBy(r => r.Sub.DisplayName, StringComparer.Ordinal).ToList();
        var total = FxConversion.Convert(rises.Where(r => r.From.MaxCycles is null).Select(r => r.Difference), rates, reference);
        return new PriceRisesView(total.Total is { } t && rises.Any(r => r.From.MaxCycles is null) ? MoneyView.From(t) : null,
            rises.Select(r =>
            {
                var pct = ChangePct(r.From, r.Difference.Amount, r.To.StartsOn);
                return new PriceRiseView(r.Sub.Input.Subscription.Id, r.Sub.DisplayName, r.Sub.Provider, r.To.StartsOn,
                    MoneyView.From(r.From.Price), DurationView.From(r.From.Every), MoneyView.From(r.To.Price), DurationView.From(r.To.Every),
                    FxConversion.Convert([r.Difference], rates, reference).Total is { } d ? MoneyView.From(d) : null,
                    r.From.MaxCycles is not null, pct,
                    inflation.RealChange(pct, MonthOf(r.From.StartsOn), MonthOf(r.To.StartsOn), r.To.Price.Currency));
            }).ToList());
    }

    private static YearMonth MonthOf(LocalDate date) => new(date.Year, date.Month);

    /// <summary>The yearly cost change of a rise in percent, both yearly amounts taken at the rise's date; null from a free phase.</summary>
    private static decimal? ChangePct(BillingPhase from, decimal difference, LocalDate on)
        => SpendingHistory.Yearly(from, on) is > 0m and var old ? Math.Round(difference / old * 100, 1, MidpointRounding.AwayFromZero) : null;

    /// <summary>Every piece <see cref="Build"/> converts, for one rate query: the cash of this month and the twelve after, and the amortized cost of the month and the year.</summary>
    public static IEnumerable<CostPiece> RatePieces(IReadOnlyList<BuiltSubscription> set, LocalDate today)
    {
        var window = CostWindow.Resolve(today, null, null);
        var cash = CostWindow.Resolve(today, window.Start, window.Start.PlusMonths(13)); // upcoming (30 days) and watch (90 days) fall inside
        var year = CostWindow.Year(today);
        // The years (#396) and the spent totals (#395) read every charge up to December, the savings and price rises their own pieces.
        return set.SelectMany(b => b.Timeline.CashCostPieces(cash)
                .Concat(b.Timeline.AmortizedCostPieces(window)).Concat(b.Timeline.AmortizedCostPieces(year))
                .Concat(b.Timeline.Charges.Where(c => c.Date.Year <= today.Year).Select(SpendingHistory.Piece))
                .Concat(SpendingHistory.Forgone(b, set, today)?.Pieces ?? []))
            .Concat(SpendingHistory.Rises(set, today).Select(r => r.Difference));
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
                yield return new WatchView(OverviewCodes.LongCycleChargeSoon, [id], charge.Date, view([SpendingHistory.Piece(charge)]));
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
}
