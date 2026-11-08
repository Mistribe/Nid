using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Core.Contracts;

/// <summary>Half-open interval <c>[Start, EndBefore)</c>. <c>EndBefore</c> is the first day NOT covered; null = open.</summary>
public readonly record struct DateRange
{
    private DateRange(LocalDate start, LocalDate? endBefore)
    {
        Start = start;
        EndBefore = endBefore;
    }

    public LocalDate Start { get; }

    public LocalDate? EndBefore { get; }

    public static Result<DateRange> Create(LocalDate start, LocalDate? endBefore)
        => endBefore is { } end && end <= start
            ? Result.Failure<DateRange>(new Failure("core.range_empty", "The end must be after the start.", null))
            : Result.Success(new DateRange(start, endBefore));

    public bool Contains(LocalDate date) => date >= Start && (EndBefore is not { } end || date < end);

    public bool Overlaps(DateRange other)
        => (EndBefore is not { } end || other.Start < end)
           && (other.EndBefore is not { } otherEnd || Start < otherEnd);
}
