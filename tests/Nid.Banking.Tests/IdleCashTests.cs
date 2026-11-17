using Nid.Banking.Contracts;
using Nid.Banking.Domain;
using NodaTime;

namespace Nid.Banking.Tests;

/// <summary>Issue #81: idle cash, the checking accounts' total above the buffer every day of the last 28 (today: 2026-03-10).</summary>
public class IdleCashTests
{
    private static readonly LocalDate Today = new(2026, 3, 10);
    private static readonly LocalDate First = Today.PlusDays(-27);

    private static BankBalance On(LocalDate day, decimal amount) => new(Guid.Empty, day, amount, "CLBD", Instant.FromUtc(2026, 3, 9, 6, 0));

    private static decimal? Lowest(params IReadOnlyList<BankBalance>[] accounts) => IdleCash.LowestTotal(Today, accounts);

    [Fact]
    public void The_lowest_total_spans_the_28_days_up_to_today_each_day_holding_the_previous_balance()
    {
        // Held from before the window, a dip on its first day, then a day without a balance holding the dip's.
        Assert.Equal(4_000m, Lowest([On(First.PlusDays(-5), 6_000m), On(First, 4_000m), On(First.PlusDays(10), 9_000m)]));
        // The day before the window does not count.
        Assert.Equal(6_000m, Lowest([On(First.PlusDays(-1), 1_000m), On(First, 6_000m)]));
        // Two accounts add up day by day: the lowest total is not the sum of each one's lowest.
        Assert.Equal(7_000m, Lowest(
            [On(First, 5_000m), On(Today.PlusDays(-3), 2_000m)],
            [On(First, 2_000m), On(Today.PlusDays(-10), 5_000m)]));
    }

    [Fact]
    public void Nothing_is_said_without_a_balance_on_the_first_day_of_the_window()
    {
        Assert.Null(Lowest());
        Assert.Null(Lowest([On(First.PlusDays(1), 9_000m)])); // the first day is unknown
        Assert.Null(Lowest([On(First, 9_000m)], [])); // a second account without any balance
    }

    [Fact]
    public void Every_day_above_the_buffer_is_told_and_one_day_at_it_is_not()
    {
        Assert.Equal((1_000.50m, IdleCash.Room), IdleCash.Find(7_000.50m, 6_000m, 200m, false));
        Assert.Null(IdleCash.Find(6_000m, 6_000m, 200m, false));
        // A single day below drags the lowest total down with it.
        var dip = Lowest([On(First, 9_000m), On(Today.PlusDays(-4), 5_999m), On(Today.PlusDays(-3), 9_000m)]);
        Assert.Null(IdleCash.Find(dip!.Value, 6_000m, 200m, false));
    }

    [Fact]
    public void It_is_told_only_when_a_livret_has_room_or_a_goal_is_behind()
    {
        Assert.Null(IdleCash.Find(9_000m, 6_000m, 0m, false));
        Assert.Equal(IdleCash.GoalBehind, IdleCash.Find(9_000m, 6_000m, 0m, true)?.Reason);
        Assert.Equal(IdleCash.RoomAndGoalBehind, IdleCash.Find(9_000m, 6_000m, 0.01m, true)?.Reason);
    }
}
