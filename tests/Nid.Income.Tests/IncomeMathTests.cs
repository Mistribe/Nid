using Nid.Core.Contracts;
using Nid.Income.Contracts;
using Nid.Income.Domain;
using NodaTime;
using Duration = Nid.Core.Contracts.Duration;

namespace Nid.Income.Tests;

public class IncomeMathTests
{
    private static readonly Currency Eur = Currency.TryCreate("EUR", out var c) ? c : throw new InvalidOperationException();
    private static Money M(decimal amount) => Money.Create(amount, Eur).TryGetValue(out var m) ? m : throw new InvalidOperationException();
    private static Duration Months(int n) => Duration.Create(n, DurationUnit.Month).TryGetValue(out var d) ? d : throw new InvalidOperationException();
    private static Duration Year => Duration.Create(1, DurationUnit.Year).TryGetValue(out var d) ? d : throw new InvalidOperationException();
    private static readonly ItemId Source = new(Guid.CreateVersion7());
    private static IncomePhase P(string label, LocalDate start, LocalDate? end, decimal net, Duration every, int? max = null)
        => new(Guid.CreateVersion7(), Source, label, start, end, M(net), every, null, max, null);
    private static LocalDate D(int y, int m, int d) => new(y, m, d);

    [Fact]
    public void The_golden_case_of_the_data_model_is_4480_a_month()
    {
        var on = D(2025, 10, 1);
        var phases = new[]
        {
            P("Salaire de base", D(2025, 9, 1), null, 2300m, Months(1)),
            P("Prime annuelle", D(2024, 12, 15), null, 1800m, Year),
            P("Salaire de base 2", D(2024, 1, 1), null, 1900m, Months(1)),
            P("Allocations", D(2024, 1, 1), null, 130m, Months(1)),
        };
        Assert.Equal(4480m, IncomeMath.MonthlyEquivalent(phases, on));
    }

    [Theory]
    [InlineData(1, 1200, 1200)]
    [InlineData(3, 1200, 400)]
    public void A_monthly_cadence_divides_by_its_count(int months, int net, int perMonth)
        => Assert.Equal(perMonth, IncomeMath.MonthlyEquivalent(P("x", D(2025, 1, 1), null, net, Months(months))));

    [Fact]
    public void A_yearly_phase_counts_a_twelfth()
        => Assert.Equal(150m, IncomeMath.MonthlyEquivalent(P("x", D(2025, 1, 1), null, 1800m, Year)));

    [Fact]
    public void Uneven_quarters_keep_full_precision_and_round_on_the_wire() // Review Focus #3
    {
        var phase = P("x", D(2025, 1, 1), null, 1000m, Months(3));
        var each = IncomeMath.MonthlyEquivalent(phase);
        Assert.Equal(333.33m, IncomeMath.Round(each));
        Assert.Equal(1000.00m, IncomeMath.Round(each * 3));
        Assert.Equal(0.01m, IncomeMath.Round(0.005m)); // half up on a positive amount
    }

    [Fact]
    public void An_anchor_on_the_31st_lands_on_each_months_last_day_without_drifting() // Review Focus #1
    {
        var phase = P("x", D(2024, 1, 31), null, 100m, Months(1));
        var dates = IncomeMath.Occurrences(phase, D(2024, 1, 1), D(2024, 6, 1)).Select(o => o.On).ToList();
        Assert.Equal([D(2024, 1, 31), D(2024, 2, 29), D(2024, 3, 31), D(2024, 4, 30), D(2024, 5, 31)], dates);
    }

    [Fact]
    public void Occurrences_stop_before_the_end_and_after_max_cycles()
    {
        var ended = P("x", D(2025, 1, 15), D(2025, 4, 15), 100m, Months(1));
        Assert.Equal([D(2025, 1, 15), D(2025, 2, 15), D(2025, 3, 15)], IncomeMath.Occurrences(ended, D(2025, 1, 1), D(2026, 1, 1)).Select(o => o.On));
        var capped = P("x", D(2025, 1, 15), null, 100m, Months(1), max: 2);
        Assert.Equal(2, IncomeMath.Occurrences(capped, D(2025, 1, 1), D(2026, 1, 1)).Count());
    }

    [Fact]
    public void A_raise_moves_the_equivalent_on_its_date()
    {
        var before = P("Salaire", D(2024, 9, 1), D(2025, 9, 1), 2200m, Months(1));
        var after = P("Salaire", D(2025, 9, 1), null, 2300m, Months(1));
        Assert.Equal(2200m, IncomeMath.MonthlyEquivalent([before, after], D(2025, 8, 31)));
        Assert.Equal(2300m, IncomeMath.MonthlyEquivalent([before, after], D(2025, 9, 1)));
    }

    [Fact]
    public void Expected_income_groups_occurrences_and_one_offs_by_month()
    {
        var salary = P("Salaire", D(2025, 1, 28), null, 2000m, Months(1));
        var bonus = new Flow(Guid.CreateVersion7(), Source, D(2025, 2, 10), FlowKind.Income, M(500m), IncomeWire.OneOffCategory, null, null);
        var months = IncomeMath.Expected([salary], [bonus], D(2025, 1, 1), D(2025, 2, 28), Eur);
        Assert.Equal([(2025, 1, 2000m), (2025, 2, 2500m)], months.Select(m => (m.Year, m.Month, m.Total.Amount)));
    }

    [Fact]
    public void A_gap_is_warned_within_60_days_unless_a_phase_follows()
    {
        var today = D(2025, 10, 1);
        var ending = P("CDD", D(2025, 1, 1), D(2025, 11, 30), 1500m, Months(1));
        Assert.Equal([new GapWarning("CDD", D(2025, 11, 30))], IncomeMath.Gaps([ending], today));
        var next = P("CDD", D(2025, 11, 30), null, 1600m, Months(1));
        Assert.Empty(IncomeMath.Gaps([ending, next], today));
        var far = P("Loin", D(2025, 1, 1), today.PlusDays(61), 1500m, Months(1));
        Assert.Empty(IncomeMath.Gaps([far], today));
        var otherLabel = P("Autre", D(2025, 11, 30), null, 1600m, Months(1));
        Assert.Single(IncomeMath.Gaps([ending, otherLabel], today));
    }

    [Fact]
    public void The_aggregate_equivalent_keeps_full_precision_for_uneven_quarters()
    {
        var phases = new[] { P("T", D(2025, 1, 1), null, 1000m, Months(3)), P("M", D(2025, 1, 1), null, 2000m, Months(1)) };
        Assert.Equal(2333.33m, IncomeMath.Round(IncomeMath.MonthlyEquivalent(phases, D(2025, 10, 1))));
    }

    [Theory]
    [InlineData(DurationUnit.Day)]
    [InlineData(DurationUnit.Week)]
    public void A_cadence_other_than_months_or_years_is_refused(DurationUnit unit)
    {
        var every = Duration.Create(1, unit).TryGetValue(out var d) ? d : throw new InvalidOperationException();
        Assert.Throws<ArgumentOutOfRangeException>(() => IncomeMath.MonthlyEquivalent(P("x", D(2025, 1, 1), null, 100m, every)));
    }

    [Fact]
    public void A_phase_ending_exactly_60_days_ahead_is_warned()
    {
        var today = D(2025, 10, 1);
        var edge = P("Bord", D(2025, 1, 1), today.PlusDays(IncomeMath.GapWindowDays), 1500m, Months(1));
        Assert.Single(IncomeMath.Gaps([edge], today));
    }

    [Fact]
    public void A_mid_month_start_excludes_the_earlier_occurrence_and_an_empty_month_totals_zero()
    {
        var phase = P("Salaire", D(2025, 1, 10), null, 100m, Months(2));
        var months = IncomeMath.Expected([phase], [], D(2025, 1, 15), D(2025, 3, 31), Eur);
        Assert.Equal([(1, 0m), (2, 0m), (3, 100m)], months.Select(m => (m.Month, m.Total.Amount)));
        Assert.Empty(months[1].Occurrences);
    }

    [Fact]
    public void A_capped_phase_stops_being_active_after_its_last_occurrence() // Task 3 review ruling b
    {
        var bonus = P("Prime", D(2023, 1, 15), null, 1800m, Year, max: 2); // occurrences 2023-01-15 and 2024-01-15
        Assert.Equal(D(2025, 1, 15), IncomeMath.EffectiveEnd(bonus));
        Assert.True(IncomeMath.IsActive(bonus, D(2025, 1, 14)));
        Assert.False(IncomeMath.IsActive(bonus, D(2025, 2, 1)));
        Assert.Equal(0m, IncomeMath.MonthlyEquivalent([bonus], D(2025, 2, 1)));
        Assert.Equal(150m, IncomeMath.MonthlyEquivalent([bonus], D(2024, 6, 1)));
    }

    [Fact]
    public void The_earlier_of_ends_on_and_the_cap_is_the_effective_end()
    {
        Assert.Equal(D(2023, 6, 1), IncomeMath.EffectiveEnd(P("x", D(2023, 1, 1), D(2023, 6, 1), 10m, Months(1), max: 12)));
        Assert.Equal(D(2023, 4, 1), IncomeMath.EffectiveEnd(P("x", D(2023, 1, 1), D(2023, 6, 1), 10m, Months(1), max: 3)));
        Assert.Null(IncomeMath.EffectiveEnd(P("x", D(2023, 1, 1), null, 10m, Months(1))));
    }

    [Fact]
    public void A_capped_phase_ending_within_60_days_with_no_successor_warns()
    {
        var capped = P("Prime", D(2025, 1, 1), null, 100m, Months(1), max: 12); // effective end 2026-01-01
        var warned = Assert.Single(IncomeMath.Gaps([capped], D(2025, 11, 15)));
        Assert.Equal(("Prime", D(2026, 1, 1)), (warned.Label, warned.EndsOn));
        Assert.Empty(IncomeMath.Gaps([capped], D(2025, 6, 1)));
        Assert.Empty(IncomeMath.Gaps([capped, P("Prime", D(2026, 1, 1), null, 120m, Months(1))], D(2025, 11, 15)));
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(1_000_000)]
    public void A_cap_whose_date_overflows_never_throws_and_bounds_nothing(int max)
    {
        var phase = P("x", D(2025, 1, 1), null, 10m, Months(1), max: max);
        Assert.Null(IncomeMath.EffectiveEnd(phase));
        Assert.True(IncomeMath.IsActive(phase, D(2030, 1, 1)));
    }

    [Fact]
    public void A_raise_is_a_change_with_its_previous_amount_and_percent()
    {
        var before = P("Salaire", D(2024, 1, 1), D(2025, 1, 1), 2860m, Months(1));
        var after = P("Salaire", D(2025, 1, 1), null, 3030m, Months(1));

        var changes = IncomeMath.Changes([after, before], D(2025, 10, 15));

        Assert.Equal(["change", "start"], changes.Select(c => c.Kind));
        var raise = changes[0];
        Assert.Equal((D(2025, 1, 1), after.Id, 2860m, 3030m, 5.9m),
            (raise.On, raise.PhaseId, raise.Before!.Value.Amount, raise.After!.Value.Amount, raise.Percent));
        Assert.Equal((D(2024, 1, 1), before.Id, (Money?)null), (changes[1].On, changes[1].PhaseId, changes[1].Before));
        // Another cadence makes the amounts incomparable.
        var quarterly = P("Salaire", D(2025, 1, 1), null, 9000m, Months(3));
        Assert.Null(IncomeMath.Changes([before, quarterly], D(2025, 10, 15))[0].Percent);
    }

    [Fact]
    public void A_new_label_is_added_and_an_unfollowed_end_is_ended()
    {
        var phases = new[]
        {
            P("Salaire", D(2024, 1, 1), null, 2000m, Months(1)),
            P("Prime", D(2024, 6, 1), D(2025, 6, 1), 100m, Months(1)),
        };

        var changes = IncomeMath.Changes(phases, D(2025, 10, 15));

        Assert.Equal([("ended", D(2025, 6, 1), "Prime"), ("added", D(2024, 6, 1), "Prime"), ("start", D(2024, 1, 1), "Salaire")],
            changes.Select(c => (c.Kind, c.On, c.Label)));
        Assert.All(changes, c => Assert.False(c.Upcoming));
    }

    [Fact]
    public void Changes_after_today_are_upcoming()
    {
        var phases = new[]
        {
            P("Salaire", D(2025, 1, 1), D(2026, 1, 1), 2000m, Months(1)),
            P("Salaire", D(2026, 1, 1), null, 2100m, Months(1)),
        };

        var changes = IncomeMath.Changes(phases, D(2025, 10, 15));

        Assert.Equal([(true, "change"), (false, "start")], changes.Select(c => (c.Upcoming, c.Kind)));
    }

    [Fact]
    public void Every_phase_starting_on_the_first_day_starts_the_source()
    {
        var phases = new[]
        {
            P("Salaire", D(2024, 1, 1), null, 2000m, Months(1)),
            P("Allocations", D(2024, 1, 1), null, 130m, Months(1)),
        };

        Assert.Equal([("start", "Allocations"), ("start", "Salaire")],
            IncomeMath.Changes(phases, D(2025, 10, 15)).Select(c => (c.Kind, c.Label)));
    }

    [Fact]
    public void A_label_resumed_after_a_gap_has_ended_then_changed()
    {
        var phases = new[]
        {
            P("Contrat", D(2024, 1, 1), D(2024, 7, 1), 1800m, Months(1)),
            P("Contrat", D(2024, 9, 1), null, 1900m, Months(1)),
        };

        Assert.Equal([("change", D(2024, 9, 1)), ("ended", D(2024, 7, 1)), ("start", D(2024, 1, 1))],
            IncomeMath.Changes(phases, D(2025, 10, 15)).Select(c => (c.Kind, c.On)));
    }

    [Fact]
    public void The_next_payment_gathers_every_label_on_the_first_date_after_today()
    {
        var phases = new[]
        {
            P("Salaire", D(2025, 1, 28), null, 2000m, Months(1)),
            P("Prime", D(2024, 10, 28), null, 1800m, Year),
            P("Loyer", D(2025, 1, 5), null, 700m, Months(1)),
        };

        var next = IncomeMath.Next(phases, D(2025, 10, 15));

        Assert.Equal([("Prime", D(2025, 10, 28)), ("Salaire", D(2025, 10, 28))], next.Select(o => (o.Label, o.On)));
        Assert.Equal([D(2025, 10, 28)], IncomeMath.Next(phases, D(2025, 10, 27)).Select(o => o.On).Distinct());
        Assert.Empty(IncomeMath.Next([P("Fini", D(2024, 1, 1), D(2025, 1, 1), 10m, Months(1))], D(2025, 10, 15)));
    }

    [Fact]
    public void A_phase_starting_months_ahead_has_its_start_as_next_payment()
    {
        var next = IncomeMath.Next([P("Nouveau poste", D(2026, 3, 1), null, 2500m, Months(1))], D(2025, 10, 15));

        Assert.Equal([("Nouveau poste", D(2026, 3, 1))], next.Select(o => (o.Label, o.On)));
    }
}
