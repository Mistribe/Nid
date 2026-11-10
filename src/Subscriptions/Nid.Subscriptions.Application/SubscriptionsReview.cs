using Nid.Core.Contracts;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Subscriptions.Application;

/// <summary>Issue #12: the yearly review's subscriptions, every visible one archived included, each charge at the rate of its date.</summary>
public sealed class SubscriptionsReview(SubscriptionSet set, IHouseholdCalendar calendar, IFxRates fx, IHouseholdSettings household) : ISubscriptionsReview
{
    public async Task<Result<SubscriptionsReviewPart>> ReviewAsync(
        PersonId reader, VisibilityScope scope, LocalDate start, LocalDate end, CancellationToken ct)
    {
        var today = await calendar.TodayAsync(ct);
        var built = await set.LoadAsync(reader, scope, today, Horizons.Resolve(today, null), ct, includeArchived: true);
        var pieces = ReviewMath.Cost(built, start, end);
        var reference = await household.ReferenceCurrencyAsync(ct);
        var rates = await Conversion.LoadAsync(fx, pieces, reference, today, ct);
        // One pass, as FxConversion.Convert: no cost as soon as a charge has no rate, its currency named.
        var total = 0m;
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var p in pieces)
            if (rates.TryConvert(p.Amount, p.Currency, reference, p.ChargeDate, p.IsProjected, out var converted, out _))
                total += converted;
            else
                missing.Add(p.Currency.Code);
        return Result.Success(new SubscriptionsReviewPart(
            missing.Count > 0 ? null : decimal.Round(total, Money.MaxDecimals),
            ReviewMath.Started(built, start, end), ReviewMath.Ended(built, start, end, today), ReviewMath.PriceChanges(built, start, end),
            missing.ToList()));
    }

    public async Task<LocalDate?> FirstChargeAsync(PersonId reader, VisibilityScope scope, CancellationToken ct)
    {
        var today = await calendar.TodayAsync(ct);
        var built = await set.LoadAsync(reader, scope, today, Horizons.Resolve(today, null), ct, includeArchived: true);
        return built.SelectMany(b => SpendingHistory.Past(b.Timeline)).Select(c => (LocalDate?)c.Date).Min();
    }
}
