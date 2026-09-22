using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Core.Contracts;

public enum DurationUnit
{
    Day,
    Week,
    Month,
    Year,
}

/// <summary>
/// A length of time as <c>{count, unit}</c>, never converted to days. Occurrences are computed from an
/// anchor date (<c>anchor + k × duration</c>), so a 31 January anchor gives 29 February, 31 March, 30 April.
/// </summary>
public readonly record struct Duration
{
    private Duration(int count, DurationUnit unit)
    {
        Count = count;
        Unit = unit;
    }

    public int Count { get; }

    public DurationUnit Unit { get; }

    public static Result<Duration> Create(int count, DurationUnit unit)
        => count > 0
            ? Result.Success(new Duration(count, unit))
            : Result.Failure<Duration>(new Failure("core.duration_count", "A duration has a positive count.", null));

    /// <summary>Date of the <paramref name="occurrence"/>-th renewal after <paramref name="anchor"/> (0 = the anchor).</summary>
    public LocalDate AddTo(LocalDate anchor, int occurrence)
    {
        var total = checked(Count * occurrence);
        return Unit switch
        {
            DurationUnit.Day => anchor.PlusDays(total),
            DurationUnit.Week => anchor.PlusWeeks(total),
            DurationUnit.Month => anchor.PlusMonths(total),
            DurationUnit.Year => anchor.PlusYears(total),
            _ => throw new ArgumentOutOfRangeException(nameof(Unit)),
        };
    }
}
