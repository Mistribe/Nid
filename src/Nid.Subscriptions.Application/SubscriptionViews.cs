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

    private static void Add(Dictionary<string, decimal> totals, string currency, decimal amount)
        => totals[currency] = totals.GetValueOrDefault(currency) + amount;

    private static IReadOnlyList<MoneyView> ToViews(Dictionary<string, decimal> totals)
        => totals.OrderBy(t => t.Key, StringComparer.Ordinal).Select(t => new MoneyView(t.Value, t.Key)).ToList();
}
