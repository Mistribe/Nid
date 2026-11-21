using NodaTime;

namespace Nid.Banking.Domain;

/// <summary>
/// Issue #305: a category whose month runs away. Amounts are the spending view's, in the reference currency, positive for money spent,
/// over <c>today</c>'s month.
/// </summary>
public static class UnusualSpending
{
    public const string AboveUsual = "usual";
    public const string BudgetOver = "budget_over";
    public const string BudgetPace = "budget_pace";

    /// <summary>Before this day a month's few lines say little: neither the usual month nor the pace is compared.</summary>
    public const int FromDay = 10;

    // ponytail: a fixed floor in the reference currency (50 € for a euro household); make it follow the currency if a household needs it.
    public const decimal MinExcess = 50m;

    /// <summary>
    /// What a budget's month spends at the current pace: the budget's expected path ends at <paramref name="planned"/>, and what is spent
    /// beyond it so far (<paramref name="expectedSoFar"/>, committed charges whole from their due day) keeps growing at the same rate.
    /// </summary>
    public static decimal Projected(decimal planned, decimal expectedSoFar, decimal spent, LocalDate today)
        => planned + (spent - expectedSoFar) * today.With(DateAdjusters.EndOfMonth).Day / today.Day;

    /// <summary>
    /// Why the category stands out, null when it does not: its budget is over (any day); from <see cref="FromDay"/>, its budget will be
    /// over by more than <see cref="MinExcess"/> at the current pace, or it spent more than <paramref name="percent"/> % and
    /// <see cref="MinExcess"/> above its <paramref name="usual"/> month.
    /// </summary>
    public static string? Find(LocalDate today, decimal percent, decimal spent, decimal? usual, decimal? planned, decimal? expectedSoFar)
    {
        if (planned is { } budget && spent > budget)
            return BudgetOver;
        if (today.Day < FromDay)
            return null;
        if (planned is { } p && expectedSoFar is { } expected && Projected(p, expected, spent, today) - p > MinExcess)
            return BudgetPace;
        return usual is { } u && spent - u > MinExcess && spent > u * (1 + percent / 100) ? AboveUsual : null;
    }
}
