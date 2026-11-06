using Nid.Core.Contracts;
using Nid.Income.Contracts;
using Nid.Income.Domain;
using NodaTime;
using Duration = Nid.Core.Contracts.Duration;

namespace Nid.Income.Tests;

/// <summary>Projection spec §3.2, §5.1: the monthly equivalent on a future date, a phase ending without successor being extended.</summary>
public class IncomeTimelineTests
{
    private static readonly Currency Eur = Currency.TryCreate("EUR", out var c) ? c : throw new InvalidOperationException();
    private static Money M(decimal amount) => Money.Create(amount, Eur).TryGetValue(out var m) ? m : throw new InvalidOperationException();
    private static Duration Months(int n) => Duration.Create(n, DurationUnit.Month).TryGetValue(out var d) ? d : throw new InvalidOperationException();
    private static readonly ItemId Source = new(Guid.CreateVersion7());
    private static IncomePhase P(string label, LocalDate start, LocalDate? end, decimal net)
        => new(Guid.CreateVersion7(), Source, label, start, end, M(net), Months(1), null, null, null);
    private static LocalDate D(int y, int m, int d) => new(y, m, d);

    private static readonly LocalDate Today = D(2026, 10, 15);

    [Fact]
    public void Future_phase_counts_from_its_start()
    {
        var phases = new[] { P("Salaire", D(2025, 1, 1), D(2027, 1, 1), 2000m), P("Salaire", D(2027, 1, 1), null, 2500m) };

        Assert.Equal((2000m, false), IncomeMath.MonthlyEquivalentExtended(phases, Today, D(2026, 12, 31), sourceClosed: false));
        Assert.Equal((2500m, false), IncomeMath.MonthlyEquivalentExtended(phases, Today, D(2027, 1, 31), sourceClosed: false));
    }

    [Fact]
    public void Phase_ending_without_successor_is_extended()
    {
        var phases = new[] { P("CDD", D(2025, 1, 1), D(2027, 3, 1), 2000m) };

        Assert.Equal((2000m, false), IncomeMath.MonthlyEquivalentExtended(phases, Today, D(2027, 2, 28), sourceClosed: false));
        Assert.Equal((2000m, true), IncomeMath.MonthlyEquivalentExtended(phases, Today, D(2027, 6, 30), sourceClosed: false));
    }

    [Fact]
    public void Closed_source_stops()
    {
        var phases = new[] { P("CDD", D(2025, 1, 1), D(2027, 3, 1), 2000m) };

        Assert.Equal((0m, false), IncomeMath.MonthlyEquivalentExtended(phases, Today, D(2027, 6, 30), sourceClosed: true));
    }

    [Fact]
    public void A_phase_already_ended_before_today_is_not_revived()
    {
        var phases = new[] { P("Ancien", D(2024, 1, 1), D(2025, 1, 1), 900m) };

        Assert.Equal((0m, false), IncomeMath.MonthlyEquivalentExtended(phases, Today, D(2027, 6, 30), sourceClosed: false));
    }

    [Theory]
    [InlineData("CDD")]
    [InlineData("CDI")]
    public void An_extended_phase_stops_where_the_next_phase_of_the_source_starts(string successorLabel)
    {
        var phases = new[] { P("CDD", D(2025, 1, 1), D(2027, 3, 1), 2000m), P(successorLabel, D(2027, 6, 1), null, 2200m) };

        Assert.Equal((2000m, true), IncomeMath.MonthlyEquivalentExtended(phases, Today, D(2027, 4, 30), sourceClosed: false));
        Assert.Equal((2200m, false), IncomeMath.MonthlyEquivalentExtended(phases, Today, D(2027, 7, 31), sourceClosed: false));
    }

    [Fact]
    public void A_short_phase_after_the_end_stops_the_extension_only_while_it_runs()
    {
        var phases = new[] { P("Salaire", D(2025, 1, 1), D(2027, 1, 1), 2000m), P("Prime 2027", D(2027, 1, 1), D(2027, 2, 1), 1000m) };

        Assert.Equal((1000m, false), IncomeMath.MonthlyEquivalentExtended(phases, Today, D(2027, 1, 31), sourceClosed: false));
        // From February, nothing runs after the salary's end: the salary is extended again; the one-shot bonus never is.
        Assert.Equal((2000m, true), IncomeMath.MonthlyEquivalentExtended(phases, Today, D(2027, 2, 28), sourceClosed: false));
    }

    [Fact]
    public void An_ended_later_phase_replaces_the_extension_of_the_earlier_one()
    {
        var phases = new[] { P("CDD", D(2025, 1, 1), D(2027, 3, 1), 2000m), P("CDI", D(2027, 6, 1), D(2028, 12, 1), 2200m) };

        Assert.Equal((2000m, true), IncomeMath.MonthlyEquivalentExtended(phases, Today, D(2027, 4, 30), sourceClosed: false));
        Assert.Equal((2200m, false), IncomeMath.MonthlyEquivalentExtended(phases, Today, D(2027, 7, 31), sourceClosed: false));
        Assert.Equal((2200m, true), IncomeMath.MonthlyEquivalentExtended(phases, Today, D(2029, 1, 31), sourceClosed: false));
    }

    [Fact]
    public void A_phase_starting_after_today_is_extended_after_its_end()
    {
        var phases = new[] { P("Futur", D(2027, 1, 1), D(2027, 3, 1), 2000m) };

        Assert.Equal((0m, false), IncomeMath.MonthlyEquivalentExtended(phases, Today, D(2026, 12, 31), sourceClosed: false));
        Assert.Equal((2000m, true), IncomeMath.MonthlyEquivalentExtended(phases, Today, D(2027, 6, 30), sourceClosed: false));
    }

    [Fact]
    public void Parts_sum_to_MonthlyEquivalentExtended_and_flag_the_extended_phase()
    {
        var phases = new[] { P("CDD", D(2025, 1, 1), D(2027, 1, 1), 2000m), P("Prime", D(2025, 6, 1), null, 300m) };
        foreach (var on in new[] { D(2026, 10, 15), D(2027, 3, 31), D(2028, 6, 30) })
        {
            var parts = IncomeMath.MonthlyEquivalentExtendedParts(phases, Today, on, sourceClosed: false);
            var (amount, extended) = IncomeMath.MonthlyEquivalentExtended(phases, Today, on, sourceClosed: false);
            Assert.Equal(amount, parts.Sum(p => p.Amount));
            Assert.Equal(extended, parts.Any(p => p.Extended));
        }
        Assert.Contains(IncomeMath.MonthlyEquivalentExtendedParts(phases, Today, D(2028, 6, 30), false), p => p.Phase.Label == "CDD" && p.Extended);
    }
}
