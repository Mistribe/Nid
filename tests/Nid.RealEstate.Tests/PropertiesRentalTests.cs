using System.Globalization;
using Nid.Core.Contracts;
using Nid.RealEstate.Application;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;

namespace Nid.RealEstate.Tests;

/// <summary>Properties dashboard spec §4.4: the rental tab's figures on a fixture household, with the real engine.</summary>
public class PropertiesRentalTests
{
    private static readonly LocalDate Today = new(2026, 10, 22);
    private static readonly PersonId Alice = PersonId.New();

    private static ItemAccess Item(string name, string currency = "EUR")
        => new(ItemId.New(), name, Visibility.Shared, [new Owner(Alice, PersonKind.Adult, 10_000)]) { Currency = currency };

    private static Flow Fact(ItemAccess item, LocalDate on, FlowKind kind, string category, decimal amount)
        => new(Guid.NewGuid(), item.Id, on, kind, Loans.Ok(Money.Create(amount, Currency.Eur)), category, null, null);

    private static StoredTenancy Lease(LocalDate starts, decimal rent, LocalDate? ends = null)
        => new(Guid.NewGuid(), new Tenancy("Bail", starts, ends, rent, 0m, null, null), 1);

    /// <summary>150 000 at 3 % over 240 payments, on the 15th from February 2024 (the test loans' dates).</summary>
    private static readonly LoanTerms FlatLoanTerms = Loans.Annuity(150_000m, 0.03m, 240);

    private static readonly ItemAccess FlatItem = Item("T2 Villeurbanne");
    private static readonly ItemAccess FlatLoanItem = Item("Prêt T2");

    /// <summary>
    /// Let since March 2024 at 780 a month, twelve rents from November 2025, its property tax (1 120) and works (1 800) in the
    /// period, and its loan: 186 300 all in, plus the works.
    /// </summary>
    private static OverviewProperty Flat() => new(FlatItem,
        new StoredProperty(FlatItem.Id, new PropertyDetails(PropertyType.Rental, new LocalDate(2023, 12, 5), 173_000m, 13_300m, 0m, 0m, null, null,
            PropertyCadence.Yearly, null, null, null), 1, [], [Lease(new LocalDate(2024, 3, 1), 780m)]),
        [], [
            .. Enumerable.Range(0, 12).Select(m => Fact(FlatItem, new LocalDate(2025, 11, 5).PlusMonths(m), FlowKind.Income, PropertyCategories.Rent, 780m)),
            Fact(FlatItem, new LocalDate(2025, 10, 25), FlowKind.Expense, "property_tax", 1_120m),
            Fact(FlatItem, new LocalDate(2026, 3, 10), FlowKind.Expense, PropertyCategories.Works, 1_800m),
            Fact(FlatItem, new LocalDate(2025, 10, 1), FlowKind.Expense, "insurance", 500m), // before the period
        ],
        [(Loans.Stored(terms: FlatLoanTerms), FlatLoanItem)], []);

    private static readonly ItemAccess StudioItem = Item("Studio");

    /// <summary>Let from 1 May 2026 but leased from the 15th (14 days vacant), at 450: not let twelve months, its yield is not significant.</summary>
    private static OverviewProperty Studio() => new(StudioItem,
        new StoredProperty(StudioItem.Id, new PropertyDetails(PropertyType.Rental, new LocalDate(2021, 6, 18), 80_000m, 6_400m, 0m, 0m, null, null,
            PropertyCadence.Yearly, null, new LocalDate(2026, 5, 1), null), 1, [], [Lease(new LocalDate(2026, 5, 15), 450m, new LocalDate(2027, 5, 15))]),
        [], Enumerable.Range(0, 6).Select(m => Fact(StudioItem, new LocalDate(2026, 5, 20).PlusMonths(m), FlowKind.Income, PropertyCategories.Rent, 450m)).ToList(),
        [], []);

    private static readonly ItemAccess HomeItem = Item("Résidence");

    /// <summary>Never let: not on the tab.</summary>
    private static OverviewProperty Home() => new(HomeItem,
        new StoredProperty(HomeItem.Id, new PropertyDetails(PropertyType.PrimaryResidence, new LocalDate(2019, 4, 12), 355_000m, 26_500m, 0m, 0m, null, null,
            PropertyCadence.Yearly, null, null, null), 1, [], []), [], [], [], []);

    private static PropertiesRental Build(ItemId? only = null) => PropertiesRentalMath.Build([Flat(), Studio(), Home()], Currency.Eur, RateTable.Empty(Today, 0), Today, only)!;

    private static decimal D(string amount) => decimal.Parse(amount, CultureInfo.InvariantCulture);

    private static decimal D(LoanMoneyView? money) => D(money!.Amount);

    /// <summary>The flat's loan payments due in the period: the 15th of November 2025 to the 15th of October 2026.</summary>
    private static decimal FlatPayments()
    {
        var stored = Loans.Stored(terms: FlatLoanTerms);
        return LoanReading.Schedule(FlatLoanTerms, stored).Rows
            .Where(r => r.DueDate > Today.PlusMonths(-12) && r.DueDate <= Today).Sum(r => r.Payment + r.Insurance);
    }

    [Fact]
    public void The_period_is_the_calendar_months_of_the_twelve_months_to_today_and_they_add_up()
    {
        var rental = Build();

        Assert.Equal((new LocalDate(2025, 10, 23), Today, 2), (rental.From, rental.To, rental.Count));
        Assert.Equal(13, rental.Months.Count);
        Assert.Equal(("2025-10", "2026-10"), (rental.Months[0].Month, rental.Months[^1].Month));
        Assert.Equal(9_360m + 2_700m, D(rental.RentReceived));
        Assert.Equal(rental.Months.Sum(m => D(m.RentReceived)), D(rental.RentReceived));
        Assert.Equal(rental.Months.Sum(m => D(m.Charges)), D(rental.Charges));
        Assert.Equal(rental.Months.Sum(m => D(m.LoanPayments)), D(rental.LoanPayments));
        Assert.Equal(rental.Months.Sum(m => D(m.CashFlow)), D(rental.CashFlow));
        Assert.Equal(1_120m, D(rental.Months[0].Charges)); // the insurance of 1 October is before the period
        Assert.Equal(Math.Round(D(rental.CashFlow) / 12m, 2, MidpointRounding.AwayFromZero), D(rental.CashFlowMonthly));
    }

    [Fact]
    public void The_cash_flow_counts_works_and_loan_payments_and_the_net_yield_counts_neither()
    {
        var rental = Build();

        var flat = rental.Properties.Single(p => p.Name == "T2 Villeurbanne");
        Assert.Equal(("9360.00", "2920.00"), (flat.RentReceived, flat.Charges));
        Assert.Equal(FlatPayments(), D(flat.LoanPayments!));
        Assert.Equal(9_360m - 2_920m - FlatPayments(), D(flat.CashFlow!));
        Assert.Equal(D(flat.CashFlow!) + D(rental.Properties.Single(p => p.Name == "Studio").CashFlow!), D(rental.CashFlow));
        // D9: on the flat alone (the studio's yield is not significant), over its cost price with the works.
        Assert.Equal((9_360m / 188_100m).ToString("0.0000", CultureInfo.InvariantCulture), rental.GrossYield);
        Assert.Equal(((9_360m - 1_120m) / 188_100m).ToString("0.0000", CultureInfo.InvariantCulture), rental.NetYieldBeforeTax);
        Assert.Equal(flat.GrossYield, rental.GrossYield);
        Assert.Equal(1, rental.NotSignificantCount);
    }

    [Fact]
    public void The_vacancy_adds_up_the_vacant_days_over_each_property_s_days()
    {
        var rental = Build();

        Assert.Equal(14, rental.VacantDays);
        Assert.Equal(((decimal)14 / (365 + 175)).ToString("0.0000", CultureInfo.InvariantCulture), rental.VacancyRate);
        var studio = rental.Properties.Single(p => p.Name == "Studio");
        Assert.Equal(("Bail", "450.00"), (studio.CurrentTenancy!.Label, studio.RentMonthly));
        Assert.Null(studio.GrossYield);
    }

    [Fact]
    public void Expected_rent_and_arrears_are_the_properties_own()
    {
        var rental = Build();

        Assert.Equal(rental.Properties.Sum(p => D(p.ExpectedRent)), D(rental.ExpectedRent));
        Assert.Equal(rental.Properties.Sum(p => D(p.Arrears)), D(rental.Arrears));
        Assert.Equal(["T2 Villeurbanne", "Studio"], rental.Properties.Select(p => p.Name));
        Assert.Equal(2, rental.Tenancies.Count);
    }

    [Fact]
    public void One_property_keeps_its_own_figures_and_another_is_not_found()
    {
        var studio = Build(StudioItem.Id);
        Assert.Equal(1, studio.Count);
        Assert.Equal(2_700m, D(studio.RentReceived));
        Assert.Equal("0.00", studio.LoanPayments!.Amount);

        Assert.Null(PropertiesRentalMath.Build([Flat(), Studio(), Home()], Currency.Eur, RateTable.Empty(Today, 0), Today, HomeItem.Id));
    }

    [Fact]
    public void Without_a_let_property_the_tab_has_nothing()
    {
        var rental = PropertiesRentalMath.Build([Home()], Currency.Eur, RateTable.Empty(Today, 0), Today)!;
        Assert.Equal(0, rental.Count);
        Assert.Null(rental.RentReceived);
        Assert.Null(rental.VacancyRate);
        Assert.Empty(rental.Properties);
    }
}
