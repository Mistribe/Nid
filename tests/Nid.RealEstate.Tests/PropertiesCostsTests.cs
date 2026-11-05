using System.Globalization;
using Nid.Core.Contracts;
using Nid.RealEstate.Application;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;
using Duration = Nid.Core.Contracts.Duration;

namespace Nid.RealEstate.Tests;

/// <summary>Properties dashboard spec §5.4: the costs tab's figures on a fixture household, with the real engine.</summary>
public class PropertiesCostsTests
{
    private static readonly LocalDate Today = new(2026, 10, 22);
    private static readonly PersonId Alice = PersonId.New();

    private static ItemAccess Item(string name, string currency = "EUR")
        => new(ItemId.New(), name, Visibility.Shared, [new Owner(Alice, PersonKind.Adult, 10_000)]) { Currency = currency };

    private static Flow Fact(ItemAccess item, LocalDate on, FlowKind kind, string category, decimal amount, string currency = "EUR")
        => new(Guid.NewGuid(), item.Id, on, kind, Loans.Ok(Money.Create(amount, Currency.TryCreate(currency, out var c) ? c : Currency.Eur)), category, null, null);

    private static StoredRecurringCost Cost(string category, decimal amount, int count, DurationUnit unit, LocalDate starts, LocalDate? ends = null)
        => new(Guid.NewGuid(), new RecurringCost(category, amount, Loans.Ok(Duration.Create(count, unit)), starts, ends, null), 1);

    private static PropertyDetails Bought(PropertyType type, decimal price, decimal notary, LocalDate on)
        => new(type, on, price, notary, 0m, 0m, null, null, PropertyCadence.Yearly, null, null, null);

    private static readonly LoanTerms FlatLoanTerms = Loans.Annuity(150_000m, 0.03m, 240);
    private static readonly ItemAccess FlatItem = Item("T2 Villeurbanne");

    /// <summary>
    /// Its property tax and works paid in the period (an insurance just before it), twelve rents, a yearly tax and a quarterly condo
    /// fee planned, an insurance ended in June, and a loan due on the 15th.
    /// </summary>
    private static OverviewProperty Flat() => new(FlatItem,
        new StoredProperty(FlatItem.Id, Bought(PropertyType.Rental, 173_000m, 13_300m, new LocalDate(2023, 12, 5)), 1,
            [
                Cost("property_tax", 1_120m, 1, DurationUnit.Year, new LocalDate(2024, 11, 15)),
                Cost("condo_fees", 300m, 3, DurationUnit.Month, new LocalDate(2024, 11, 1)),
                Cost("insurance", 180m, 1, DurationUnit.Year, new LocalDate(2023, 1, 1), new LocalDate(2026, 6, 1)),
            ], []),
        [], [
            .. Enumerable.Range(0, 12).Select(m => Fact(FlatItem, new LocalDate(2025, 11, 5).PlusMonths(m), FlowKind.Income, PropertyCategories.Rent, 780m)),
            Fact(FlatItem, new LocalDate(2025, 10, 25), FlowKind.Expense, "property_tax", 1_120m),
            Fact(FlatItem, new LocalDate(2026, 3, 10), FlowKind.Expense, PropertyCategories.Works, 1_800m),
            Fact(FlatItem, new LocalDate(2025, 10, 1), FlowKind.Expense, "insurance", 500m),
        ],
        [(Loans.Stored(terms: FlatLoanTerms), Item("Prêt T2"))], []);

    private static readonly ItemAccess HomeItem = Item("Résidence");

    private static OverviewProperty Home() => new(HomeItem,
        new StoredProperty(HomeItem.Id, Bought(PropertyType.PrimaryResidence, 355_000m, 26_500m, new LocalDate(2019, 4, 12)), 1, [], []),
        [], [Fact(HomeItem, new LocalDate(2026, 1, 10), FlowKind.Expense, "maintenance", 200m)], [], []);

    private static readonly ItemAccess ChaletItem = Item("Chalet", "CHF");

    private static OverviewProperty Chalet() => new(ChaletItem,
        new StoredProperty(ChaletItem.Id, Bought(PropertyType.SecondaryResidence, 180_000m, 0m, new LocalDate(2020, 1, 1)), 1, [], []),
        [], [Fact(ChaletItem, new LocalDate(2026, 2, 1), FlowKind.Expense, "energy", 900m, "CHF")], [], []);

    private static PropertiesCosts Build(params OverviewProperty[] set) => PropertiesCostsMath.Build(set, Currency.Eur, RateTable.Empty(Today, 0), Today, []);

    private static decimal D(string amount) => decimal.Parse(amount, CultureInfo.InvariantCulture);

    private static decimal D(LoanMoneyView? money) => D(money!.Amount);

    [Fact]
    public void Paid_is_the_period_s_expenses_and_planned_the_next_twelve_months_occurrences()
    {
        var costs = Build(Flat(), Home());

        Assert.Equal((new LocalDate(2025, 10, 23), Today), (costs.From, costs.To));
        Assert.Equal((3_120m, 1_800m), (D(costs.Paid), D(costs.WorksPaid)));
        // The tax on 15 November, the condo fee on 1 November, February, May and August; the insurance has ended.
        Assert.Equal(1_120m + 4 * 300m, D(costs.Planned));
        Assert.Equal(
            [(new LocalDate(2026, 11, 1), "condo_fees"), (new LocalDate(2026, 11, 15), "property_tax"), (new LocalDate(2027, 2, 1), "condo_fees"),
             (new LocalDate(2027, 5, 1), "condo_fees"), (new LocalDate(2027, 8, 1), "condo_fees")],
            costs.Dues.Select(d => (d.On, d.Category)));
        Assert.Equal(("300.00", "EUR"), (costs.Dues[0].Amount, costs.Dues[0].Currency));
        // D14: the dashboard's monthly equivalents of the costs running today: 1 120 ÷ 12 and 300 ÷ 3.
        Assert.Equal(93.33m + 100m, D(costs.PlannedMonthly));
    }

    [Fact]
    public void The_categories_add_up_to_paid_and_planned()
    {
        var costs = Build(Flat(), Home());

        Assert.Equal(["works", "property_tax", "maintenance", "condo_fees"], costs.ByCategory.Select(c => c.Category));
        Assert.Equal(D(costs.Paid), costs.ByCategory.Where(c => c.Paid is not null).Sum(c => D(c.Paid)));
        Assert.Equal(D(costs.Planned), costs.ByCategory.Where(c => c.Planned is not null).Sum(c => D(c.Planned)));
        Assert.Null(costs.ByCategory.Single(c => c.Category == "works").Planned);
    }

    [Fact]
    public void A_row_carries_its_real_cost_as_the_property_page_in_six_columns()
    {
        var costs = Build(Flat(), Home());

        var row = costs.Properties.Single(p => p.Name == "T2 Villeurbanne");
        var figures = PropertiesOverviewMath.FiguresOf(Flat(), RateTable.Empty(Today, 0), Today);
        Assert.Equal(figures.RealCost, D(row.RealCost));
        Assert.Equal(D(row.RealCost), new[] { row.Acquisition, row.Works, row.LoanInterest, row.LoanInsurance, row.LoanFees, row.OtherCharges }.Sum(D));
        Assert.Equal(("186300.00", "1800.00", "2920.00", "9360.00"), (row.Acquisition, row.Works, row.Paid, row.RentReceived));
        Assert.Equal(("9360.00", (figures.RealCost - 9_360m).ToString("0.00", CultureInfo.InvariantCulture)), (row.RentsSincePurchase, row.NetCostOfRents));
        Assert.Equal("193.33", row.PlannedMonthly);
        var october = LoanReading.Schedule(FlatLoanTerms, Loans.Stored(terms: FlatLoanTerms)).Rows.First(r => r.DueDate.ToYearMonth() == Today.ToYearMonth());
        Assert.Equal(october.Payment + october.Insurance, D(row.LoanPayment!));
        Assert.Equal("0.00", costs.Properties.Single(p => p.Name == "Résidence").LoanPayment);
        Assert.Equal(costs.Properties.Sum(p => D(p.RealCost)), D(costs.RealCost));
        Assert.Equal(costs.Properties.Sum(p => D(p.NetCostOfRents)), D(costs.NetCostOfRents));
        // By real cost, largest first.
        Assert.Equal(["Résidence", "T2 Villeurbanne"], costs.Properties.Select(p => p.Name));
    }

    [Fact]
    public void A_property_in_another_currency_without_a_rate_leaves_the_totals_unknown_and_names_it()
    {
        var costs = Build(Flat(), Chalet());

        Assert.Null(costs.Paid);
        Assert.Null(costs.RealCost);
        Assert.Contains(costs.Findings, f => f.Code == FxCodes.RateMissing && f.Reference!.StartsWith("CHF", StringComparison.Ordinal));
        Assert.Equal("900.00", costs.Properties.Single(p => p.Name == "Chalet").Paid);
    }

    [Fact]
    public void Without_a_cost_nothing_is_paid_nor_planned()
    {
        var costs = Build();
        Assert.Null(costs.Paid);
        Assert.Null(costs.WorksPaid);
        Assert.Null(costs.Planned);
        Assert.Empty(costs.Dues);
    }

    [Fact]
    public void Due_dates_stop_at_the_end_of_the_cost_and_of_the_window()
    {
        var cost = new RecurringCost("condo_fees", 300m, Loans.Ok(Duration.Create(3, DurationUnit.Month)), new LocalDate(2024, 11, 1), new LocalDate(2027, 3, 1), null);
        Assert.Equal([new LocalDate(2026, 11, 1), new LocalDate(2027, 2, 1)], PropertyMath.DueBetween(cost, Today, Today.PlusMonths(12)));
    }

    [Fact]
    public void Paid_per_category_and_calendar_year_with_the_change_between_two_complete_years()
    {
        var home = Home();
        home = home with
        {
            Flows =
            [
                .. home.Flows,
                Fact(HomeItem, new LocalDate(2021, 10, 1), FlowKind.Expense, "property_tax", 900m), // before the five years
                Fact(HomeItem, new LocalDate(2023, 10, 1), FlowKind.Expense, "property_tax", 1_000m),
                Fact(HomeItem, new LocalDate(2024, 10, 1), FlowKind.Expense, "property_tax", 1_100m),
                Fact(HomeItem, new LocalDate(2025, 4, 1), FlowKind.Expense, "property_tax", 600m),
                Fact(HomeItem, new LocalDate(2025, 10, 1), FlowKind.Expense, "property_tax", 610m),
                Fact(HomeItem, new LocalDate(2026, 10, 1), FlowKind.Expense, "property_tax", 1_300m),
            ],
        };

        var years = Build(home).ByYear;

        Assert.Equal([2022, 2023, 2024, 2025, 2026], years.Years.Select(y => y.Year));
        Assert.Equal([true, true, true, true, false], years.Years.Select(y => y.Complete));
        Assert.Equal(["property_tax", "maintenance"], years.Categories.Select(c => c.Category));
        var tax = years.Categories[0].Cells;
        Assert.Equal([null, "1000.00", "1100.00", "1210.00", "1300.00"], tax.Select(c => c.Paid?.Amount));
        // No change from a year without payment, nor into the current year.
        Assert.Equal([null, null, "0.1000", "0.1000", null], tax.Select(c => c.Change));
    }

    [Fact]
    public void A_year_a_property_was_bought_in_or_before_is_partial()
    {
        var flat = Flat();
        flat = flat with
        {
            Flows =
            [
                Fact(FlatItem, new LocalDate(2023, 12, 20), FlowKind.Expense, "insurance", 20m),
                Fact(FlatItem, new LocalDate(2024, 6, 1), FlowKind.Expense, "insurance", 180m),
                Fact(FlatItem, new LocalDate(2025, 6, 1), FlowKind.Expense, "insurance", 171m),
            ],
        };

        var years = Build(flat, Home()).ByYear;

        // Bought in December 2023: 2022 did not have it, 2023 had it for a month.
        Assert.Equal([false, false, true, true, false], years.Years.Select(y => y.Complete));
        var insurance = Assert.Single(years.Categories, c => c.Category == "insurance").Cells;
        Assert.Equal([null, null, null, "-0.0500", null], insurance.Select(c => c.Change));
    }

    [Fact]
    public void A_property_bought_on_1_january_makes_its_year_complete_and_the_one_before_partial()
    {
        var home = Home();
        home = home with
        {
            Property = home.Property with { Details = home.Property.Details with { PurchaseDate = new LocalDate(2024, 1, 1) } },
            Flows =
            [
                Fact(HomeItem, new LocalDate(2023, 10, 1), FlowKind.Expense, "property_tax", 1_000m),
                Fact(HomeItem, new LocalDate(2024, 10, 1), FlowKind.Expense, "property_tax", 1_100m),
                Fact(HomeItem, new LocalDate(2025, 10, 1), FlowKind.Expense, "property_tax", 1_210m),
            ],
        };

        var years = Build(home).ByYear;

        Assert.Equal([false, false, true, true, false], years.Years.Select(y => y.Complete));
        Assert.Equal([null, null, null, "0.1000", null], Assert.Single(years.Categories).Cells.Select(c => c.Change));
    }

    [Fact]
    public void A_year_an_archived_property_was_held_is_partial()
    {
        var home = Home();
        home = home with
        {
            Flows =
            [
                Fact(HomeItem, new LocalDate(2022, 10, 1), FlowKind.Expense, "property_tax", 1_000m),
                Fact(HomeItem, new LocalDate(2023, 10, 1), FlowKind.Expense, "property_tax", 1_100m),
                Fact(HomeItem, new LocalDate(2024, 10, 1), FlowKind.Expense, "property_tax", 1_210m),
                Fact(HomeItem, new LocalDate(2025, 10, 1), FlowKind.Expense, "property_tax", 1_331m),
            ],
        };

        // Spec D7: an archived (sold) property is left out of the costs; its charges are missing from each year it was held,
        // bought in 2020 and archived on 1 March 2023.
        var years = PropertiesCostsMath.Build([home], Currency.Eur, RateTable.Empty(Today, 0), Today, [(new LocalDate(2020, 6, 1), new LocalDate(2023, 3, 1))]).ByYear;

        Assert.Equal([false, false, true, true, false], years.Years.Select(y => y.Complete));
        Assert.Equal([null, null, null, "0.1000", null], Assert.Single(years.Categories).Cells.Select(c => c.Change));
        // Archived before the first year shown: every past year is whole again.
        var earlier = PropertiesCostsMath.Build([home], Currency.Eur, RateTable.Empty(Today, 0), Today, [(new LocalDate(2015, 6, 1), new LocalDate(2021, 12, 31))]).ByYear;
        Assert.Equal([true, true, true, true, false], earlier.Years.Select(y => y.Complete));
    }

    [Fact]
    public void A_missing_rate_leaves_the_year_unknown()
    {
        var years = Build(Chalet()).ByYear;

        var energy = Assert.Single(years.Categories).Cells;
        Assert.Null(energy[4].Paid);
        Assert.Null(energy[3].Paid);
    }
}
