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
        Assert.Equal(4480m, IncomeMath.MonthlyEquivalent(phases, on, Eur).Amount);
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
        Assert.Equal(2200m, IncomeMath.MonthlyEquivalent([before, after], D(2025, 8, 31), Eur).Amount);
        Assert.Equal(2300m, IncomeMath.MonthlyEquivalent([before, after], D(2025, 9, 1), Eur).Amount);
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
}
