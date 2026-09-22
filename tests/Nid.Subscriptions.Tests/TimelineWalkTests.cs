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

        Assert.True(timeline.Charges.Count <= SubscriptionTimeline.MaxCyclesPerPhase);
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
    public void A_subscription_that_does_not_renew_never_charges()
    {
        var input = Input([Phase(new LocalDate(2025, 1, 1), null, 10m)], [], autoRenew: false);

        var timeline = SubscriptionTimeline.Build(input, new LocalDate(2025, 1, 1), Horizon).Ok();

        Assert.Empty(timeline.Charges);
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
