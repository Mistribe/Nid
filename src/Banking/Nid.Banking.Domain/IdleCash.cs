using Nid.Banking.Contracts;
using NodaTime;

namespace Nid.Banking.Domain;

/// <summary>Issue #81: idle cash, the checking accounts' total balance above the buffer every day of the last <see cref="Days"/>.</summary>
public static class IdleCash
{
    public const int Days = 28;

    public const string Room = "room";
    public const string GoalBehind = "goal_behind";
    public const string RoomAndGoalBehind = "room_goal_behind";

    /// <summary>
    /// The lowest total of the window's days up to <paramref name="today"/>, a day without a balance holding the account's previous one;
    /// null without accounts, or when one has no balance on or before the window's first day (28 days cannot be told).
    /// </summary>
    public static decimal? LowestTotal(LocalDate today, IReadOnlyCollection<IReadOnlyList<BankBalance>> accounts)
    {
        if (accounts.Count == 0)
            return null;
        decimal? lowest = null;
        for (var day = today.PlusDays(1 - Days); day <= today; day = day.PlusDays(1))
        {
            var total = 0m;
            foreach (var balances in accounts)
            {
                if (balances.LastOrDefault(b => b.AsOf <= day) is not { } held)
                    return null;
                total += held.Amount;
            }
            lowest = Math.Min(lowest ?? total, total);
        }
        return lowest;
    }

    /// <summary>What held besides the cash above the buffer, null when neither did: a livret with room, an open goal behind.</summary>
    public static string? Reason(decimal room, bool goalBehind) => (room > 0m, goalBehind) switch
    {
        (true, true) => RoomAndGoalBehind,
        (true, false) => Room,
        (false, true) => GoalBehind,
        _ => null,
    };

    /// <summary>The amount the lowest total was above the buffer, and why it is told; null when it was not above it, or nothing else held.</summary>
    public static (decimal Above, string Reason)? Find(decimal lowest, decimal buffer, decimal room, bool goalBehind)
        => lowest > buffer && Reason(room, goalBehind) is { } reason ? (lowest - buffer, reason) : null;
}
