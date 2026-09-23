using Nid.Core.Contracts;
using Nid.Subscriptions.Contracts;
using Nid.Subscriptions.Domain;
using NodaTime;

namespace Nid.Subscriptions.Application;

/// <summary>The cost window a read reports over; defaults to the current calendar month.</summary>
public static class CostWindow
{
    public static DateRange Resolve(LocalDate today, LocalDate? from, LocalDate? to)
    {
        var start = from ?? new LocalDate(today.Year, today.Month, 1);
        var end = to ?? start.PlusMonths(1);
        return DateRange.Create(start, end).TryGetValue(out var range)
            ? range
            : DateRange.Create(start, start.PlusDays(1)).TryGetValue(out var fallback) ? fallback : default;
    }
}

public static class SubscriptionViews
{
    public static SubscriptionSummary Summarise(
        SubscriptionInput input, Timeline timeline, string displayName, DateRange window)
        => new(
            input.Subscription.Id,
            displayName,
            input.Subscription.AutoRenew,
            timeline.ExpiresOn(),
            timeline.NextCharge()?.Date,
            timeline.NextCharge() is { } next ? MoneyView.From(next.Price) : null,
            MoneyView.From(timeline.CashCost(window)),
            MoneyView.From(timeline.AmortizedCost(window)),
            timeline.FreeDays(window),
            timeline.Warnings.Select(FindingView.From).ToList());

    public static SubscriptionDetail Detail(
        SubscriptionInput input, Timeline timeline, string displayName, DateRange window)
        => new(
            input.Subscription.Id,
            displayName,
            input.Subscription.AutoRenew,
            input.Subscription.CancelledAt,
            timeline.ExpiresOn(),
            timeline.NextCharge()?.Date,
            timeline.NextCharge() is { } next ? MoneyView.From(next.Price) : null,
            MoneyView.From(timeline.CashCost(window)),
            MoneyView.From(timeline.AmortizedCost(window)),
            timeline.FreeDays(window),
            timeline.Coverage.Select(c => new CoverageView(
                c.Start, c.EndBefore, c.Source.ToString().ToLowerInvariant(), c.IsProjected)).ToList(),
            timeline.Charges.Select(c => new ChargeView(
                c.Date, MoneyView.From(c.Price), c.IsProjected,
                c.Origin is ChargeOrigin.FromPurchase p ? p.PurchaseId.Value : null)).ToList(),
            timeline.Warnings.Select(FindingView.From).ToList());
}

/// <summary>
/// Household totals across several subscriptions. A purchase granting to more than one of them left the
/// account once, so it is counted once — the decision recorded in the 1C1 design §6.
/// </summary>
public static class HouseholdCost
{
    public static (IReadOnlyList<MoneyView> Cash, IReadOnlyList<MoneyView> Amortized, IReadOnlyList<Guid> Shared)
        Combine(IReadOnlyList<Timeline> timelines, DateRange window)
    {
        var cash = new Dictionary<string, decimal>();
        var amortized = new Dictionary<string, decimal>();
        var seenPurchases = new HashSet<Guid>();
        var sharedPurchases = new HashSet<Guid>();

        foreach (var timeline in timelines)
        {
            foreach (var charge in timeline.Charges)
            {
                if (charge.Origin is ChargeOrigin.FromPurchase purchase)
                {
                    if (!seenPurchases.Add(purchase.PurchaseId.Value))
                    {
                        sharedPurchases.Add(purchase.PurchaseId.Value);
                        continue; // the money left once
                    }
                }

                if (window.Contains(charge.Date))
                    Add(cash, charge.Price.Currency.Code, charge.Price.Amount);
            }

            foreach (var total in timeline.AmortizedCost(window).Totals)
                Add(amortized, total.Currency.Code, total.Amount);
        }

        return (ToViews(cash), ToViews(amortized), sharedPurchases.OrderBy(g => g).ToList());
    }

    /// <summary>
    /// The stable code for the divergence recorded in the 1C1 design §6: a purchase granting to several
    /// subscriptions leaves the account once, so the household CASH total counts it once — but the engine
    /// amortizes per timeline, and the same purchase's days count fully on each subscription, so the
    /// household AMORTIZED total counts it as many times as it granted. Splitting a shared purchase's
    /// amortized cost per grant would change the data model, the 1B contracts and the engine, and is out of
    /// scope here; until then the divergence travels on the channel the spec already established for
    /// anything a human should look at, rather than as tribal knowledge in a test comment.
    /// </summary>
    public const string AmortizedDoubleCountCode = "subscriptions.amortized_double_count";

    /// <summary>One finding per purchase that contributed to more than one of the subscriptions combined.</summary>
    public static IReadOnlyList<Finding> Divergences(IReadOnlyList<Guid> sharedPurchases)
        => sharedPurchases.Select(id => new Finding(
            AmortizedDoubleCountCode,
            "This purchase granted to several subscriptions: the household cash total counts it once, the "
            + "amortized total counts it on each of them, so the two diverge.",
            id.ToString())).ToList();

    /// <summary>The household cash total before it is summed: a purchase granting to several subscriptions counts once, as in <see cref="Combine"/>.</summary>
    public static IReadOnlyList<CostPiece> CashPieces(IReadOnlyList<Timeline> timelines, DateRange window)
    {
        var pieces = new List<CostPiece>();
        var seenPurchases = new HashSet<Guid>();
        foreach (var timeline in timelines)
        foreach (var charge in timeline.Charges)
        {
            if (charge.Origin is ChargeOrigin.FromPurchase purchase && !seenPurchases.Add(purchase.PurchaseId.Value))
                continue;
            if (window.Contains(charge.Date))
                pieces.Add(new CostPiece(charge.Date, charge.Price.Currency, charge.Price.Amount, charge.IsProjected));
        }

        return pieces;
    }

    /// <summary>The household amortized total before it is summed. Shares the 1C1 limitation of <see cref="Combine"/>: a shared purchase counts on each subscription.</summary>
    public static IReadOnlyList<CostPiece> AmortizedPieces(IReadOnlyList<Timeline> timelines, DateRange window)
        => timelines.SelectMany(t => t.AmortizedCostPieces(window)).ToList();

    private static void Add(Dictionary<string, decimal> totals, string currency, decimal amount)
        => totals[currency] = totals.GetValueOrDefault(currency) + amount;

    private static IReadOnlyList<MoneyView> ToViews(Dictionary<string, decimal> totals)
        => totals.OrderBy(t => t.Key, StringComparer.Ordinal).Select(t => new MoneyView(t.Value, t.Key)).ToList();
}
