using System.Globalization;
using Nid.Core.Contracts;
using Nid.RealEstate.Application;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;

namespace Nid.RealEstate.Tests;

/// <summary>Properties dashboard spec §6.4: the valuation tab's figures on a fixture household.</summary>
public class PropertiesValuationTests
{
    private static readonly LocalDate Today = new(2026, 10, 22);
    private static readonly PersonId Alice = PersonId.New();

    private static ItemAccess Item(string name, string currency = "EUR")
        => new(ItemId.New(), name, Visibility.Shared, [new Owner(Alice, PersonKind.Adult, 10_000)]) { Currency = currency };

    private static Valuation Estimate(ItemAccess item, LocalDate on, decimal value)
        => new(Guid.NewGuid(), item.Id, on, Loans.Ok(Money.Create(value, Currency.TryCreate(item.Currency!, out var c) ? c : Currency.Eur)), null);

    private static Flow Works(ItemAccess item, LocalDate on, decimal amount)
        => new(Guid.NewGuid(), item.Id, on, FlowKind.Expense, Loans.Ok(Money.Create(amount, Currency.Eur)), PropertyCategories.Works, null, null);

    private static OverviewProperty Property(ItemAccess item, PropertyType type, decimal price, decimal notary, LocalDate bought, decimal? surface,
        IReadOnlyList<Valuation> values, IReadOnlyList<Flow>? flows = null)
        => new(item, new StoredProperty(item.Id, new PropertyDetails(type, bought, price, notary, 0m, 0m, surface, null, PropertyCadence.Yearly, null, null, null),
            1, [], []), values, flows ?? [], [], []);

    private static readonly ItemAccess HomeItem = Item("Lyon 7e");

    /// <summary>Bought in April 2019 for 381 500 all in, 8 000 of works in May 2020 and a 2 000 kitchen and 500 of paint on one day in 2023.</summary>
    private static OverviewProperty Home() => Property(HomeItem, PropertyType.PrimaryResidence, 355_000m, 26_500m, new LocalDate(2019, 4, 12), 82m,
        [Estimate(HomeItem, new LocalDate(2024, 9, 30), 402_000m), Estimate(HomeItem, new LocalDate(2026, 9, 30), 412_000m)],
        [Works(HomeItem, new LocalDate(2020, 5, 1), 8_000m), Works(HomeItem, new LocalDate(2023, 6, 1), 2_000m), Works(HomeItem, new LocalDate(2023, 6, 1), 500m)]);

    private static readonly ItemAccess FlatItem = Item("T2 Villeurbanne");

    private static OverviewProperty Flat() => Property(FlatItem, PropertyType.Rental, 173_000m, 13_300m, new LocalDate(2023, 12, 5), 45m,
        [Estimate(FlatItem, new LocalDate(2026, 9, 30), 198_000m)]);

    private static readonly ItemAccess StudioItem = Item("Studio");

    /// <summary>Bought in March: less than a year held, and no surface.</summary>
    private static OverviewProperty Studio() => Property(StudioItem, PropertyType.Rental, 85_000m, 0m, new LocalDate(2026, 3, 1), null,
        [Estimate(StudioItem, new LocalDate(2026, 9, 1), 90_000m)]);

    private static readonly ItemAccess LandItem = Item("Terrain");

    private static OverviewProperty Land() => Property(LandItem, PropertyType.Land, 30_000m, 0m, new LocalDate(2022, 1, 1), 600m, []);

    private static PropertiesValuation Build(params OverviewProperty[] set) => PropertiesValuationMath.Build(set, Currency.Eur, RateTable.Empty(Today, 0), Today);

    private static decimal D(string amount) => decimal.Parse(amount, CultureInfo.InvariantCulture);

    private static decimal D(LoanMoneyView? money) => D(money!.Amount);

    [Fact]
    public void The_household_figures_are_the_overview_s_and_the_price_per_m2_counts_the_sized_properties()
    {
        var valuation = Build(Home(), Flat(), Studio(), Land());

        Assert.Equal((4, 3, 0, 1), (valuation.Count, valuation.UpToDateCount, valuation.StaleCount, valuation.WithoutValueCount));
        Assert.Equal(700_000m, D(valuation.Value));
        Assert.Equal(392_000m + 186_300m + 85_000m, D(valuation.CostPrice));
        Assert.Equal(D(valuation.Value) - D(valuation.CostPrice), D(valuation.LatentGain));
        // D16: the home and the flat (the studio has no surface, the land no estimate), rounded to the euro.
        Assert.Equal(("127.00", 4_803m, 1), (valuation.SurfaceM2, D(valuation.ValuePerM2), valuation.WithoutSurfaceCount));
        Assert.Equal(["Lyon 7e", "T2 Villeurbanne", "Studio", "Terrain"], valuation.Properties.Select(p => p.Name));
    }

    [Fact]
    public void A_row_carries_its_page_s_value_and_cost_price_its_price_per_m2_and_its_annual_pace()
    {
        var home = Build(Home()).Properties[0];

        Assert.Equal(("412000.00", "392000.00", "20000.00", 5.1m), (home.Value, home.CostPrice, home.LatentGain, home.LatentGainPct));
        Assert.Equal(("82.00", "5024.00", "yearly"), (home.SurfaceM2, home.ValuePerM2, home.Cadence));
        // D17: (412 000 ÷ 392 000)^(365.25 ÷ 2 750) − 1, by hand: 0.66 %.
        Assert.Equal(0.7m, home.AnnualPacePct);
        Assert.Null(Build(Studio()).Properties[0].AnnualPacePct);
        Assert.Null(Build(Land()).Properties[0].AnnualPacePct);
    }

    [Fact]
    public void The_history_lists_the_estimates_and_each_change_of_the_cost_price()
    {
        var home = Build(Home()).Properties[0];

        Assert.Equal([(new LocalDate(2024, 9, 30), "402000.00"), (new LocalDate(2026, 9, 30), "412000.00")], home.Estimates.Select(e => (e.On, e.Value)));
        // D18: the purchase, then each day of works (two works the same day are one step).
        Assert.Equal(
            [(new LocalDate(2019, 4, 12), "381500.00"), (new LocalDate(2020, 5, 1), "389500.00"), (new LocalDate(2023, 6, 1), "392000.00")],
            home.CostSteps.Select(s => (s.On, s.CostPrice)));
    }

    [Fact]
    public void A_property_in_another_currency_without_a_rate_leaves_the_totals_unknown()
    {
        var chalet = Item("Chalet", "CHF");
        var valuation = Build(Home(), Property(chalet, PropertyType.SecondaryResidence, 180_000m, 0m, new LocalDate(2020, 1, 1), 70m,
            [Estimate(chalet, new LocalDate(2026, 1, 1), 200_000m)]));

        Assert.Null(valuation.Value);
        Assert.Null(valuation.ValuePerM2);
        Assert.Contains(valuation.Findings, f => f.Code == FxCodes.RateMissing);
        Assert.Equal("200000.00", valuation.Properties.Single(p => p.Name == "Chalet").Value);
    }

    [Fact]
    public void Without_a_property_there_is_no_figure()
    {
        var valuation = Build();
        Assert.Null(valuation.Value);
        Assert.Null(valuation.SurfaceM2);
        Assert.Empty(valuation.Properties);
    }
}
