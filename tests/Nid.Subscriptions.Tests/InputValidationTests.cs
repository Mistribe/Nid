using NodaTime;
using Nid.Core.Contracts;
using Nid.Subscriptions.Contracts;
using Nid.Subscriptions.Domain;
using Duration = Nid.Core.Contracts.Duration;   // NodaTime has a Duration too

namespace Nid.Subscriptions.Tests;

public class InputValidationTests
{
    private static readonly ItemId Sub = ItemId.New();

    private static Money Eur(decimal amount) => Money.Create(amount, Currency.Eur).Ok();

    private static Duration Months(int count) => Duration.Create(count, DurationUnit.Month).Ok();

    private static BillingPhase Phase(LocalDate startsOn, LocalDate? endsOn)
        => new(BillingPhaseId.New(), startsOn, endsOn, Eur(10m), Months(1), null, null,
               new Payer(PayerType.Household, null));

    private static Purchase Purchase(PurchaseKind kind, decimal price, decimal? allocated, params Grant[] grants)
        => new(PurchaseId.New(), new LocalDate(2025, 1, 1), "label", Eur(price),
               new Payer(PayerType.Household, null), kind, grants, allocated);

    private static SubscriptionInput Input(IReadOnlyList<BillingPhase> phases, IReadOnlyList<Purchase> purchases)
        => new(new SubscriptionHeader(Sub, AutoRenew: true, CancelledAt: null), phases, purchases);

    private static Grant PaidMonths(int count) => new(Sub, Months(count), CoverageSource.Paid, null);

    [Fact]
    public void Overlapping_phases_are_refused()
    {
        var input = Input(
            [Phase(new LocalDate(2025, 1, 1), new LocalDate(2025, 6, 1)),
             Phase(new LocalDate(2025, 3, 1), null)],
            []);

        Assert.Equal("subscriptions.phase_overlap", InputValidation.Validate(input).FailureCode());
    }

    [Fact]
    public void Adjacent_phases_are_accepted()
    {
        var input = Input(
            [Phase(new LocalDate(2025, 1, 1), new LocalDate(2025, 6, 1)),
             Phase(new LocalDate(2025, 6, 1), null)],
            []);

        Assert.True(InputValidation.Validate(input).IsSuccess);
    }

    [Fact]
    public void A_phase_ending_before_it_starts_is_refused()
    {
        var input = Input([Phase(new LocalDate(2025, 6, 1), new LocalDate(2025, 1, 1))], []);

        Assert.Equal("subscriptions.phase_range", InputValidation.Validate(input).FailureCode());
    }

    [Fact]
    public void An_allocated_amount_outside_the_price_is_refused()
    {
        var input = Input([], [Purchase(PurchaseKind.Bundle, 50m, 60m, PaidMonths(1))]);

        Assert.Equal("subscriptions.allocated_range", InputValidation.Validate(input).FailureCode());
    }

    [Fact]
    public void An_allocated_amount_on_something_that_is_not_a_bundle_is_refused()
    {
        var input = Input([], [Purchase(PurchaseKind.PrepaidTerm, 50m, 10m, PaidMonths(1))]);

        Assert.Equal("subscriptions.allocated_not_bundle", InputValidation.Validate(input).FailureCode());
    }

    [Theory]
    [InlineData(PurchaseKind.PrepaidTerm)]
    [InlineData(PurchaseKind.Bundle)]
    [InlineData(PurchaseKind.Gift)]
    public void A_negative_price_is_refused_outside_refunds_and_adjustments(PurchaseKind kind)
    {
        var input = Input([], [Purchase(kind, -5m, null)]);

        Assert.Equal("subscriptions.price_negative", InputValidation.Validate(input).FailureCode());
    }

    [Theory]
    [InlineData(PurchaseKind.Refund)]
    [InlineData(PurchaseKind.Adjustment)]
    public void A_negative_price_is_accepted_on_refunds_and_adjustments(PurchaseKind kind)
    {
        var input = Input([], [Purchase(kind, -5m, null)]);

        Assert.True(InputValidation.Validate(input).IsSuccess);
    }

    [Fact]
    public void A_grant_for_another_subscription_is_refused()
    {
        var foreign = new Grant(ItemId.New(), Months(1), CoverageSource.Paid, null);
        var input = Input([], [Purchase(PurchaseKind.PrepaidTerm, 10m, null, foreign)]);

        Assert.Equal("subscriptions.grant_foreign", InputValidation.Validate(input).FailureCode());
    }

    [Fact]
    public void A_zero_length_duration_is_refused()
    {
        var empty = new Grant(Sub, default, CoverageSource.Paid, null);
        var input = Input([], [Purchase(PurchaseKind.PrepaidTerm, 10m, null, empty)]);

        Assert.Equal("subscriptions.duration_count", InputValidation.Validate(input).FailureCode());
    }

    [Fact]
    public void A_subscription_with_nothing_in_it_warns()
    {
        var warnings = InputValidation.Validate(Input([], [])).Ok();

        Assert.Equal("subscriptions.incomplete", Assert.Single(warnings).Code);
    }

    [Fact]
    public void A_usable_subscription_warns_about_nothing()
    {
        var input = Input([Phase(new LocalDate(2025, 1, 1), null)], []);

        Assert.Empty(InputValidation.Validate(input).Ok());
    }
}
