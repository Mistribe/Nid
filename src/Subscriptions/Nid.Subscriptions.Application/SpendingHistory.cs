using Nid.Core.Contracts;
using Nid.Subscriptions.Application.Catalog;
using Nid.Subscriptions.Contracts;
using Nid.Subscriptions.Domain;
using NodaTime;

namespace Nid.Subscriptions.Application;

/// <summary>
/// What a subscription has cost since its first charge (#395), in the reference currency, each charge at the rate of its
/// date. <paramref name="Partial"/>: a rate was missing, the totals hold the charges that converted and the findings name the rest.
/// </summary>
public sealed record SpentView(LocalDate Since, MoneyView? Total, MoneyView? Last12Months, bool Partial);

/// <summary>
/// #397: what an ended subscription without successor would have cost since its end, up to today: its last price at its last
/// cycle (<paramref name="LastPrice"/> every <paramref name="Every"/>), converted at each forgone charge's date.
/// </summary>
public sealed record SavedView(LocalDate Since, MoneyView? Total, MoneyView LastPrice, DurationView Every, bool Partial);

/// <summary>
/// #396: one calendar year of charges. The current year runs to December: <paramref name="Upcoming"/> is what is still to
/// come, zero for a past year. <paramref name="ByLabel"/> splits <paramref name="Paid"/> by main label, every label of the
/// chart in every year, in the same order.
/// </summary>
public sealed record YearView(int Year, MoneyView? Paid, MoneyView? Upcoming, MoneyView? Total, IReadOnlyList<YearLabelView> ByLabel);

/// <summary><paramref name="Name"/> and <paramref name="Color"/> are null for <see cref="ListGrouping.NoneKey"/>.</summary>
public sealed record YearLabelView(string Key, string? Name, string? Color, MoneyView? Paid);

/// <summary>#395: a provider and what its subscriptions, ended ones included, have cost since their first charge.</summary>
public sealed record TopProviderView(ProviderSummaryView Provider, MoneyView? Spent, bool Partial);

/// <summary>#397: what the ended subscriptions without successor would have cost over the last 12 months, and each of them.</summary>
public sealed record SavingsView(MoneyView? Last12Months, bool Partial, IReadOnlyList<SavedItemView> Items);

public sealed record SavedItemView(ItemId ItemId, string DisplayName, ProviderSummaryView? Provider, LocalDate EndedOn, MoneyView? Last12Months);

/// <summary>
/// #398: a phase change in the last 12 months to a dearer year. <paramref name="PromoEnded"/>: the old phase was a launch offer
/// (it had a cycle limit), so the change is the offer ending, not an increase, and stays out of <see cref="PriceRisesView.YearlyTotal"/>.
/// <paramref name="ChangePct"/> is the yearly cost change in percent (one decimal), <paramref name="Real"/> the same after inflation (#419).
/// </summary>
public sealed record PriceRiseView(
    ItemId ItemId, string DisplayName, ProviderSummaryView? Provider, LocalDate On, MoneyView OldPrice, DurationView OldEvery,
    MoneyView NewPrice, DurationView NewEvery, MoneyView? YearlyDifference, bool PromoEnded,
    decimal? ChangePct = null, RealPercentView? Real = null);

/// <summary>#398: the increases' yearly differences summed (promotions ending left out), and every change listed, latest first.</summary>
public sealed record PriceRisesView(MoneyView? YearlyTotal, IReadOnlyList<PriceRiseView> Items);

/// <summary>
/// The past charges of the timelines, and what they add up to (#395 to #398): one walk over the engine's charges, each turned
/// into a piece converted at its own date. Spent totals read a subscription's charges, the years the household's (a purchase
/// granting to several subscriptions once), the savings the charges an ended subscription no longer makes.
/// </summary>
public static class SpendingHistory
{
    public static CostPiece Piece(Charge charge) => new(charge.Date, charge.Price.Currency, charge.Price.Amount, charge.IsProjected);

    /// <summary>The charges made by today, the first one first.</summary>
    public static IEnumerable<Charge> Past(Timeline timeline) => timeline.Charges.Where(c => !c.IsProjected);

    /// <summary>Since the first charge and over the last 12 months (after today − 12 months); null without any charge yet.</summary>
    public static (SpentView? View, IReadOnlyList<Finding> Missing) Spent(Timeline timeline, Currency reference, RateTable rates, LocalDate today)
    {
        var past = Past(timeline).ToList();
        if (past.Count == 0)
            return (null, []);

        var total = FxConversion.Convert(past.Select(Piece), rates, reference, partial: true);
        var last12 = FxConversion.Convert(past.Where(c => c.Date > today.PlusMonths(-12)).Select(Piece), rates, reference, partial: true);
        return (new SpentView(past.Min(c => c.Date), View(total), View(last12), total.Missing.Count > 0), total.Missing);
    }

    /// <summary>
    /// What a group of subscriptions spent, for <see cref="ListGrouping.Group"/>: each charge once, so a purchase granting to
    /// several of them counts once, as in the years (D5). Null without any charge yet.
    /// </summary>
    public static Func<IReadOnlyList<ItemId>, (MoneyView? Total, bool Partial)> SpentOf(
        IReadOnlyList<BuiltSubscription> set, Currency reference, RateTable rates) => ids =>
    {
        var past = ChargeCycles.HouseholdCharges(set.Where(b => ids.Contains(b.Input.Subscription.Id)))
            .Where(x => !x.Charge.IsProjected).Select(x => Piece(x.Charge)).ToList();
        if (past.Count == 0)
            return (null, false);
        var total = FxConversion.Convert(past, rates, reference, partial: true);
        return (View(total), total.Missing.Count > 0);
    };

    /// <summary>
    /// The charges an ended subscription would have made since its end (the first day it was no longer covered), at the
    /// price and cycle of its last phase charge, up to today. None for one still running, one replaced by another subscription
    /// of the same provider (covered after its end), one whose last charge was free, or one paid by purchases only.
    /// </summary>
    public static (LocalDate End, Charge Last, BillingPhase Phase, IReadOnlyList<CostPiece> Pieces)? Forgone(
        BuiltSubscription sub, IEnumerable<BuiltSubscription> others, LocalDate today)
    {
        if (sub.Timeline.ExpiresOn() is not { } end || end >= today)
            return null;
        if (sub.Timeline.Charges.LastOrDefault(c => c.Origin is ChargeOrigin.FromPhase && !c.IsProjected) is not { } last
            || last.Price.Amount <= 0m || ChargeCycles.PhaseOf(sub.Input, last) is not { } phase)
            return null;
        if (Replaced(sub, others, end))
            return null;

        var pieces = new List<CostPiece>();
        for (var on = end; on <= today; on = phase.Every.AddTo(end, pieces.Count))
            pieces.Add(new CostPiece(on, last.Price.Currency, last.Price.Amount, false));
        return (end, last, phase, pieces);
    }

    /// <summary>Another subscription of the same provider still covered after <paramref name="end"/>: the ended one has a successor.</summary>
    public static bool Replaced(BuiltSubscription sub, IEnumerable<BuiltSubscription> others, LocalDate end)
        => sub.Provider is { } provider && others.Any(o => o.Input.Subscription.Id != sub.Input.Subscription.Id
            && o.Provider?.Key == provider.Key && o.Timeline.ExpiresOn() is { } until && until > end);

    public static SavedView? Saved(BuiltSubscription sub, IEnumerable<BuiltSubscription> others, Currency reference, RateTable rates, LocalDate today)
    {
        if (Forgone(sub, others, today) is not { } forgone)
            return null;
        var total = FxConversion.Convert(forgone.Pieces, rates, reference, partial: true);
        return new SavedView(forgone.End, View(total), MoneyView.From(forgone.Last.Price), DurationView.From(forgone.Phase.Every), total.Missing.Count > 0);
    }

    /// <summary>Every phase change of the set in <c>(today − 12 months, today]</c> to a phase that costs more a year, in one currency.</summary>
    public static IEnumerable<(BuiltSubscription Sub, BillingPhase From, BillingPhase To, CostPiece Difference)> Rises(
        IEnumerable<BuiltSubscription> set, LocalDate today)
    {
        foreach (var sub in set)
        {
            var phases = sub.Input.Phases.OrderBy(p => p.StartsOn).ToList();
            for (var i = 1; i < phases.Count; i++)
            {
                var (from, to) = (phases[i - 1], phases[i]);
                if (to.StartsOn <= today.PlusMonths(-12) || to.StartsOn > today || to.Price.Currency != from.Price.Currency)
                    continue;
                var difference = Yearly(to, to.StartsOn) - Yearly(from, to.StartsOn);
                if (difference > 0m)
                    yield return (sub, from, to, new CostPiece(to.StartsOn, to.Price.Currency, difference, false));
            }
        }
    }

    /// <summary>A phase's price times the charges it makes in the year from <paramref name="on"/>: 12 monthly, 52 or 53 weekly.</summary>
    public static decimal Yearly(BillingPhase phase, LocalDate on)
    {
        var cycles = 0;
        while (phase.Every.AddTo(on, cycles) < on.PlusYears(1))
            cycles++;
        return phase.Price.Amount * cycles;
    }

    /// <summary>One bar per calendar year from the first charge to this one, which runs to December (charges past today projected).</summary>
    public static IReadOnlyList<YearView> Years(
        IReadOnlyList<BuiltSubscription> set, LocalDate today, Func<IEnumerable<CostPiece>, MoneyView?> view)
    {
        var charges = ChargeCycles.HouseholdCharges(set).Where(x => x.Charge.Date.Year <= today.Year).ToList();
        if (charges.Count == 0)
            return [];

        static (string Key, string? Name, string? Color) LabelOf(BuiltSubscription b)
            => b.Provider?.Labels.FirstOrDefault() is { } l ? (l.Key, l.Name, l.Color) : (ListGrouping.NoneKey, null, null);
        var labels = charges.Select(x => LabelOf(x.Sub)).DistinctBy(l => l.Key)
            .OrderBy(l => l.Key == ListGrouping.NoneKey).ThenBy(l => l.Name, StringComparer.Ordinal).ToList();

        var first = charges.Min(x => x.Charge.Date.Year);
        return Enumerable.Range(first, today.Year - first + 1).Select(year =>
        {
            var inYear = charges.Where(x => x.Charge.Date.Year == year).ToList();
            var paid = inYear.Where(x => !x.Charge.IsProjected).ToList();
            return new YearView(year,
                view(paid.Select(x => Piece(x.Charge))),
                view(inYear.Where(x => x.Charge.IsProjected).Select(x => Piece(x.Charge))),
                view(inYear.Select(x => Piece(x.Charge))),
                labels.Select(l => new YearLabelView(l.Key, l.Name, l.Color,
                    view(paid.Where(x => LabelOf(x.Sub).Key == l.Key).Select(x => Piece(x.Charge))))).ToList());
        }).ToList();
    }

    private static MoneyView? View(ConvertedTotal total) => total.Total is { } m ? MoneyView.From(m) : null;
}
