using Nid.Core.Contracts;
using Nid.Investments.Application;
using Nid.Investments.Contracts;
using Nid.Investments.Domain;
using NodaTime;

namespace Nid.Investments.Tests;

/// <summary>#184: what an employee savings plan releases, and when.</summary>
public class EnvelopeAvailabilityTests
{
    private static readonly LocalDate Today = new(2026, 6, 15);

    private static readonly AvailabilityTranche[] Tranches =
    [
        new(new LocalDate(2025, 1, 1), 2000m), // released
        new(new LocalDate(2026, 6, 15), 1000m), // released today
        new(new LocalDate(2028, 1, 1), 3000m),
        new(new LocalDate(2027, 1, 1), 4000m),
    ];

    [Fact]
    public void Without_tranches_nothing_is_known_and_the_plan_stays_locked()
    {
        Assert.Null(EnvelopeAvailability.At(null, 10_000m, Today));
        Assert.Null(EnvelopeAvailability.At([], 10_000m, Today));
    }

    [Fact]
    public void The_tranches_as_printed_split_the_value_when_it_did_not_move()
    {
        var at = EnvelopeAvailability.At(Tranches, 10_000m, Today)!;
        Assert.Equal(3000m, at.AvailableToday);
        Assert.Equal([new Release(new LocalDate(2027, 1, 1), 4000m), new Release(new LocalDate(2028, 1, 1), 3000m)], at.Releases);
    }

    [Fact]
    public void A_value_that_moved_since_the_statement_is_split_pro_rata_and_the_parts_sum_to_it()
    {
        var at = EnvelopeAvailability.At(Tranches, 10_001m, Today)!;
        Assert.Equal([4000.40m, 3000.30m], at.Releases.Select(r => r.Amount));
        Assert.Equal(3000.30m, at.AvailableToday);
        Assert.Equal(10_001m, at.AvailableToday + at.Releases.Sum(r => r.Amount));
    }

    [Fact]
    public void Once_every_date_is_past_the_whole_value_is_available()
    {
        var at = EnvelopeAvailability.At(Tranches, 12_000m, new LocalDate(2028, 1, 1))!;
        Assert.Equal(12_000m, at.AvailableToday);
        Assert.Empty(at.Releases);
    }

    [Fact]
    public void The_allocation_moves_what_is_released_out_of_locked_and_dates_the_rest()
    {
        var slices = EnvelopeAllocation.Release(EnvelopeAllocation.Of(EnvelopeKind.EmployeeSavings, 10_000m, null, new Dictionary<Guid, EnvelopeHolding>()), Tranches, Today);
        Assert.Equal(
        [
            new AllocationSlice("mixed", "days", 3000m, true),
            new AllocationSlice("mixed", "locked", 4000m, true, new LocalDate(2027, 1, 1)),
            new AllocationSlice("mixed", "locked", 3000m, true, new LocalDate(2028, 1, 1)),
        ], slices);
    }

    [Fact]
    public void Without_tranches_the_allocation_keeps_the_plan_locked()
    {
        var slices = EnvelopeAllocation.Of(EnvelopeKind.EmployeeSavings, 10_000m, null, new Dictionary<Guid, EnvelopeHolding>());
        Assert.Same(slices, EnvelopeAllocation.Release(slices, null, Today));
    }

    // #466: an early-release case.
    private static EarlyRelease Case(LocalDate on, decimal? amount = null) => new("home_purchase", on, amount);

    [Fact]
    public void A_past_case_without_an_amount_frees_the_whole_plan()
    {
        var at = EnvelopeAvailability.At(Tranches, 10_000m, Today, Case(new LocalDate(2026, 3, 1)))!;
        Assert.Equal((10_000m, 7000m), (at.AvailableToday, at.EarlyReleased));
        Assert.Empty(at.Releases);
    }

    [Fact]
    public void A_past_case_with_an_amount_takes_it_from_the_earliest_releases()
    {
        var at = EnvelopeAvailability.At(Tranches, 10_000m, Today, Case(new LocalDate(2026, 3, 1), 5000m))!;
        Assert.Equal((8000m, 5000m), (at.AvailableToday, at.EarlyReleased));
        Assert.Equal([new Release(new LocalDate(2028, 1, 1), 2000m)], at.Releases);
    }

    [Fact]
    public void A_future_case_becomes_a_release_on_its_date_from_what_comes_after_it()
    {
        var at = EnvelopeAvailability.At(Tranches, 10_000m, Today, Case(new LocalDate(2027, 6, 1)))!;
        Assert.Equal(3000m, at.AvailableToday);
        Assert.Equal([new Release(new LocalDate(2027, 1, 1), 4000m), new Release(new LocalDate(2027, 6, 1), 3000m)], at.Releases);
    }

    [Fact]
    public void Without_tranches_a_case_frees_its_amount_and_the_rest_stays_locked_with_no_date()
    {
        var at = EnvelopeAvailability.At(null, 10_000m, Today, Case(new LocalDate(2026, 3, 1), 2000m))!;
        Assert.Equal((2000m, 8000m, 2000m), (at.AvailableToday, at.Locked, at.EarlyReleased));
        // No more than the plan holds.
        Assert.Equal(10_000m, EnvelopeAvailability.At(null, 10_000m, Today, Case(new LocalDate(2026, 3, 1), 50_000m))!.AvailableToday);
    }

    [Fact]
    public void The_allocation_counts_what_a_case_frees_as_available_on_request()
    {
        var slices = EnvelopeAllocation.Release(EnvelopeAllocation.Of(EnvelopeKind.EmployeeSavings, 10_000m, null, new Dictionary<Guid, EnvelopeHolding>()),
            null, Today, Case(new LocalDate(2026, 3, 1), 2000m));
        Assert.Equal([new AllocationSlice("mixed", "days", 2000m, true), new AllocationSlice("mixed", "locked", 8000m, true)], slices);
    }
}
