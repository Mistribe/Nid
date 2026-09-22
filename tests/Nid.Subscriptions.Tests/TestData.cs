using NodaTime;
using Nid.Core.Contracts;
using Nid.Subscriptions.Contracts;
using Duration = Nid.Core.Contracts.Duration;   // NodaTime has a Duration too

namespace Nid.Subscriptions.Tests;

/// <summary>Builders shared by the engine tests. Ids are stable so origins can be asserted.</summary>
internal static class TestData
{
    public static readonly ItemId Sub = ItemId.New();

    public static Money Eur(decimal amount) => Money.Create(amount, Currency.Eur).Ok();

    public static Duration Months(int count) => Duration.Create(count, DurationUnit.Month).Ok();

    public static Duration Days(int count) => Duration.Create(count, DurationUnit.Day).Ok();

    public static Payer Household => new(PayerType.Household, null);

    public static BillingPhase Phase(
        LocalDate startsOn,
        LocalDate? endsOn,
        decimal price,
        Duration? every = null,
        Duration? covers = null,
        int? maxCycles = null,
        BillingPhaseId? id = null)
        => new(id ?? BillingPhaseId.New(), startsOn, endsOn, Eur(price), every ?? Months(1), covers,
               maxCycles, Household);

    public static Purchase Purchase(
        LocalDate date,
        decimal price,
        PurchaseKind kind,
        IReadOnlyList<Grant> grants,
        decimal? allocated = null,
        PurchaseId? id = null)
        => new(id ?? PurchaseId.New(), date, "test", Eur(price), Household, kind, grants, allocated);

    public static Grant Grant(Duration duration, CoverageSource source, LocalDate? startsOn = null)
        => new(Sub, duration, source, startsOn);

    public static SubscriptionInput Input(
        IReadOnlyList<BillingPhase> phases,
        IReadOnlyList<Purchase> purchases,
        bool autoRenew = true,
        LocalDate? cancelledAt = null)
        => new(new SubscriptionHeader(Sub, autoRenew, cancelledAt), phases, purchases);
}
