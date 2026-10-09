using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using Nid.Investments.Domain;
using NodaTime;

namespace Nid.Investments.Tests;

public class RoutineMathTests
{
    private static readonly ItemId Item = ItemId.New();
    private static LocalDate D(int y, int m, int d) => new(y, m, d);
    private static Money Eur(decimal amount) => Money.Create(amount, Currency.Eur).TryGetValue(out var m) ? m : throw new InvalidOperationException();
    private static Flow Flow(LocalDate on, FlowKind kind, decimal amount) => new(Guid.NewGuid(), Item, on, kind, Eur(amount), null, null, null);

    [Theory]
    [InlineData(2025, 10, 15, 2025, 9, 30)]
    [InlineData(2025, 1, 3, 2024, 12, 31)]
    [InlineData(2024, 3, 1, 2024, 2, 29)]
    public void The_default_date_is_the_end_of_the_previous_month(int y, int m, int d, int ey, int em, int ed)
        => Assert.Equal(D(ey, em, ed), RoutineMath.DefaultDate(D(y, m, d)));

    [Fact]
    public void IsDue_follows_the_cadence_period()
    {
        LocalDate[] july = [D(2025, 7, 10)];
        Assert.True(RoutineMath.IsDue(ValuationCadence.Monthly, july, D(2025, 9, 30)));
        Assert.False(RoutineMath.IsDue(ValuationCadence.Monthly, [D(2025, 9, 1)], D(2025, 9, 30)));
        Assert.False(RoutineMath.IsDue(ValuationCadence.Quarterly, july, D(2025, 9, 30)));
        Assert.True(RoutineMath.IsDue(ValuationCadence.Quarterly, july, D(2025, 10, 31)));
        Assert.False(RoutineMath.IsDue(ValuationCadence.Yearly, july, D(2025, 12, 31)));
        Assert.True(RoutineMath.IsDue(ValuationCadence.Yearly, july, D(2026, 1, 31)));
        Assert.False(RoutineMath.IsDue(ValuationCadence.Manual, [], D(2025, 9, 30)));
        Assert.True(RoutineMath.IsDue(ValuationCadence.Monthly, [], D(2025, 9, 30))); // never valued
    }

    [Fact]
    public void Net_contributions_count_after_the_previous_value_up_to_the_routine_date()
    {
        Flow[] flows =
        [
            Flow(D(2025, 8, 31), FlowKind.Contribution, 100m), // on the previous value's date: already in it
            Flow(D(2025, 9, 10), FlowKind.Contribution, 50m),
            Flow(D(2025, 9, 20), FlowKind.Withdrawal, 20m),
            Flow(D(2025, 9, 25), FlowKind.Income, 5m), // not a contribution
            Flow(D(2025, 10, 1), FlowKind.Contribution, 70m), // after the routine date
        ];
        Assert.Equal(30m, RoutineMath.NetContributions(flows, D(2025, 8, 31), D(2025, 9, 30)));
        Assert.Equal(130m, RoutineMath.NetContributions(flows, null, D(2025, 9, 30)));
    }
}
