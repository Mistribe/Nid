using System.Globalization;
using NodaTime;
using Nid.Core.Contracts;
using Nid.Subscriptions.Application;
using Nid.Subscriptions.Application.Catalog;
using Nid.Subscriptions.Contracts;
using Nid.Subscriptions.Domain;
using static Nid.Subscriptions.Tests.TestData;
using Duration = Nid.Core.Contracts.Duration;

namespace Nid.Subscriptions.Tests;

/// <summary>#395 to #398: spent totals, cost per year, savings from cancellations, price rises.</summary>
public class SpendingHistoryTests
{
    private static readonly LocalDate Today = new(2025, 10, 15);
    private static readonly RateTable NoRates = RateTable.Empty(Today, 7);

    private static ProviderSummaryView Provider(string key, params string[] labels)
        => new(key, key.ToUpperInvariant(), "custom", labels.Select(l => new LabelView(l, l.ToUpperInvariant(), "#3679cd")).ToList(), null, []);

    private static BuiltSubscription Built(IReadOnlyList<BillingPhase> phases, ProviderSummaryView? provider = null, string name = "s",
        IReadOnlyList<Purchase>? purchases = null, ItemId? id = null)
    {
        var input = new SubscriptionInput(new SubscriptionHeader(id ?? ItemId.New(), true, null), phases, purchases ?? []);
        return new BuiltSubscription(input, SubscriptionTimeline.Build(input, Today, Today.PlusMonths(13)).Ok(), name,
            [new ItemOwnerView(new Guid(1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0), 10_000)], provider);
    }

    private static BillingPhase Usd(LocalDate start, decimal price)
        => new(BillingPhaseId.New(), start, null, Money.Create(price, Currency.Usd).Ok(), Months(1), null, null, Household);

    private static decimal? Amount(MoneyView? money) => money is null ? null : decimal.Parse(money.Amount, CultureInfo.InvariantCulture);

    private static SubscriptionsOverview Overview(params BuiltSubscription[] set)
        => OverviewMath.Build(set, [], Currency.Eur, NoRates, Today);

    [Fact]
    public void Spent_sums_the_past_charges_since_the_first_and_over_twelve_months()
    {
        var sub = Built([Phase(new LocalDate(2024, 1, 5), null, 10m)]);

        var (spent, missing) = SpendingHistory.Spent(sub.Timeline, Currency.Eur, NoRates, Today);

        // 5 Jan 2024 to 5 Oct 2025: 22 charges; after 15 Oct 2024: 5 Nov 2024 to 5 Oct 2025, 12 charges.
        Assert.Equal((new LocalDate(2024, 1, 5), 220m, 120m, false), (spent!.Since, Amount(spent.Total), Amount(spent.Last12Months), spent.Partial));
        Assert.Empty(missing);
    }

    [Fact]
    public void Spent_is_none_before_the_first_charge()
        => Assert.Null(SpendingHistory.Spent(Built([Phase(Today.PlusDays(3), null, 10m)]).Timeline, Currency.Eur, NoRates, Today).View);

    [Fact]
    public void A_missing_rate_leaves_a_partial_total_and_names_the_rate()
    {
        var sub = Built([Phase(new LocalDate(2025, 7, 1), new LocalDate(2025, 9, 1), 10m), Usd(new LocalDate(2025, 9, 1), 20m)]);

        var (spent, missing) = SpendingHistory.Spent(sub.Timeline, Currency.Eur, NoRates, Today);

        Assert.True(spent!.Partial);
        Assert.Equal(20m, Amount(spent.Total)); // July and August in euros; September and October have no USD rate
        Assert.Equal(2, missing.Count);
        Assert.All(missing, f => Assert.Equal(FxCodes.RateMissing, f.Code));
    }

    [Fact]
    public void A_charge_in_another_currency_converts_at_its_date()
    {
        var sub = Built([Usd(new LocalDate(2025, 9, 1), 20m)]);
        var rates = new RateTable([new FxRate(new LocalDate(2025, 9, 1), Currency.Eur, 0.5m, FxRateSource.Ecb),
            new FxRate(new LocalDate(2025, 10, 1), Currency.Eur, 0.8m, FxRateSource.Ecb)], Today, 7);

        var (spent, _) = SpendingHistory.Spent(sub.Timeline, Currency.Eur, rates, Today);

        Assert.Equal((26m, false), (Amount(spent!.Total), spent.Partial)); // 10 + 16
    }

    private static BuiltSubscription Ended(ProviderSummaryView? provider = null, decimal price = 10m)
        => Built([Phase(new LocalDate(2025, 1, 1), new LocalDate(2025, 6, 1), price)], provider, "ended");

    [Fact]
    public void An_ended_subscription_saves_its_last_price_at_each_cycle_since_its_end()
    {
        var saved = SpendingHistory.Saved(Ended(), [], Currency.Eur, NoRates, Today)!;

        // Ended 1 June: 1 June, July, August, September and October would have charged 10.
        Assert.Equal((new LocalDate(2025, 6, 1), 50m, "10", 1, "month"),
            (saved.Since, Amount(saved.Total), saved.LastPrice.Amount, saved.Every.Count, saved.Every.Unit));
    }

    [Fact]
    public void A_subscription_replaced_at_the_same_provider_saves_nothing()
    {
        var netflix = Provider("netflix");
        var ended = Ended(netflix);

        Assert.Null(SpendingHistory.Saved(ended, [ended, Built([Phase(new LocalDate(2025, 6, 1), null, 12m)], netflix)], Currency.Eur, NoRates, Today));
        Assert.NotNull(SpendingHistory.Saved(ended, [ended, Built([Phase(new LocalDate(2025, 6, 1), null, 12m)], Provider("disney"))],
            Currency.Eur, NoRates, Today));
    }

    [Fact]
    public void A_running_or_free_subscription_saves_nothing()
    {
        Assert.Null(SpendingHistory.Saved(Built([Phase(new LocalDate(2025, 1, 1), null, 10m)]), [], Currency.Eur, NoRates, Today));
        Assert.Null(SpendingHistory.Saved(Ended(price: 0m), [], Currency.Eur, NoRates, Today));
    }

    [Fact]
    public void A_dearer_phase_in_the_last_twelve_months_is_a_rise_with_its_yearly_difference()
    {
        var sub = Built([Phase(new LocalDate(2024, 1, 1), new LocalDate(2025, 3, 1), 9.99m), Phase(new LocalDate(2025, 3, 1), null, 12.99m)]);

        var rise = Assert.Single(SpendingHistory.Rises([sub], Today));

        Assert.Equal((new LocalDate(2025, 3, 1), 36m), (rise.To.StartsOn, rise.Difference.Amount));
    }

    [Fact]
    public void A_rise_older_than_twelve_months_or_a_cheaper_year_is_not_listed()
    {
        var old = Built([Phase(new LocalDate(2023, 1, 1), new LocalDate(2024, 9, 1), 9.99m), Phase(new LocalDate(2024, 9, 1), null, 12.99m)]);
        // 10 a month is 120 a year; 100 a year is cheaper even if the charge is higher.
        var yearly = Built([Phase(new LocalDate(2025, 1, 1), new LocalDate(2025, 5, 1), 10m),
            Phase(new LocalDate(2025, 5, 1), null, 100m, every: Duration.Create(1, DurationUnit.Year).Ok())]);

        Assert.Empty(SpendingHistory.Rises([old, yearly], Today));
    }

    [Fact]
    public void A_week_counts_fifty_two_or_fifty_three_times_a_year()
    {
        var weekly = Phase(new LocalDate(2025, 1, 1), null, 1m, every: Duration.Create(1, DurationUnit.Week).Ok());
        Assert.Equal(53m, SpendingHistory.Yearly(weekly, new LocalDate(2025, 1, 1)));
        Assert.Equal(12m, SpendingHistory.Yearly(Phase(new LocalDate(2025, 1, 31), null, 1m), new LocalDate(2025, 1, 31)));
    }

    [Fact]
    public void The_overview_lists_rises_and_tells_a_launch_offer_ending_apart()
    {
        var rise = Built([Phase(new LocalDate(2024, 1, 1), new LocalDate(2025, 3, 1), 9.99m), Phase(new LocalDate(2025, 3, 1), null, 12.99m)], name: "rise");
        var promo = Built([Phase(new LocalDate(2025, 5, 1), new LocalDate(2025, 8, 1), 0.99m, maxCycles: 3), Phase(new LocalDate(2025, 8, 1), null, 9.99m)], name: "promo");

        var rises = Overview(rise, promo).PriceRises;

        Assert.Equal([("promo", true), ("rise", false)], rises.Items.Select(r => (r.DisplayName, r.PromoEnded)).ToList());
        Assert.Equal(36m, Amount(rises.YearlyTotal)); // the launch offer ending is not an increase
        Assert.Empty(Overview(Built([Phase(new LocalDate(2025, 1, 1), null, 5m)])).PriceRises.Items);
    }

    [Fact]
    public void Years_run_from_the_first_charge_to_december_and_split_this_year()
    {
        var music = Provider("spotify", "music");
        var years = Overview(Built([Phase(new LocalDate(2024, 1, 5), null, 10m)], music), Built([Phase(new LocalDate(2025, 2, 1), null, 5m)])).Years;

        Assert.Equal([2024, 2025], years.Select(y => y.Year));
        Assert.Equal((120m, 0m, 120m), (Amount(years[0].Paid)!.Value, Amount(years[0].Upcoming)!.Value, Amount(years[0].Total)!.Value));
        // 2025: 10 × 10 + 9 × 5 paid by 15 October; November and December to come.
        Assert.Equal((145m, 30m, 175m), (Amount(years[1].Paid)!.Value, Amount(years[1].Upcoming)!.Value, Amount(years[1].Total)!.Value));
        Assert.Equal([("music", 100m), (ListGrouping.NoneKey, 45m)], years[1].ByLabel.Select(l => (l.Key, Amount(l.Paid)!.Value)).ToList());
        Assert.Equal(["music", ListGrouping.NoneKey], years[0].ByLabel.Select(l => l.Key)); // every label in every year
    }

    [Fact]
    public void Top_providers_count_ended_subscriptions()
    {
        var netflix = Provider("netflix");
        var overview = Overview(Ended(netflix), Built([Phase(new LocalDate(2025, 6, 1), null, 12m)], netflix), Built([Phase(new LocalDate(2025, 1, 1), null, 1m)], Provider("vpn")));

        Assert.Equal([("netflix", 110m), ("vpn", 10m)], overview.TopProviders.Select(p => (p.Provider.Key, Amount(p.Spent)!.Value)).ToList());
    }

    [Fact]
    public void Savings_sum_the_last_twelve_months_of_each_ended_subscription()
    {
        var old = Built([Phase(new LocalDate(2020, 1, 1), new LocalDate(2021, 1, 1), 5m)], name: "old");

        var savings = Overview(Ended(), old, Built([Phase(new LocalDate(2025, 1, 1), null, 1m)])).Savings;

        // « ended »: June to October, 50; « old »: November 2024 to October 2025, 12 × 5.
        Assert.Equal([("old", 60m), ("ended", 50m)], savings.Items.Select(i => (i.DisplayName, Amount(i.Last12Months)!.Value)).ToList());
        Assert.Equal((110m, false), (Amount(savings.Last12Months)!.Value, savings.Partial));
    }

    [Fact]
    public void Provider_groups_sum_what_their_subscriptions_spent()
    {
        var netflix = Provider("netflix");
        var set = new[] { Ended(netflix), Built([Phase(Today.PlusDays(5), null, 12m)], netflix) };
        var summaries = Overview(set).TopProviders;

        Assert.Equal(50m, Amount(Assert.Single(summaries).Spent)); // the one without a charge yet counts as nothing
    }

    [Fact]
    public void A_purchase_shared_by_two_subscriptions_counts_once_for_their_provider()
    {
        var netflix = Provider("netflix");
        var (a, b) = (ItemId.New(), ItemId.New());
        var bundle = Purchase(new LocalDate(2025, 3, 1), 120m, PurchaseKind.PrepaidTerm,
            [new Grant(a, Months(12), CoverageSource.Paid, null), new Grant(b, Months(12), CoverageSource.Paid, null)]);
        var set = new[] { Built([], netflix, "a", [bundle], a), Built([], netflix, "b", [bundle], b) };

        var overview = Overview(set);

        // Each subscription did cost the purchase; the provider and the year count it once.
        Assert.All(set, s => Assert.Equal(120m, Amount(SpendingHistory.Spent(s.Timeline, Currency.Eur, NoRates, Today).View!.Total)));
        Assert.Equal(120m, Amount(Assert.Single(overview.TopProviders).Spent));
        Assert.Equal(120m, Amount(Assert.Single(overview.Years).Paid));
    }
}
