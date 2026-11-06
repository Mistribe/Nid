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

public class OverviewMathTests
{
    private static readonly LocalDate Today = new(2025, 10, 15);
    private static readonly Guid P1 = new(1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    private static readonly Guid P2 = new(2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    private static readonly Guid P3 = new(3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    private static Duration Years(int count) => Duration.Create(count, DurationUnit.Year).Ok();

    private static ProviderSummaryView Provider(string key, params string[] labels)
        => new(key, key.ToUpperInvariant(), "custom", labels.Select(l => new LabelView(l, l.ToUpperInvariant(), "#" + l)).ToList(), null, []);

    private static BuiltSubscription Built(
        IReadOnlyList<BillingPhase> phases, IReadOnlyList<Purchase>? purchases = null, bool autoRenew = true, ItemId? id = null,
        ProviderSummaryView? provider = null, string name = "s", params (Guid Person, int Bp)[] owners)
    {
        var input = new SubscriptionInput(new SubscriptionHeader(id ?? ItemId.New(), autoRenew, null), phases, purchases ?? []);
        return new BuiltSubscription(
            input, SubscriptionTimeline.Build(input, Today, Today.PlusMonths(12)).Ok(), name,
            (owners.Length == 0 ? [(P1, 10_000)] : owners).Select(o => new ItemOwnerView(o.Person, o.Bp)).ToList(), provider);
    }

    private static BuiltSubscription Monthly(int day, decimal price, ProviderSummaryView? provider = null, string name = "s",
        params (Guid Person, int Bp)[] owners)
        => Built([Phase(new LocalDate(2025, 1, day), null, price)], provider: provider, name: name, owners: owners);

    private static SubscriptionsOverview Overview(params BuiltSubscription[] set) => Overview(set, [P1]);

    private static SubscriptionsOverview Overview(BuiltSubscription[] set, Guid[] persons)
        => OverviewMath.Build(
            set, persons.Select(p => new PersonInfo(new PersonId(p), PersonKind.Adult, p.ToString(), null, 0)).ToList(),
            Currency.Eur, RateTable.Empty(Today, 7), Today, InflationSeries.Empty);

    private static decimal? Amount(MoneyView? money)
        => money is null ? null : decimal.Parse(money.Amount, CultureInfo.InvariantCulture);

    [Fact]
    public void Monthly_and_yearly_charges_split_into_recurring_and_long_cycle()
    {
        var overview = Overview(
            Monthly(5, 17.20m),
            Built([Phase(new LocalDate(2025, 5, 10), null, 216.00m, every: Years(1))]));

        Assert.Equal(Enumerable.Range(0, 12).Select(i => new LocalDate(2025, 11, 1).PlusMonths(i).ToString("yyyy-MM", CultureInfo.InvariantCulture)),
            overview.Months.Select(m => m.Month));
        Assert.All(overview.Months, m => Assert.Equal(17.20m, Amount(m.Recurring)));
        Assert.All(overview.Months, m => Assert.Equal(m.Month == "2026-05" ? 216.00m : 0m, Amount(m.LongCycle)));
    }

    [Fact]
    public void A_shared_purchase_counts_once_in_months_and_upcoming()
    {
        ItemId a = ItemId.New(), b = ItemId.New();
        var purchase = Purchase(new LocalDate(2025, 11, 5), 120m, PurchaseKind.PrepaidTerm,
            [new Grant(a, Months(12), CoverageSource.Paid, null), new Grant(b, Months(12), CoverageSource.Paid, null)]);

        var overview = Overview(Built([], [purchase], id: a), Built([], [purchase], id: b));

        Assert.Equal(120m, Amount(overview.Months[0].LongCycle));
        var line = Assert.Single(overview.Upcoming);
        Assert.Equal((new LocalDate(2025, 11, 5), 120m), (line.Date, Amount(line.Converted)!.Value));
        Assert.Contains(overview.Findings, f => f.Code == HouseholdCost.AmortizedDoubleCountCode);
    }

    [Fact]
    public void Remaining_cash_counts_today_and_after()
    {
        var overview = Overview(Monthly(10, 5m), Monthly(20, 7m));

        Assert.Equal(12m, Amount(overview.MonthCash));
        Assert.Equal(7m, Amount(overview.MonthCashRemaining));
    }

    [Fact]
    public void An_ended_subscription_counts_as_ended_and_nowhere_else()
    {
        var netflix = Provider("netflix");
        var ended = Built([Phase(new LocalDate(2025, 1, 1), new LocalDate(2025, 9, 1), 10m)], autoRenew: false, provider: netflix);

        var overview = Overview(ended, Monthly(1, 8m, netflix));

        Assert.Equal((1, 1), (overview.ActiveCount, overview.EndedCount));
        Assert.All(overview.Months, m => Assert.Equal((8m, 0m), (Amount(m.Recurring)!.Value, Amount(m.LongCycle)!.Value)));
        Assert.DoesNotContain(overview.Upcoming, u => u.ItemId == ended.Input.Subscription.Id);
        Assert.Empty(overview.Watch); // one active netflix: no same_provider
    }

    [Fact]
    public void Upcoming_covers_thirty_days_from_today_sorted_by_date()
    {
        var overview = Overview(Monthly(5, 1m, name: "fifth"), Monthly(20, 2m, name: "twentieth"),
            Monthly(14, 3m, name: "fourteenth"), Monthly(15, 4m, name: "fifteenth"));

        // 14 Nov is today + 30: out; 14 Oct is past.
        Assert.Equal(
            [(new LocalDate(2025, 10, 15), "fifteenth"), (new LocalDate(2025, 10, 20), "twentieth"), (new LocalDate(2025, 11, 5), "fifth")],
            overview.Upcoming.Select(u => (u.Date, u.DisplayName)).ToList());
        Assert.Equal(new MoneyView("4", "EUR"), overview.Upcoming[0].Price);
    }

    private static BuiltSubscription Prepaid(int months, bool autoRenew)
    {
        var id = ItemId.New();
        return Built([], [Purchase(Today, 100m, PurchaseKind.PrepaidTerm, [new Grant(id, Months(months), CoverageSource.Paid, Today)])],
            autoRenew, id);
    }

    [Fact]
    public void Watch_flags_an_end_without_renewal_within_a_year()
    {
        var ends = Prepaid(12, autoRenew: false);

        var watch = Assert.Single(Overview(ends).Watch);

        Assert.Equal(OverviewCodes.EndsWithoutRenewal, watch.Code);
        Assert.Equal([ends.Input.Subscription.Id.Value], watch.ItemIds);
        Assert.Equal(Today.PlusMonths(12), watch.Date);
        Assert.Null(watch.Amount);
    }

    [Fact]
    public void Watch_does_not_flag_an_end_in_thirteen_months_or_a_renewing_one()
    {
        Assert.Empty(Overview(Prepaid(13, autoRenew: false)).Watch);
        Assert.Empty(Overview(Prepaid(12, autoRenew: true)).Watch);
    }

    [Fact]
    public void Watch_flags_a_long_cycle_charge_within_90_days()
    {
        var yearly = Built([Phase(new LocalDate(2025, 1, 13), null, 99m, every: Years(1))]);

        var watch = Assert.Single(Overview(yearly).Watch);

        Assert.Equal(OverviewCodes.LongCycleChargeSoon, watch.Code);
        Assert.Equal(Today.PlusDays(90), watch.Date);
        Assert.Equal(99m, Amount(watch.Amount));
    }

    [Fact]
    public void Watch_does_not_flag_a_long_cycle_charge_in_91_days()
        => Assert.Empty(Overview(Built([Phase(new LocalDate(2025, 1, 14), null, 99m, every: Years(1))])).Watch);

    [Fact]
    public void Watch_flags_two_active_subscriptions_of_one_provider()
    {
        var netflix = Provider("netflix");
        var one = Monthly(1, 8m, netflix);
        var two = Monthly(1, 12m, netflix);

        var watch = Assert.Single(Overview(one, two, Monthly(1, 3m, Provider("other"))).Watch);

        Assert.Equal(OverviewCodes.SameProvider, watch.Code);
        Assert.Equal(new[] { one, two }.Select(b => b.Input.Subscription.Id.Value).Order(), watch.ItemIds.Order());
        Assert.Null(watch.Date);
        Assert.Equal(20m, Amount(watch.Amount));
    }

    [Fact]
    public void Watch_sorts_by_date_with_undated_last()
    {
        var netflix = Provider("netflix");
        var overview = Overview(Monthly(1, 8m, netflix), Monthly(1, 12m, netflix), Prepaid(6, autoRenew: false),
            Built([Phase(new LocalDate(2025, 1, 13), null, 99m, every: Years(1))]));

        Assert.Equal([OverviewCodes.LongCycleChargeSoon, OverviewCodes.EndsWithoutRenewal, OverviewCodes.SameProvider],
            overview.Watch.Select(w => w.Code));
    }

    [Fact]
    public void By_person_lists_every_person_even_at_zero()
    {
        var overview = Overview([Monthly(1, 20m, owners: (P1, 10_000)), Monthly(1, 10m, owners: [(P1, 5_000), (P2, 5_000)])], [P1, P2, P3]);

        Assert.Equal(
            [(P1, 25m, 1, 1), (P2, 5m, 0, 1), (P3, 0m, 0, 0)],
            overview.ByPerson.Select(p => (p.PersonId, Amount(p.Amortized)!.Value, p.OwnedAlone, p.Shared)).ToList());
    }

    [Fact]
    public void By_label_uses_the_first_label_and_sums_to_the_monthly_total()
    {
        var overview = Overview(
            Monthly(1, 20m, Provider("a", "streaming", "music")), Monthly(1, 10m, Provider("b", "streaming")), Monthly(1, 5m));

        Assert.Equal(
            [("streaming", "STREAMING", "#streaming", 30m), (ListGrouping.NoneKey, null, null, 5m)],
            overview.ByLabel.Select(l => (l.Key, l.Name, l.Color, Amount(l.Amortized)!.Value)).ToList());
        Assert.Equal(35m, Amount(overview.MonthlyAmortized));
    }

    [Fact]
    public void A_missing_rate_nulls_only_its_month()
    {
        var usd = new BillingPhase(BillingPhaseId.New(), new LocalDate(2026, 3, 1), null, Money.Create(10m, Currency.Usd).Ok(), Months(1),
            null, null, Household);

        var overview = Overview(Monthly(5, 17.20m), Built([usd]));

        Assert.All(overview.Months, m =>
            Assert.Equal(string.CompareOrdinal(m.Month, "2026-03") < 0 ? 17.20m : null, Amount(m.Recurring)));
        Assert.All(overview.Months, m => Assert.Equal(0m, Amount(m.LongCycle)));
        Assert.NotNull(overview.MonthlyAmortized); // the USD phase starts later: this month needs no USD rate
        Assert.Contains(overview.Findings, f => f.Code == FxCodes.RateMissing);
        Assert.Single(overview.Findings, f => f.Code == FxCodes.RateMissing); // one per rate, however many figures lack it
    }

    [Fact]
    public void An_empty_set_gives_zeros_not_nulls()
    {
        var overview = Overview([], [P1]);

        Assert.Equal("EUR", overview.Currency);
        Assert.Equal([0m, 0m, 0m, 0m],
            new[] { overview.MonthlyAmortized, overview.AnnualAmortized, overview.MonthCash, overview.MonthCashRemaining }.Select(m => Amount(m)!.Value));
        Assert.Equal(12, overview.Months.Count);
        Assert.All(overview.Months, m => Assert.Equal((0m, 0m), (Amount(m.Recurring)!.Value, Amount(m.LongCycle)!.Value)));
        Assert.Equal(0m, Amount(Assert.Single(overview.ByPerson).Amortized));
        Assert.Empty(overview.ByLabel);
        Assert.Empty(overview.Upcoming);
        Assert.Empty(overview.Watch);
        Assert.Empty(overview.Findings);
        Assert.Equal((0, 0), (overview.ActiveCount, overview.EndedCount));
    }

    [Fact]
    public void Rate_pieces_cover_every_figure_the_overview_converts()
    {
        var set = new[] { Monthly(5, 17.20m), Built([Phase(new LocalDate(2025, 5, 10), null, 216.00m, every: Years(1))]) };

        var need = FxConversion.Need(OverviewMath.RatePieces(set, Today).Select(p => p with { Currency = Currency.Usd }), Currency.Eur)!;

        // The spent totals and the years read back to the first charge (#395, #396); the twelfth month's charges are the last ones read.
        Assert.Equal((new LocalDate(2025, 1, 5), new LocalDate(2026, 10, 5)), (need.From, need.To));
    }
}
