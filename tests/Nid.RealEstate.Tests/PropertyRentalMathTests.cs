using System.Globalization;
using Nid.Core.Contracts;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;
using Duration = Nid.Core.Contracts.Duration;

namespace Nid.RealEstate.Tests;

/// <summary>Spec 3C2 §3–§5: a property's rental over W = (d − 12 months, d], and its planned costs' figures.</summary>
public class PropertyRentalMathTests
{
    private static readonly LocalDate D = new(2025, 10, 15);
    private static readonly ItemId Home = ItemId.New();

    /// <summary>README §12: 250 000 + 19 000 of notary fees (and the 12 000 of works below: a cost price of 281 000), a rental.</summary>
    private static PropertyDetails Bought(LocalDate? rentalStartsOn = null, PropertyType type = PropertyType.Rental)
        => new(type, new LocalDate(2020, 6, 15), 250_000m, 19_000m, 0m, 0m, null, null, PropertyCadence.Yearly, null, rentalStartsOn, null);

    private static Money Eur(decimal amount) => Loans.Ok(Money.Create(amount, Currency.Eur));

    private static Flow Rent(LocalDate on, decimal amount = 900m)
        => new(Guid.NewGuid(), Home, on, FlowKind.Income, Eur(amount), PropertyCategories.Rent, null, null);

    private static Flow Expense(LocalDate on, string category, decimal amount)
        => new(Guid.NewGuid(), Home, on, FlowKind.Expense, Eur(amount), category, null, null);

    private static Flow Works => Expense(new LocalDate(2021, 3, 1), PropertyCategories.Works, 12_000m);

    private static Tenancy Lease(LocalDate starts, LocalDate? ends = null, decimal rent = 900m, decimal charges = 0m)
        => new("T3", starts, ends, rent, charges, null, null);

    /// <summary>Twelve rents, on the 5th of each month from November 2024 to October 2025: all inside W.</summary>
    private static IEnumerable<Flow> AYearOfRent() => Enumerable.Range(0, 12).Select(m => Rent(new LocalDate(2024, 11, 5).PlusMonths(m)));

    /// <summary>No estimate and no loan: only the rental matters here; the works always count in the cost price.</summary>
    private static PropertyFigures At(PropertyDetails property, IEnumerable<Flow> flows, params Tenancy[] tenancies)
        => PropertyMath.At(property, true, [], [Works, .. flows], [], Loans.Chain(tenancies), D);

    private static RecurringCost Cost(decimal amount, int count, DurationUnit unit, LocalDate starts, LocalDate? ends = null)
        => new("property_tax", amount, Loans.Ok(Duration.Create(count, unit)), starts, ends, null);

    [Fact]
    public void Readme_section_12s_rent_gives_a_gross_yield_of_0_0384()
    {
        var r = At(Bought(), AYearOfRent(), Lease(new LocalDate(2020, 9, 1))).Rental;
        Assert.Equal(new LocalDate(2020, 9, 1), r.RentedSince);
        Assert.Equal(10_800m, r.ExpectedRent); // 464.52 (16 days of October 2024) + 11 × 900 + 435.48 (15 days of October 2025)
        Assert.Equal(10_800m, r.RentReceived);
        // The rent ledger counts from the first rent, in November 2024, to the end of October 2025, paid on the 5th.
        Assert.Equal(0m, r.Arrears);
        Assert.Equal(0, r.VacantDays);
        Assert.Equal(0m, r.VacancyRate);
        Assert.Equal(0.0384m, decimal.Round(r.GrossYield!.Value, 4)); // 10 800 ÷ 281 000
        Assert.Equal(r.GrossYield, r.NetYieldBeforeTax); // no expense in W: the 2021 works never count here
    }

    [Fact]
    public void A_tenancys_charges_recorded_apart_still_count_as_received()
    {
        var months = Enumerable.Range(0, 12).Select(m => new LocalDate(2024, 11, 5).PlusMonths(m)).ToList();
        var flows = months.SelectMany(on => new[] { Rent(on, 800m),
            new Flow(Guid.NewGuid(), Home, on, FlowKind.Income, Eur(50m), PropertyCategories.RecoverableCharges, null, null) });
        var r = At(Bought(), flows, Lease(new LocalDate(2020, 9, 1), rent: 800m, charges: 50m)).Rental;
        Assert.Equal(0m, r.Arrears);
        Assert.Equal(10_200m, r.RentReceived);
    }

    [Fact]
    public void The_net_yield_deducts_the_expenses_of_W_but_its_works()
    {
        var r = At(Bought(), [.. AYearOfRent(), Expense(new LocalDate(2025, 9, 20), "property_tax", 1_500m),
            Expense(new LocalDate(2025, 5, 1), PropertyCategories.Works, 4_000m)], Lease(new LocalDate(2020, 9, 1))).Rental;
        // Works dated in W raise the cost price to 285 000 and are not deducted again.
        Assert.Equal(0.0379m, decimal.Round(r.GrossYield!.Value, 4)); // 10 800 ÷ 285 000
        Assert.Equal(0.0326m, decimal.Round(r.NetYieldBeforeTax!.Value, 4)); // (10 800 − 1 500) ÷ 285 000
    }

    [Fact]
    public void A_partial_month_is_prorated_to_its_days_half_up_to_the_cent()
    {
        // 0.15 for one day of a 30-day September is 0.005: half up gives 0.01, never 0.00.
        Assert.Equal(0.01m, At(Bought(new LocalDate(2020, 9, 1)), [],
            Lease(new LocalDate(2025, 9, 30), new LocalDate(2025, 9, 30), rent: 0.15m)).Rental.ExpectedRent);
        // From 10 June, 600 + 50: 21 days of 30 (455.00), July to September (1 950.00), 15 days of 31 in October (314.52).
        Assert.Equal(2_719.52m, At(Bought(), [], Lease(new LocalDate(2025, 6, 10), rent: 600m, charges: 50m)).Rental.ExpectedRent);
    }

    [Fact]
    public void Vacancy_counts_the_days_between_two_tenancies_and_a_shared_month_is_prorated_for_each() // Review Focus #4
    {
        var r = At(Bought(), [], Lease(new LocalDate(2020, 9, 1), new LocalDate(2025, 3, 9)), Lease(new LocalDate(2025, 3, 20))).Rental;
        Assert.Equal(10, r.VacantDays); // 10 to 19 March 2025
        Assert.Equal(0.0274m, decimal.Round(r.VacancyRate!.Value, 4)); // 10 ÷ 365
        // March: 9 days of the first (261.29) and 12 days of the second (348.39), each prorated and rounded on its own.
        Assert.Equal(10_509.68m, r.ExpectedRent);
        // Nothing received: the ledger's last 12 months, October 2025 due whole (16 more days of the second lease, 464.52).
        Assert.Equal(10_974.20m, r.Arrears);
    }

    [Fact]
    public void Back_to_back_tenancies_leave_no_vacant_day_and_their_months_are_due_whole()
    {
        // The first ends on 31 March, its last day let; the second starts on 1 April.
        var r = At(Bought(), [], Lease(new LocalDate(2020, 9, 1), new LocalDate(2025, 3, 31)), Lease(new LocalDate(2025, 4, 1))).Rental;
        Assert.Equal(0, r.VacantDays);
        Assert.Equal(10_800m, r.ExpectedRent); // 12 × 900
    }

    [Fact]
    public void Yields_are_not_significant_before_12_months_of_rental_and_vacancy_starts_at_R()
    {
        var r = At(Bought(new LocalDate(2025, 1, 1)), [Rent(new LocalDate(2025, 2, 5))], Lease(new LocalDate(2025, 2, 1))).Rental;
        Assert.Equal(new LocalDate(2025, 1, 1), r.RentedSince);
        Assert.Equal(31, r.VacantDays); // January 2025, of the 288 days from 2025-01-01 to d
        Assert.Equal(0.1076m, decimal.Round(r.VacancyRate!.Value, 4));
        Assert.Null(r.GrossYield);
        Assert.Null(r.NetYieldBeforeTax);
    }

    [Theory]
    [InlineData(2024, 10, 15, true)]  // R = d − 12 months: significant
    [InlineData(2024, 10, 16, false)] // a day later: not
    public void Yields_are_significant_from_a_rental_start_on_or_before_d_minus_12_months(int year, int month, int day, bool significant)
    {
        var r = At(Bought(new LocalDate(year, month, day)), AYearOfRent()).Rental;
        Assert.Equal(significant, r.GrossYield is not null);
        Assert.Equal(significant, r.NetYieldBeforeTax is not null);
    }

    [Fact]
    public void W_leaves_out_a_rent_dated_d_minus_12_months_and_keeps_one_dated_d() // Review Focus #3
    {
        var r = At(Bought(new LocalDate(2020, 9, 1)),
            [Rent(new LocalDate(2024, 10, 15), 100m), Rent(new LocalDate(2024, 10, 16), 10m), Rent(D, 1m)]).Rental;
        Assert.Equal(11m, r.RentReceived);
    }

    [Fact]
    public void A_leap_window_counts_366_days() // Review Focus #3
    {
        // d = 2024-03-01: W runs from 2023-03-02 to 2024-03-01, 29 February included; nothing is let.
        var r = PropertyMath.At(Bought(new LocalDate(2020, 9, 1)), true, [], [], [], [], new LocalDate(2024, 3, 1)).Rental;
        Assert.Equal(366, r.VacantDays);
        Assert.Equal(1m, r.VacancyRate);
    }

    [Theory]
    [InlineData(11, "900")]  // a month unpaid
    [InlineData(13, "-900")] // a month paid twice: negative, shown as is
    public void Arrears_are_the_ledger_s_due_since_the_first_rent_less_all_the_cash(int rents, string arrears)
    {
        // The first rent is in November 2024: the ledger owes November to October 2025, the current month whole.
        var flows = Enumerable.Range(0, rents).Select(i => Rent(new LocalDate(2024, 11, 5).PlusDays(i)));
        var r = At(Bought(), flows, Lease(new LocalDate(2020, 9, 1))).Rental;
        Assert.Equal(10_800m, r.ExpectedRent); // W's figure is unchanged
        Assert.Equal(decimal.Parse(arrears, CultureInfo.InvariantCulture), r.Arrears);
    }

    [Fact]
    public void A_tenancy_starting_before_the_rental_start_moves_R_and_none_leaves_it_null()
    {
        Assert.Equal(new LocalDate(2022, 6, 1), At(Bought(new LocalDate(2023, 1, 1)), [], Lease(new LocalDate(2022, 6, 1))).Rental.RentedSince);

        var none = At(Bought(), []).Rental;
        Assert.Null(none.RentedSince);
        Assert.Null(none.VacantDays);
        Assert.Null(none.VacancyRate);
        Assert.Null(none.GrossYield);
        Assert.Null(none.NetYieldBeforeTax);
    }

    [Fact]
    public void Vacancy_is_null_while_the_rental_has_not_started()
    {
        var r = At(Bought(), [], Lease(D.PlusDays(1))).Rental;
        Assert.Equal(D.PlusDays(1), r.RentedSince);
        Assert.Null(r.VacantDays);
        Assert.Null(r.VacancyRate);
        Assert.Equal(0m, r.ExpectedRent);
    }

    [Fact]
    public void A_rental_with_no_tenancy_and_no_rental_start_is_warned()
    {
        static IEnumerable<string> Codes(PropertyFigures f) => f.Warnings.Select(w => w.Code);
        Assert.Contains(PropertyWarnings.RentalWithoutTenancy, Codes(At(Bought(), [])));
        Assert.DoesNotContain(PropertyWarnings.RentalWithoutTenancy, Codes(At(Bought(new LocalDate(2020, 9, 1)), [])));
        Assert.DoesNotContain(PropertyWarnings.RentalWithoutTenancy, Codes(At(Bought(), [], Lease(new LocalDate(2020, 9, 1)))));
        Assert.DoesNotContain(PropertyWarnings.RentalWithoutTenancy, Codes(At(Bought(type: PropertyType.PrimaryResidence), [])));
    }

    [Theory]
    [InlineData(1_200, 1, DurationUnit.Year, "100.00")]
    [InlineData(250, 3, DurationUnit.Month, "83.33")]
    [InlineData(1_000, 2, DurationUnit.Year, "41.67")]
    public void The_monthly_equivalent_spreads_the_amount_over_its_months_to_the_cent(int amount, int count, DurationUnit unit, string expected)
        => Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture),
            PropertyMath.MonthlyEquivalent(Cost(amount, count, unit, new LocalDate(2021, 1, 1))));

    [Fact]
    public void The_next_due_date_is_the_first_occurrence_on_or_after_d_and_before_the_end()
    {
        Assert.Equal(D, PropertyMath.NextDueOn(Cost(1_200m, 1, DurationUnit.Year, new LocalDate(2021, 10, 15)), D)); // due today
        Assert.Equal(new LocalDate(2026, 1, 1), PropertyMath.NextDueOn(Cost(100m, 1, DurationUnit.Month, new LocalDate(2026, 1, 1)), D)); // starts later
        Assert.Equal(new LocalDate(2026, 1, 1), PropertyMath.NextDueOn(Cost(250m, 3, DurationUnit.Month, new LocalDate(2025, 1, 1)), D));
        Assert.Null(PropertyMath.NextDueOn(Cost(100m, 1, DurationUnit.Month, new LocalDate(2024, 1, 1), new LocalDate(2025, 6, 1)), D)); // ended
        // The end is exclusive: the occurrence falling on it is not due.
        Assert.Null(PropertyMath.NextDueOn(Cost(100m, 1, DurationUnit.Month, new LocalDate(2025, 1, 15), new LocalDate(2025, 11, 15)), D.PlusDays(1)));
        // A 31st anchor: every month from 31 January is 28 February, then 31 March — never the 28th again.
        Assert.Equal(new LocalDate(2025, 3, 31),
            PropertyMath.NextDueOn(Cost(100m, 1, DurationUnit.Month, new LocalDate(2025, 1, 31)), new LocalDate(2025, 3, 1)));
    }

    [Fact]
    public void A_tenancy_covers_its_start_and_its_end()
    {
        var lease = Lease(new LocalDate(2025, 1, 1), D);
        Assert.True(PropertyMath.Covers(lease, new LocalDate(2025, 1, 1)));
        Assert.True(PropertyMath.Covers(lease, D));
        Assert.False(PropertyMath.Covers(lease, D.PlusDays(1)));
        Assert.True(PropertyMath.Covers(Lease(new LocalDate(2025, 1, 1)), D));
        Assert.False(PropertyMath.Covers(Lease(D.PlusDays(1)), D));
    }
}
