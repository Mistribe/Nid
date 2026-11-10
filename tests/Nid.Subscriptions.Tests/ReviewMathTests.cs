using NodaTime;
using Nid.Core.Contracts;
using Nid.Subscriptions.Application;
using Nid.Subscriptions.Application.Catalog;
using Nid.Subscriptions.Contracts;
using Nid.Subscriptions.Domain;
using static Nid.Subscriptions.Tests.TestData;

namespace Nid.Subscriptions.Tests;

/// <summary>Issue #12: the yearly review's subscriptions over (31 Dec 2023, 31 Dec 2024].</summary>
public class ReviewMathTests
{
    private static readonly LocalDate Today = new(2025, 10, 15);
    private static readonly LocalDate Start = new(2023, 12, 31), End = new(2024, 12, 31);

    private static BuiltSubscription Built(string name, IReadOnlyList<BillingPhase> phases, string? provider = null)
    {
        var input = new SubscriptionInput(new SubscriptionHeader(ItemId.New(), true, null), phases, []);
        return new BuiltSubscription(input, SubscriptionTimeline.Build(input, Today, Today.PlusMonths(13)).Ok(), name, [],
            provider is null ? null : new ProviderSummaryView(provider, provider.ToUpperInvariant(), "custom", [], null, []));
    }

    [Fact]
    public void A_price_rise_mid_year_is_a_change()
    {
        var sub = Built("Video", [Phase(new LocalDate(2024, 1, 1), new LocalDate(2024, 7, 1), 9.99m), Phase(new LocalDate(2024, 7, 1), null, 12.99m)]);

        var change = Assert.Single(ReviewMath.PriceChanges([sub], Start, End));

        Assert.Equal((new LocalDate(2024, 7, 1), 9.99m, "EUR", Months(1), 12.99m, "EUR", Months(1), false),
            (change.On, change.BeforeAmount, change.BeforeCurrency, change.BeforeEvery, change.AfterAmount, change.AfterCurrency, change.AfterEvery, change.PromoEnded));
        Assert.Equal(6 * 9.99m + 6 * 12.99m, ReviewMath.Cost([sub], Start, End).Sum(p => p.Amount));
    }

    [Fact]
    public void A_launch_offer_ending_is_a_promo_end_and_the_same_price_again_is_no_change()
    {
        var promo = Built("Music", [Phase(new LocalDate(2024, 2, 1), new LocalDate(2024, 5, 1), 1m, maxCycles: 3), Phase(new LocalDate(2024, 5, 1), null, 10m)]);
        var same = Built("News", [Phase(new LocalDate(2024, 1, 1), new LocalDate(2024, 6, 1), 5m), Phase(new LocalDate(2024, 6, 1), null, 5m)]);
        var yearly = Built("Cloud", [Phase(new LocalDate(2024, 1, 1), new LocalDate(2024, 6, 1), 5m), Phase(new LocalDate(2024, 6, 1), null, 5m, Months(12))]);

        var changes = ReviewMath.PriceChanges([promo, same, yearly], Start, End);

        Assert.Equal([("Music", true), ("Cloud", false)], changes.Select(c => (c.Name, c.PromoEnded)));
    }

    [Fact]
    public void A_gap_between_two_phases_at_another_price_is_a_change_dated_on_the_restart()
    {
        var sub = Built("Gym", [Phase(new LocalDate(2024, 1, 1), new LocalDate(2024, 3, 1), 20m), Phase(new LocalDate(2024, 5, 1), null, 25m)]);

        var change = Assert.Single(ReviewMath.PriceChanges([sub], Start, End));

        Assert.Equal((new LocalDate(2024, 5, 1), 20m, 25m), (change.On, change.BeforeAmount, change.AfterAmount));
    }

    [Fact]
    public void Started_and_ended_in_the_year()
    {
        var older = Built("Older", [Phase(new LocalDate(2023, 6, 1), null, 4m)]);
        var trial = Built("Trial", [Phase(new LocalDate(2024, 3, 1), new LocalDate(2024, 9, 1), 8m)], "acme");
        var replaced = Built("Replaced", [Phase(new LocalDate(2023, 1, 1), new LocalDate(2024, 5, 1), 6m)], "box");
        var successor = Built("Successor", [Phase(new LocalDate(2024, 5, 1), null, 7m)], "box");
        var later = Built("Later", [Phase(new LocalDate(2023, 1, 1), new LocalDate(2025, 2, 1), 3m)]);
        IReadOnlyList<BuiltSubscription> set = [older, trial, replaced, successor, later];

        Assert.Equal([("Trial", new LocalDate(2024, 3, 1), "ACME"), ("Successor", new LocalDate(2024, 5, 1), "BOX")],
            ReviewMath.Started(set, Start, End).Select(i => (i.Name, i.On, i.Provider)));
        Assert.Equal([("Trial", new LocalDate(2024, 9, 1))], ReviewMath.Ended(set, Start, End, Today).Select(i => (i.Name, i.On)));
    }

    [Fact]
    public void An_expiry_later_in_the_year_than_today_is_not_ended_yet()
    {
        var past = Built("Past", [Phase(new LocalDate(2025, 1, 1), new LocalDate(2025, 10, 1), 4m)]);
        var coming = Built("Coming", [Phase(new LocalDate(2025, 1, 1), new LocalDate(2025, 11, 1), 4m)]);

        Assert.Equal(["Past"], ReviewMath.Ended([past, coming], new LocalDate(2024, 12, 31), new LocalDate(2025, 12, 31), Today).Select(i => i.Name));
    }

    [Fact]
    public void An_archived_subscription_ends_on_the_earlier_of_its_expiry_and_its_archive_day()
    {
        var running = Built("Running", [Phase(new LocalDate(2024, 1, 1), null, 4m)]).ArchivedAt(new LocalDate(2024, 8, 1));
        var before = Built("Before", [Phase(new LocalDate(2024, 1, 1), new LocalDate(2024, 10, 1), 4m)]).ArchivedAt(new LocalDate(2024, 9, 15));
        var onIt = Built("On it", [Phase(new LocalDate(2024, 1, 1), new LocalDate(2024, 10, 1), 4m)]).ArchivedAt(new LocalDate(2024, 10, 1));
        var after = Built("After", [Phase(new LocalDate(2024, 1, 1), new LocalDate(2024, 5, 1), 4m)]).ArchivedAt(new LocalDate(2024, 9, 1));
        var nextYear = Built("Next year", [Phase(new LocalDate(2024, 1, 1), null, 4m)]).ArchivedAt(new LocalDate(2025, 3, 1));

        Assert.Equal(
            [("After", new LocalDate(2024, 5, 1)), ("Running", new LocalDate(2024, 8, 1)), ("Before", new LocalDate(2024, 9, 15)), ("On it", new LocalDate(2024, 10, 1))],
            ReviewMath.Ended([running, before, onIt, after, nextYear], Start, End, Today).Select(i => (i.Name, i.On)));
    }

    [Fact]
    public void An_archived_open_ended_subscription_is_no_successor_after_its_archive_day()
    {
        var old = Built("Old", [Phase(new LocalDate(2023, 1, 1), new LocalDate(2024, 5, 1), 6m)], "box");
        var archived = Built("Archived", [Phase(new LocalDate(2024, 3, 1), null, 7m)], "box").ArchivedAt(new LocalDate(2024, 4, 15));

        // Cut at 15 April, the archived one no longer covers past the old one's end; the old one, still covered then, is its successor.
        Assert.Equal([("Old", new LocalDate(2024, 5, 1))],
            ReviewMath.Ended([old, archived], Start, End, Today).Select(i => (i.Name, i.On)));
    }
}
