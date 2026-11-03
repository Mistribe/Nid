using Nid.Core.Contracts;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;

namespace Nid.RealEstate.Tests;

/// <summary>Passive income spec C7: a month's rent (cashed) and costs (only while let, charges offset, works left out).</summary>
public class PropertyIncomeMathTests
{
    private static readonly LocalDate Month = new(2025, 10, 1);
    private static readonly ItemId Home = ItemId.New();

    private static Flow F(FlowKind kind, string category, decimal amount)
        => new(Guid.NewGuid(), Home, new LocalDate(2025, 10, 5), kind, Loans.Ok(Money.Create(amount, Currency.Eur)), category, null, null);

    private static readonly Flow[] Flows =
    [
        F(FlowKind.Income, PropertyCategories.Rent, 800m), F(FlowKind.Expense, "property_tax", 1200m), F(FlowKind.Expense, "condo_fees", 150m),
        F(FlowKind.Expense, PropertyCategories.Works, 5000m), F(FlowKind.Income, PropertyCategories.RecoverableCharges, 50m),
    ];

    private static Tenancy Lease(LocalDate starts, LocalDate? ends = null) => new("T3", starts, ends, 800m, 0m, null, null);

    [Fact]
    public void A_let_month_counts_costs_net_of_recoverable_charges_and_without_works()
        => Assert.Equal(new PropertyIncomeMonth(Month, 800m, 1300m), PropertyMath.IncomeIn(Month, Flows, [Lease(new LocalDate(2025, 1, 1))]));

    [Fact]
    public void A_month_without_a_tenancy_keeps_a_late_rent_and_no_costs()
        => Assert.Equal(new PropertyIncomeMonth(Month, 800m, 0m), PropertyMath.IncomeIn(Month, Flows, [Lease(new LocalDate(2024, 1, 1), new LocalDate(2025, 9, 29))]));

    [Fact]
    public void A_tenancy_ending_on_the_third_covers_the_month()
        => Assert.Equal(1300m, PropertyMath.IncomeIn(Month, Flows, [Lease(new LocalDate(2024, 1, 1), new LocalDate(2025, 10, 2))]).Costs);

    [Fact]
    public void A_tenancy_starting_later_in_the_month_covers_it()
        => Assert.Equal(1300m, PropertyMath.IncomeIn(Month, Flows, [Lease(new LocalDate(2025, 10, 31))]).Costs);
}
