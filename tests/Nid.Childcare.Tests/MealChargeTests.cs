using Nid.Childcare.Contracts;
using Nid.Childcare.Domain;
using NodaTime;

namespace Nid.Childcare.Tests;

/// <summary>#18: lunch and snack each at their own price, only the nanny's paid.</summary>
public class MealChargeTests
{
    private static readonly LocalTime In = new(8, 0), Out = new(17, 0);

    private static readonly IReadOnlyList<StatementDay> Days =
    [
        new(new LocalDate(2026, 9, 1), true, true, In, Out, true, null, true),
        new(new LocalDate(2026, 9, 2), true, true, In, Out, true, null),
        new(new LocalDate(2026, 9, 3), true, false, null, null, true, AbsenceReason.Family, true),
    ];

    private static ContractTerms Terms(MealProvider lunch, MealProvider snack)
        => new(new LocalDate(2026, 9, 1), null, 4m, 0.1m, 4m, 3.50m, 1.25m, lunch, snack);

    [Fact]
    public void Present_days_pay_each_meal_at_its_own_price()
    {
        Assert.Equal((7.00m, 1.25m), Upkeep.MealCharges(Days, Terms(MealProvider.Nanny, MealProvider.Nanny)));
        Assert.Equal(8.25m, Upkeep.Month(Days, Terms(MealProvider.Nanny, MealProvider.Nanny)).Meals);
    }

    [Theory]
    [InlineData(MealProvider.Employer, MealProvider.Nanny, 1.25)]
    [InlineData(MealProvider.Nanny, MealProvider.Employer, 7.00)]
    [InlineData(MealProvider.Employer, MealProvider.Employer, 0)]
    public void A_meal_the_employer_provides_adds_nothing(MealProvider lunch, MealProvider snack, decimal expected)
        => Assert.Equal(expected, Upkeep.Month(Days, Terms(lunch, snack)).Meals);

    [Fact]
    public void Terms_from_before_the_snack_keep_their_charge()
        => Assert.Equal(7.00m, Upkeep.Month(Days, new ContractTerms(new LocalDate(2026, 9, 1), null, 4m, 0.1m, 4m, 3.50m)).Meals);
}
