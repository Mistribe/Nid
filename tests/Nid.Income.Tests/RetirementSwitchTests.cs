using Nid.Core.Contracts;
using Nid.Income.Contracts;
using Nid.Income.Domain;
using NodaTime;
using Duration = Nid.Core.Contracts.Duration;

namespace Nid.Income.Tests;

/// <summary>Retirement spec §2: work income stops and a future pension moves to the departure.</summary>
public class RetirementSwitchTests
{
    private static readonly Currency Eur = Currency.TryCreate("EUR", out var c) ? c : throw new InvalidOperationException();
    private static readonly ItemId Source = new(Guid.CreateVersion7());
    private static LocalDate D(int y, int m, int d) => new(y, m, d);
    private static IncomePhase P(LocalDate start)
        => new(Guid.CreateVersion7(), Source, "Retraite", start, null, Money.Create(1000m, Eur).TryGetValue(out var m) ? m : throw new InvalidOperationException(),
            Duration.Create(1, DurationUnit.Month).TryGetValue(out var d) ? d : throw new InvalidOperationException(), null, null, null);

    [Theory]
    [InlineData(IncomeKind.Salary, true)]
    [InlineData(IncomeKind.SelfEmployment, true)]
    [InlineData(IncomeKind.Unemployment, true)]
    [InlineData(IncomeKind.Pension, false)]
    [InlineData(IncomeKind.FamilyAllowance, false)]
    [InlineData(IncomeKind.SocialBenefit, false)]
    [InlineData(IncomeKind.Alimony, false)]
    [InlineData(IncomeKind.Other, false)]
    public void Work_kinds_stop(IncomeKind kind, bool stops) => Assert.Equal(stops, RetirementSwitch.Stops(kind));

    [Fact]
    public void A_future_pension_moves_to_the_departure()
    {
        var moved = RetirementSwitch.MovePension([P(D(2030, 1, 1)), P(D(2031, 1, 1))], D(2026, 11, 10), D(2029, 6, 1));

        Assert.Equal([D(2029, 6, 1), D(2030, 6, 1)], moved.Select(p => p.StartsOn));
    }

    [Fact]
    public void A_running_pension_is_untouched()
    {
        var phases = new[] { P(D(2020, 1, 1)) };

        Assert.Equal(phases, RetirementSwitch.MovePension(phases, D(2026, 11, 10), D(2029, 6, 1)));
    }
}
