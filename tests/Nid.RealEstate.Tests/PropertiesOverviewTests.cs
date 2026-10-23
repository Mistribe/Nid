using System.Globalization;
using Nid.Core.Contracts;
using Nid.RealEstate.Application;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;
using Duration = Nid.Core.Contracts.Duration;

namespace Nid.RealEstate.Tests;

/// <summary>Properties dashboard spec §3.4: the overview's figures on a fixture household, with the real engine.</summary>
public class PropertiesOverviewTests
{
    private static readonly LocalDate Today = new(2026, 10, 22);
    private static readonly PersonId Alice = PersonId.New();
    private static readonly PersonId Bob = PersonId.New();
    private static readonly IReadOnlyList<PersonInfo> Persons =
        [new(Alice, PersonKind.Adult, "Alice", null, 0), new(Bob, PersonKind.Adult, "Bob", null, 0)];

    /// <summary>1 CHF = 1.05 EUR, every day of the curve (rates are per USD: 1 USD = 0.9 EUR = 0.857142… CHF).</summary>
    private static readonly RateTable Rates = new(
        [new FxRate(Today.PlusYears(-4), Currency.Eur, 0.9m, FxRateSource.Ecb), new FxRate(Today.PlusYears(-4), Chf, 0.9m / 1.05m, FxRateSource.Ecb)],
        Today, 3000);

    private static Currency Chf => Currency.TryCreate("CHF", out var c) ? c : throw new InvalidOperationException();

    private static ItemAccess Item(string name, string currency = "EUR", Visibility visibility = Visibility.Shared, params (PersonId Person, int Bp)[] owners)
    {
        var shares = owners.Length == 0 ? [(Alice, 5_000), (Bob, 5_000)] : owners;
        return new ItemAccess(ItemId.New(), name, visibility, shares.Select(o => new Owner(o.Person, PersonKind.Adult, o.Bp)).ToList()) { Currency = currency };
    }

    private static PropertyDetails Bought(PropertyType type, decimal price, decimal notary, LocalDate on, LocalDate? rentalStartsOn = null)
        => new(type, on, price, notary, 0m, 0m, null, null, PropertyCadence.Yearly, null, rentalStartsOn, null);

    private static Valuation Estimate(ItemAccess item, LocalDate on, decimal value)
        => new(Guid.NewGuid(), item.Id, on, Loans.Ok(Money.Create(value, Currency.TryCreate(item.Currency!, out var c) ? c : Currency.Eur)), null);

    private static Flow Rent(ItemAccess item, LocalDate on, decimal amount)
        => new(Guid.NewGuid(), item.Id, on, FlowKind.Income, Loans.Ok(Money.Create(amount, Currency.Eur)), PropertyCategories.Rent, null, null);

    private static (StoredLoan, ItemAccess) Loan(LoanTerms terms, ItemAccess item) => (Loans.Stored(terms: terms), item);

    private static OverviewProperty Property(
        ItemAccess item, PropertyDetails details, IReadOnlyList<Valuation> values, IReadOnlyList<Flow>? flows = null,
        IReadOnlyList<(StoredLoan, ItemAccess)>? loans = null, IReadOnlyList<StoredRecurringCost>? costs = null,
        IReadOnlyList<StoredTenancy>? tenancies = null, IReadOnlyList<ItemId>? leftOut = null)
        => new(item, new StoredProperty(item.Id, details, 1, costs ?? [], tenancies ?? []), values, flows ?? [], loans ?? [], leftOut ?? []);

    /// <summary>The home, shared 50/50, bought 2019 for 381 500, estimated 412 000; the golden loan (Alice 70 %, Bob 30 %) and a PTZ.</summary>
    private static readonly ItemAccess HomeItem = Item("Lyon 7e");
    private static readonly ItemAccess HomeLoanItem = Item("Prêt maison", owners: [(Alice, 7_000), (Bob, 3_000)]);
    private static readonly ItemAccess PtzItem = Item("PTZ");

    private static OverviewProperty Home(IReadOnlyList<ItemId>? leftOut = null) => Property(HomeItem,
        Bought(PropertyType.PrimaryResidence, 355_000m, 26_500m, new LocalDate(2019, 4, 12)),
        [Estimate(HomeItem, new LocalDate(2024, 9, 30), 402_000m), Estimate(HomeItem, new LocalDate(2026, 9, 30), 412_000m)],
        loans: [Loan(Loans.Golden(), HomeLoanItem), Loan(Loans.Annuity(40_000m, 0m, 240), PtzItem)], leftOut: leftOut);

    /// <summary>A rental let since 2024 at 780 a month, bought 186 300 all in, estimated 198 000; Alice's alone.</summary>
    private static readonly ItemAccess FlatItem = Item("T2 Villeurbanne", owners: [(Alice, 10_000)]);

    private static OverviewProperty Flat() => Property(FlatItem,
        Bought(PropertyType.Rental, 173_000m, 13_300m, new LocalDate(2023, 12, 5)),
        [Estimate(FlatItem, new LocalDate(2026, 9, 30), 198_000m)],
        flows: Enumerable.Range(0, 12).Select(m => Rent(FlatItem, new LocalDate(2025, 11, 5).PlusMonths(m), 780m)).ToList(),
        costs: [new StoredRecurringCost(Guid.NewGuid(), new RecurringCost("property_tax", 1_120m, Loans.Ok(Duration.Create(1, DurationUnit.Year)), new LocalDate(2024, 11, 15), null, null), 1)],
        tenancies: [new StoredTenancy(Guid.NewGuid(), new Tenancy("Bail", new LocalDate(2024, 3, 1), new LocalDate(2026, 12, 1), 780m, 0m, null, null), 1)]);

    /// <summary>A rental never estimated, estimate stale elsewhere: the third property of the household.</summary>
    private static readonly ItemAccess StudioItem = Item("Studio", owners: [(Bob, 10_000)]);

    private static OverviewProperty Studio() => Property(StudioItem, Bought(PropertyType.Rental, 80_000m, 6_400m, new LocalDate(2021, 6, 18), new LocalDate(2021, 7, 1)), []);

    private static readonly ItemAccess ChaletItem = Item("Chalet", "CHF", owners: [(Bob, 10_000)]);

    /// <summary>Estimated 200 000 CHF two years ago: stale (a yearly estimate is stale after 400 days).</summary>
    private static OverviewProperty Chalet() => Property(ChaletItem, Bought(PropertyType.SecondaryResidence, 180_000m, 0m, new LocalDate(2020, 1, 1)),
        [Estimate(ChaletItem, new LocalDate(2024, 10, 1), 200_000m)]);

    private static PropertiesOverview Build(PropertiesSpan span = PropertiesSpan.ThreeYears, params OverviewProperty[] set)
        => PropertiesOverviewMath.Build(set, Currency.Eur, Rates, Today, PropertiesOverviewMath.Grid(span, Today), span, Persons, []);

    private static decimal D(string amount) => decimal.Parse(amount, CultureInfo.InvariantCulture);

    private static decimal D(LoanMoneyView? money) => D(money!.Amount);

    private static decimal Balance(LoanTerms terms) => LoanMath.At(terms, LoanReading.Schedule(terms, Loans.Stored(terms: terms)), Today).Balance;

    [Fact]
    public void Each_row_carries_its_detail_figures_and_the_totals_add_up_the_rows()
    {
        var overview = Build(set: [Home(), Flat(), Studio()]);

        var owed = Balance(Loans.Golden()) + Balance(Loans.Annuity(40_000m, 0m, 240));
        Assert.Equal((3, 1, 0), (overview.Count, overview.WithoutValueCount, overview.StaleCount));
        Assert.Equal(610_000m, D(overview.Value));
        Assert.Equal(owed, D(overview.LinkedBalance));
        Assert.Equal(2, overview.LoanCount);
        Assert.Equal(610_000m - owed, D(overview.NetValue));
        // The cost price of the properties with an estimate: 381 500 + 186 300.
        Assert.Equal(567_800m, D(overview.CostPrice));
        Assert.Equal(42_200m, D(overview.LatentGain));
        Assert.Equal(7.4m, overview.LatentGainPct);
        Assert.Equal(new LocalDate(2026, 9, 30), overview.OldestAsOf);

        var home = overview.Properties.Single(p => p.Name == "Lyon 7e");
        Assert.Equal(("412000.00", "30500.00"), (home.Value, home.LatentGain));
        Assert.Equal(owed.ToString("0.00", CultureInfo.InvariantCulture), home.LinkedBalance);
        Assert.Equal(["Lyon 7e", "T2 Villeurbanne", "Studio"], overview.Properties.Select(p => p.Name));
        Assert.Null(overview.Properties[2].Value);
    }

    [Fact]
    public void Rent_and_the_gross_yield_come_from_the_let_properties_on_their_cost_price()
    {
        var overview = Build(set: [Home(), Flat(), Studio()]);

        // The studio is let since 2021 but received nothing: it counts, with its own cost price (D9).
        Assert.Equal(9_360m, D(overview.RentReceived));
        Assert.Equal((9_360m / (186_300m + 86_400m)).ToString("0.0000", CultureInfo.InvariantCulture), overview.GrossYield);
        Assert.Equal((9_360m / 186_300m).ToString("0.0000", CultureInfo.InvariantCulture), overview.Properties.Single(p => p.Name == "T2 Villeurbanne").GrossYield);
        Assert.Null(Build(set: [Home()]).RentReceived);
    }

    [Fact]
    public void Each_person_s_net_value_follows_the_shares_of_each_property_and_of_each_loan()
    {
        var overview = Build(set: [Home(), Flat()]);

        var golden = Balance(Loans.Golden());
        var ptz = Balance(Loans.Annuity(40_000m, 0m, 240));
        var alice = overview.ByPerson.Single(p => p.PersonId == Alice.Value);
        var bob = overview.ByPerson.Single(p => p.PersonId == Bob.Value);
        Assert.Equal(206_000m + 198_000m, D(alice.Value));
        Assert.Equal(Math.Round(golden * 0.7m, 2, MidpointRounding.AwayFromZero) + Math.Round(ptz * 0.5m, 2, MidpointRounding.AwayFromZero), D(alice.LinkedBalance));
        Assert.Equal(D(overview.NetValue), D(alice.NetValue) + D(bob.NetValue));
        Assert.Equal(D(overview.LinkedBalance), D(alice.LinkedBalance) + D(bob.LinkedBalance));
    }

    [Fact]
    public void The_split_by_type_and_the_last_point_match_today_s_figures()
    {
        var overview = Build(set: [Home(), Flat(), Chalet()]);

        Assert.Equal(["primary_residence", "secondary_residence", "rental"], overview.ByType.Select(t => t.Type));
        Assert.Equal(210_000m, D(overview.ByType.Single(t => t.Type == "secondary_residence").Value));
        var last = overview.Points[^1];
        Assert.Equal(Today, last.On);
        Assert.Equal((D(overview.Value), D(overview.LinkedBalance), D(overview.NetValue)), (D(last.Value), D(last.LinkedBalance), D(last.NetValue)));
        Assert.Equal(37, overview.Points.Count);
    }

    [Fact]
    public void The_curve_counts_a_property_at_its_price_until_its_first_estimate_and_each_loan_from_its_disbursement()
    {
        var overview = Build(PropertiesSpan.All, Home());

        // « Tout » starts at the first month end after the purchase (April 2019): before its first estimate, the price paid.
        Assert.Equal(new LocalDate(2019, 4, 30), overview.Points[0].On);
        Assert.Equal(355_000m, D(overview.Points[0].Value));
        Assert.Equal(355_000m, D(overview.Points[0].NetValue));
        // The loans from their disbursement in January 2024: the value stays the price, the net value drops by what is owed.
        var disbursed = overview.Points.Single(p => p.On == new LocalDate(2024, 1, 31));
        Assert.Equal(355_000m, D(disbursed.Value));
        Assert.True(D(disbursed.NetValue) < 355_000m);
        Assert.Equal(402_000m, D(overview.Points.Single(p => p.On == new LocalDate(2024, 9, 30)).Value));
        Assert.Equal(402_000m, D(overview.Points.Single(p => p.On == new LocalDate(2026, 8, 31)).Value));
    }

    [Fact]
    public void A_property_never_estimated_stays_out_of_the_curve()
    {
        var overview = Build(PropertiesSpan.All, Studio());
        Assert.Empty(overview.Points);
    }

    [Fact]
    public void A_property_in_another_currency_is_converted_and_a_missing_rate_is_named()
    {
        var overview = Build(set: [Chalet()]);
        Assert.Equal(210_000m, D(overview.Value));
        Assert.Equal("CHF", overview.Properties[0].Currency);
        Assert.Equal("200000.00", overview.Properties[0].Value);

        var noRate = PropertiesOverviewMath.Build([Chalet()], Currency.Eur, RateTable.Empty(Today, 0), Today,
            PropertiesOverviewMath.Grid(PropertiesSpan.TwelveMonths, Today), PropertiesSpan.TwelveMonths, Persons, []);
        Assert.Null(noRate.Value);
        Assert.Null(noRate.NetValue);
        Assert.Contains(noRate.Findings, f => f.Code == FxCodes.RateMissing);
    }

    [Fact]
    public void The_watch_list_names_the_warnings_first_then_what_comes_within_60_days()
    {
        var hidden = ItemId.New();
        var overview = Build(set: [Home(leftOut: [hidden]), Flat(), Studio(), Chalet()]);

        var codes = overview.Watch.Select(w => (w.Code, w.PropertyId)).ToList();
        Assert.Contains((PropertyWarnings.VisibilityMismatch, HomeItem.Id), codes);
        Assert.Equal(hidden, overview.Watch.Single(w => w.Code == PropertyWarnings.VisibilityMismatch).LoanId);
        Assert.Contains((PropertyWarnings.NoValuation, StudioItem.Id), codes);
        Assert.Equal(new LocalDate(2024, 10, 1), overview.Watch.Single(w => w.Code == PropertyWarnings.ValuationStale).On);
        // The studio is let since 2021 without a tenancy: nothing expected, so no arrears.
        Assert.DoesNotContain(overview.Watch, w => w.Code == PropertyOverviewCodes.RentArrears);
        var due = overview.Watch.Single(w => w.Code == UpcomingKinds.PropertyCostDue);
        Assert.Equal((new LocalDate(2026, 11, 15), "1120.00"), (due.On, due.Amount!.Amount));
        Assert.Equal(new LocalDate(2026, 12, 1), overview.Watch.Single(w => w.Code == UpcomingKinds.TenancyEnd).On);
        Assert.True(overview.Watch.ToList().FindLastIndex(w => w.On is null || w.Code == PropertyWarnings.ValuationStale)
            < overview.Watch.ToList().FindIndex(w => w.Code == UpcomingKinds.PropertyCostDue));
        Assert.Contains(hidden, overview.ExposedItems());
    }

    [Fact]
    public void Arrears_over_the_rent_ledger_are_watched_with_their_amount()
    {
        var flat = Flat() with { Flows = Enumerable.Range(0, 10).Select(m => Rent(FlatItem, new LocalDate(2025, 11, 5).PlusMonths(m), 780m)).ToList() };
        var overview = Build(set: [flat]);

        // The ledger counts from the first rent, in November 2025: September 2026, over for more than 10 days, is unpaid; October,
        // unpaid too, is not watched yet.
        var arrears = overview.Watch.Single(w => w.Code == PropertyOverviewCodes.RentArrears);
        Assert.Equal(("780.00", "EUR"), (arrears.Amount!.Amount, arrears.Amount.Currency));
    }

    [Fact]
    public void An_empty_household_has_no_figure_and_no_point()
    {
        var overview = Build(PropertiesSpan.All);
        Assert.Equal(0, overview.Count);
        Assert.Null(overview.Value);
        Assert.Null(overview.NetValue);
        Assert.Empty(overview.Points);
        Assert.Empty(overview.ByPerson);
    }
}
