using NodaTime;
using Nid.Core.Contracts;
using Nid.Subscriptions.Contracts;
using Nid.Subscriptions.Domain;
using static Nid.Subscriptions.Tests.TestData;
using Duration = Nid.Core.Contracts.Duration;   // NodaTime has a Duration too

namespace Nid.Subscriptions.Tests;

public class TimelineWalkTests
{
    private static readonly LocalDate Horizon = new(2027, 1, 1);

    [Fact]
    public void A_monthly_phase_charges_once_per_month_from_its_start()
    {
        var input = Input([Phase(new LocalDate(2025, 1, 1), null, 10m)], []);

        var timeline = SubscriptionTimeline.Build(input, new LocalDate(2025, 4, 15), new LocalDate(2025, 5, 1)).Ok();

        Assert.Equal(
            [new LocalDate(2025, 1, 1), new LocalDate(2025, 2, 1), new LocalDate(2025, 3, 1), new LocalDate(2025, 4, 1)],
            timeline.Charges.Select(c => c.Date));
    }

    [Fact]
    public void An_end_of_month_anchor_clamps_and_then_recovers()
    {
        var input = Input([Phase(new LocalDate(2025, 1, 31), null, 10m)], []);

        var timeline = SubscriptionTimeline.Build(input, new LocalDate(2025, 1, 1), new LocalDate(2025, 5, 1)).Ok();

        Assert.Equal(
            [new LocalDate(2025, 1, 31), new LocalDate(2025, 2, 28), new LocalDate(2025, 3, 31), new LocalDate(2025, 4, 30)],
            timeline.Charges.Select(c => c.Date));
    }

    [Fact]
    public void A_yearly_phase_crosses_a_leap_day()
    {
        var input = Input([Phase(new LocalDate(2024, 2, 29), null, 100m, every: Duration.Create(1, DurationUnit.Year).Ok())], []);

        var timeline = SubscriptionTimeline.Build(input, new LocalDate(2024, 1, 1), new LocalDate(2026, 6, 1)).Ok();

        Assert.Equal(
            [new LocalDate(2024, 2, 29), new LocalDate(2025, 2, 28), new LocalDate(2026, 2, 28)],
            timeline.Charges.Select(c => c.Date));
    }

    [Fact]
    public void Coverage_periods_never_overlap()
    {
        var input = Input(
            [Phase(new LocalDate(2025, 1, 1), null, 10m)],
            [Purchase(new LocalDate(2025, 2, 10), 0m, PurchaseKind.Gift, [Grant(Months(3), CoverageSource.Gift)])]);

        var timeline = SubscriptionTimeline.Build(input, new LocalDate(2025, 1, 1), Horizon).Ok();

        var ordered = timeline.Coverage.OrderBy(p => p.Start).ToList();
        for (var i = 1; i < ordered.Count; i++)
            Assert.True(ordered[i].Start >= ordered[i - 1].EndBefore,
                        $"{ordered[i - 1].Start}..{ordered[i - 1].EndBefore} overlaps {ordered[i].Start}");
    }

    [Fact]
    public void A_stacked_grant_starts_exactly_where_the_running_coverage_ended()
    {
        var input = Input(
            [Phase(new LocalDate(2025, 1, 1), null, 10m)],
            [Purchase(new LocalDate(2025, 2, 10), 0m, PurchaseKind.Gift, [Grant(Months(3), CoverageSource.Gift)])]);

        var timeline = SubscriptionTimeline.Build(input, new LocalDate(2025, 1, 1), Horizon).Ok();

        var gift = Assert.Single(timeline.Coverage, p => p.Source == CoverageSource.Gift);
        Assert.Equal(new LocalDate(2025, 3, 1), gift.Start);
        Assert.Equal(new LocalDate(2025, 6, 1), gift.EndBefore);
    }

    [Fact]
    public void A_gift_pushes_the_next_charge_to_the_end_of_the_stacked_coverage()
    {
        var input = Input(
            [Phase(new LocalDate(2025, 1, 1), null, 10m)],
            [Purchase(new LocalDate(2025, 2, 10), 0m, PurchaseKind.Gift, [Grant(Months(3), CoverageSource.Gift)])]);

        var timeline = SubscriptionTimeline.Build(input, new LocalDate(2025, 1, 1), Horizon).Ok();

        var afterGift = timeline.Charges.Where(c => c.Date > new LocalDate(2025, 2, 10)).Select(c => c.Date).ToList();
        Assert.Equal(new LocalDate(2025, 6, 1), afterGift[0]);
    }

    [Fact]
    public void A_max_cycles_limit_survives_a_stacked_grant()
    {
        var input = Input(
            [Phase(new LocalDate(2025, 1, 1), null, 10m, maxCycles: 2)],
            [Purchase(new LocalDate(2025, 1, 15), 0m, PurchaseKind.Gift, [Grant(Months(3), CoverageSource.Gift)])]);

        var timeline = SubscriptionTimeline.Build(input, new LocalDate(2025, 1, 1), Horizon).Ok();

        var phaseCharges = timeline.Charges.Where(c => c.Origin is ChargeOrigin.FromPhase).ToList();
        Assert.Equal(2, phaseCharges.Count);
    }

    [Fact]
    public void A_grant_with_an_explicit_start_lands_there()
    {
        var input = Input(
            [],
            [Purchase(new LocalDate(2025, 6, 22), 179.86m, PurchaseKind.PrepaidTerm,
                      [Grant(Months(24), CoverageSource.Paid, new LocalDate(2025, 6, 22)),
                       Grant(Months(6), CoverageSource.Bonus)])]);

        var timeline = SubscriptionTimeline.Build(input, new LocalDate(2025, 7, 1), new LocalDate(2030, 1, 1)).Ok();

        var paid = Assert.Single(timeline.Coverage, p => p.Source == CoverageSource.Paid);
        var bonus = Assert.Single(timeline.Coverage, p => p.Source == CoverageSource.Bonus);
        Assert.Equal(new LocalDate(2025, 6, 22), paid.Start);
        Assert.Equal(new LocalDate(2027, 6, 22), paid.EndBefore);
        Assert.Equal(new LocalDate(2027, 6, 22), bonus.Start);
        Assert.Equal(new LocalDate(2027, 12, 22), bonus.EndBefore);
    }

    [Fact]
    public void A_max_cycles_limit_stops_the_phase()
    {
        var input = Input([Phase(new LocalDate(2025, 1, 1), null, 0.99m, maxCycles: 3)], []);

        var timeline = SubscriptionTimeline.Build(input, new LocalDate(2025, 1, 1), Horizon).Ok();

        Assert.Equal(3, timeline.Charges.Count);
    }

    [Fact]
    public void A_runaway_phase_stops_at_the_cycle_cap()
    {
        var input = Input([Phase(new LocalDate(2025, 1, 1), null, 10m, every: Days(1))], []);

        var timeline = SubscriptionTimeline.Build(input, new LocalDate(2025, 1, 1), new LocalDate(2030, 1, 1)).Ok();

        Assert.Equal(1200, SubscriptionTimeline.MaxCyclesPerPhase);
        Assert.Equal(1200, timeline.Charges.Count);
        Assert.Contains(timeline.Warnings, w => w.Code == "subscriptions.cycle_cap");
    }

    [Fact]
    public void A_cancelled_subscription_stops_charging()
    {
        var input = Input([Phase(new LocalDate(2025, 1, 1), null, 10m)], [],
                          cancelledAt: new LocalDate(2025, 3, 15));

        var timeline = SubscriptionTimeline.Build(input, new LocalDate(2025, 1, 1), Horizon).Ok();

        Assert.All(timeline.Charges, c => Assert.True(c.Date < new LocalDate(2025, 3, 15)));
    }

    [Fact]
    public void A_subscription_that_does_not_renew_keeps_its_history_and_stops_at_today()
    {
        var input = Input([Phase(new LocalDate(2024, 1, 1), null, 10m)], [], autoRenew: false);
        var today = new LocalDate(2026, 1, 1);

        var timeline = SubscriptionTimeline.Build(input, today, Horizon).Ok();

        // Two years of real payments, from 2024-01-01 to 2025-12-01, and nothing on or after today.
        Assert.Equal(24, timeline.Charges.Count);
        Assert.All(timeline.Charges, c => Assert.True(c.Date < today));
        Assert.True(timeline.IsCoveredOn(new LocalDate(2025, 12, 15)));
        Assert.Equal(today, timeline.ExpiresOn());
        Assert.Null(timeline.NextCharge());
    }

    [Fact]
    public void Charges_after_today_are_projected_and_charges_before_it_are_not()
    {
        var input = Input([Phase(new LocalDate(2025, 1, 1), null, 10m)], []);

        var timeline = SubscriptionTimeline.Build(input, new LocalDate(2025, 3, 15), new LocalDate(2025, 6, 1)).Ok();

        Assert.All(timeline.Charges.Where(c => c.Date <= new LocalDate(2025, 3, 15)), c => Assert.False(c.IsProjected));
        Assert.All(timeline.Charges.Where(c => c.Date > new LocalDate(2025, 3, 15)), c => Assert.True(c.IsProjected));
    }

    [Fact]
    public void A_coverage_period_straddling_today_is_not_a_projection()
    {
        var input = Input([Phase(new LocalDate(2025, 1, 1), null, 10m)], []);

        var timeline = SubscriptionTimeline.Build(input, new LocalDate(2025, 3, 15), new LocalDate(2025, 6, 1)).Ok();

        var straddling = Assert.Single(timeline.Coverage, p => p.Contains(new LocalDate(2025, 3, 15)));
        Assert.False(straddling.IsProjected);
    }

    [Fact]
    public void A_gap_before_a_later_phase_is_left_uncovered()
    {
        var input = Input(
            [Phase(new LocalDate(2025, 1, 1), new LocalDate(2025, 2, 1), 10m, maxCycles: 1),
             Phase(new LocalDate(2025, 5, 1), null, 12m)],
            []);

        var timeline = SubscriptionTimeline.Build(input, new LocalDate(2025, 1, 1), new LocalDate(2025, 7, 1)).Ok();

        Assert.Contains(timeline.Charges, c => c.Date == new LocalDate(2025, 5, 1));
        Assert.DoesNotContain(timeline.Coverage, p => p.Contains(new LocalDate(2025, 3, 1)));
    }

    [Fact]
    public void An_explicit_grant_overlapping_existing_coverage_warns_without_failing()
    {
        var input = Input(
            [Phase(new LocalDate(2025, 1, 1), null, 10m, maxCycles: 2)],
            [Purchase(new LocalDate(2025, 1, 10), 5m, PurchaseKind.PrepaidTerm,
                      [Grant(Months(1), CoverageSource.Paid, new LocalDate(2025, 1, 15))])]);

        var timeline = SubscriptionTimeline.Build(input, new LocalDate(2025, 6, 1), Horizon).Ok();

        Assert.Contains(timeline.Warnings, w => w.Code == "subscriptions.grant_overlap");
    }

    [Fact]
    public void The_second_pass_allocates_a_purchase_charge_over_all_its_grants()
    {
        var input = Input(
            [],
            [Purchase(new LocalDate(2025, 6, 22), 179.86m, PurchaseKind.PrepaidTerm,
                      [Grant(Months(24), CoverageSource.Paid, new LocalDate(2025, 6, 22)),
                       Grant(Months(6), CoverageSource.Bonus)])]);

        var timeline = SubscriptionTimeline.Build(input, new LocalDate(2025, 7, 1), new LocalDate(2030, 1, 1)).Ok();

        var charge = Assert.Single(timeline.Charges, c => c.Origin is ChargeOrigin.FromPurchase);
        Assert.Equal(2, charge.Coverage.Count);
        Assert.Equal(CoverageSource.Paid, charge.Coverage[0].Source);
        Assert.Equal(CoverageSource.Bonus, charge.Coverage[1].Source);
        Assert.True(charge.Coverage[0].Start < charge.Coverage[1].Start);
        Assert.Equal(charge.Coverage[0].Days + charge.Coverage[1].Days, charge.Allocation.Days);
    }

    [Fact]
    public void A_refund_without_grants_is_a_charge_that_amortizes_nothing()
    {
        // The data model maps "refund or pro-rata credit" to a purchase with a negative price and no grants.
        var input = Input(
            [Phase(new LocalDate(2025, 1, 1), null, 10m)],
            [Purchase(new LocalDate(2025, 2, 10), -10m, PurchaseKind.Refund, [])]);

        var timeline = SubscriptionTimeline.Build(input, new LocalDate(2025, 6, 1), Horizon).Ok();

        var refund = Assert.Single(timeline.Charges, c => c.Origin is ChargeOrigin.FromPurchase);
        Assert.Equal(new LocalDate(2025, 2, 10), refund.Date);
        Assert.Equal(-10m, refund.Price.Amount);
        Assert.Empty(refund.Coverage);
        Assert.Equal(0, refund.Allocation.Days);

        var window = DateRange.Create(new LocalDate(2025, 1, 1), new LocalDate(2025, 4, 1)).Ok();

        // Three monthly charges less the refund; the refund covers no day, so it amortizes nothing.
        Assert.Equal(20m, timeline.CashCost(window).For(Currency.Eur)!.Value.Amount);
        Assert.Equal(30m, timeline.AmortizedCost(window).For(Currency.Eur)!.Value.Amount);
    }

    [Fact]
    public void A_grant_starting_in_the_future_does_not_freeze_the_phase_before_it()
    {
        // The legacy import turns one-time records into prepaid terms with an explicit range, so a grant
        // landing months ahead is ordinary input. The phase must keep billing the time it leaves uncovered.
        var input = Input(
            [Phase(new LocalDate(2025, 1, 1), null, 10m)],
            [Purchase(new LocalDate(2025, 1, 5), 10m, PurchaseKind.PrepaidTerm,
                      [Grant(Months(1), CoverageSource.Paid, new LocalDate(2025, 6, 1))])]);

        var timeline = SubscriptionTimeline.Build(input, new LocalDate(2025, 12, 1), Horizon).Ok();

        Assert.True(timeline.IsCoveredOn(new LocalDate(2025, 3, 1)));

        var window = DateRange.Create(new LocalDate(2025, 1, 1), new LocalDate(2025, 6, 1)).Ok();
        Assert.Equal(60m, timeline.CashCost(window).For(Currency.Eur)!.Value.Amount);

        // Five monthly charges cover January to June; the grant covers June, so the phase charges again
        // only when that month runs out.
        var phaseCharges = timeline.Charges
                                   .Where(c => c.Origin is ChargeOrigin.FromPhase)
                                   .Select(c => c.Date)
                                   .Take(6)
                                   .ToList();
        Assert.Equal(
            [new LocalDate(2025, 1, 1), new LocalDate(2025, 2, 1), new LocalDate(2025, 3, 1),
             new LocalDate(2025, 4, 1), new LocalDate(2025, 5, 1), new LocalDate(2025, 7, 1)],
            phaseCharges);
    }

    [Fact]
    public void A_bundle_amortizes_its_allocated_amount_and_not_its_price()
    {
        var input = Input(
            [],
            [Purchase(new LocalDate(2024, 11, 18), 88.99m, PurchaseKind.Bundle,
                      [Grant(Months(1), CoverageSource.Included)], allocated: 29.6633m)]);

        var timeline = SubscriptionTimeline.Build(input, new LocalDate(2025, 6, 1), Horizon).Ok();

        var window = DateRange.Create(new LocalDate(2024, 11, 1), new LocalDate(2025, 1, 1)).Ok();
        Assert.Equal(88.99m, timeline.CashCost(window).For(Currency.Eur)!.Value.Amount);
        Assert.Equal(29.6633m, timeline.AmortizedCost(window).For(Currency.Eur)!.Value.Amount);
    }

    [Fact]
    public void An_allocated_amount_out_of_scale_is_refused_rather_than_amortizing_the_whole_price()
    {
        var input = Input(
            [],
            [Purchase(new LocalDate(2024, 11, 18), 88.99m, PurchaseKind.Bundle,
                      [Grant(Months(1), CoverageSource.Included)], allocated: 29.663333m)]);

        Assert.Equal("subscriptions.allocated_scale",
                     SubscriptionTimeline.Build(input, new LocalDate(2025, 6, 1), Horizon).FailureCode());
    }

    [Fact]
    public void A_purchase_granting_to_several_subscriptions_covers_only_this_one()
    {
        var other = new Grant(ItemId.New(), Months(1), CoverageSource.Paid, new LocalDate(2025, 1, 1));
        var input = Input(
            [],
            [Purchase(new LocalDate(2025, 1, 1), 20m, PurchaseKind.PrepaidTerm,
                      [Grant(Months(1), CoverageSource.Paid, new LocalDate(2025, 1, 1)), other])]);

        var timeline = SubscriptionTimeline.Build(input, new LocalDate(2025, 6, 1), Horizon).Ok();

        var period = Assert.Single(timeline.Coverage);
        Assert.Equal(new LocalDate(2025, 1, 1), period.Start);
        Assert.Equal(new LocalDate(2025, 2, 1), period.EndBefore);

        var charge = Assert.Single(timeline.Charges);
        Assert.Equal(20m, charge.Price.Amount);
        Assert.Equal(31, charge.Allocation.Days);
    }

    [Fact]
    public void A_purchase_granting_only_to_another_subscription_is_not_a_charge_here()
    {
        var other = new Grant(ItemId.New(), Months(1), CoverageSource.Paid, new LocalDate(2025, 1, 1));
        var input = Input([], [Purchase(new LocalDate(2025, 1, 1), 20m, PurchaseKind.PrepaidTerm, [other])]);

        var timeline = SubscriptionTimeline.Build(input, new LocalDate(2025, 6, 1), Horizon).Ok();

        Assert.Empty(timeline.Charges);
        Assert.Empty(timeline.Coverage);
    }

    [Fact]
    public void An_open_walk_stopping_at_the_horizon_says_so()
    {
        var input = Input([Phase(new LocalDate(2025, 1, 1), null, 10m)], []);

        var timeline = SubscriptionTimeline.Build(input, new LocalDate(2025, 1, 1), new LocalDate(2025, 5, 1)).Ok();

        // Without the warning, ExpiresOn returns the horizon and reads as a real expiry date.
        Assert.Contains(timeline.Warnings, w => w.Code == "subscriptions.horizon_reached");
        Assert.Equal(new LocalDate(2025, 5, 1), timeline.ExpiresOn());
    }

    [Fact]
    public void A_walk_that_runs_out_of_phases_does_not_blame_the_horizon()
    {
        var input = Input([Phase(new LocalDate(2025, 1, 1), null, 10m, maxCycles: 3)], []);

        var timeline = SubscriptionTimeline.Build(input, new LocalDate(2025, 1, 1), Horizon).Ok();

        Assert.DoesNotContain(timeline.Warnings, w => w.Code == "subscriptions.horizon_reached");
        Assert.Equal(new LocalDate(2025, 4, 1), timeline.ExpiresOn());
    }

    [Fact]
    public void A_phase_passed_over_with_renewals_left_in_its_limit_warns()
    {
        // An intro offer of three months at 0.99 plus a three-month gift bought two weeks in: the gift
        // carries the subscription past the intro phase's ends_on with two of its three cycles unspent.
        var input = Input(
            [Phase(new LocalDate(2025, 1, 1), new LocalDate(2025, 4, 1), 0.99m, maxCycles: 3),
             Phase(new LocalDate(2025, 4, 1), null, 9.99m)],
            [Purchase(new LocalDate(2025, 1, 15), 0m, PurchaseKind.Gift, [Grant(Months(3), CoverageSource.Gift)])]);

        var timeline = SubscriptionTimeline.Build(input, new LocalDate(2025, 1, 1), new LocalDate(2025, 8, 1)).Ok();

        var warning = Assert.Single(timeline.Warnings, w => w.Code == "subscriptions.phase_cycles_unspent");
        Assert.NotNull(warning.Reference);

        // Billing is unchanged: one intro charge, then full price from the end of the gift.
        Assert.Single(timeline.Charges, c => c.Price.Amount == 0.99m);
        Assert.Equal(new LocalDate(2025, 5, 1),
                     timeline.Charges.First(c => c.Price.Amount == 9.99m).Date);
    }

    [Fact]
    public void A_phase_that_spent_its_limit_does_not_warn_about_unspent_cycles()
    {
        var input = Input(
            [Phase(new LocalDate(2025, 1, 1), new LocalDate(2025, 4, 1), 0.99m, maxCycles: 3),
             Phase(new LocalDate(2025, 4, 1), null, 9.99m)],
            []);

        var timeline = SubscriptionTimeline.Build(input, new LocalDate(2025, 1, 1), new LocalDate(2025, 8, 1)).Ok();

        Assert.DoesNotContain(timeline.Warnings, w => w.Code == "subscriptions.phase_cycles_unspent");
    }

    [Fact]
    public void A_timeline_hands_out_lists_a_caller_cannot_mutate()
    {
        // 1C caches and shares one built timeline, so no consumer may cast a view back to a live list.
        var input = Input(
            [Phase(new LocalDate(2025, 1, 1), null, 10m, maxCycles: 1)],
            [Purchase(new LocalDate(2025, 1, 10), 5m, PurchaseKind.PrepaidTerm,
                      [Grant(Months(1), CoverageSource.Paid, new LocalDate(2025, 3, 1))])]);

        var timeline = SubscriptionTimeline.Build(input, new LocalDate(2025, 6, 1), Horizon).Ok();

        Assert.True(((IList<CoveragePeriod>)timeline.Coverage).IsReadOnly);
        Assert.True(((IList<Charge>)timeline.Charges).IsReadOnly);
        Assert.True(((IList<Finding>)timeline.Warnings).IsReadOnly);

        var purchase = Assert.Single(timeline.Charges, c => c.Origin is ChargeOrigin.FromPurchase);
        Assert.True(((IList<CoveragePeriod>)purchase.Coverage).IsReadOnly);
    }

    [Fact]
    public void A_refused_input_never_produces_a_timeline()
    {
        var input = Input(
            [Phase(new LocalDate(2025, 1, 1), new LocalDate(2025, 6, 1), 10m),
             Phase(new LocalDate(2025, 3, 1), null, 10m)],
            []);

        Assert.Equal("subscriptions.phase_overlap",
                     SubscriptionTimeline.Build(input, new LocalDate(2025, 1, 1), Horizon).FailureCode());
    }
}
