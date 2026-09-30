using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using Nid.Investments.Domain;
using NodaTime;

namespace Nid.Investments.Tests;

public class EnvelopeMathTests
{
    private static readonly ItemId Item = ItemId.New();

    private static Money Eur(decimal amount) => Money.Create(amount, Currency.Eur).TryGetValue(out var m) ? m : throw new InvalidOperationException();

    private static Valuation Value(int y, int m, int d, decimal amount) => new(Guid.NewGuid(), Item, new LocalDate(y, m, d), Eur(amount), null);

    private static Flow Flow(int y, int m, int d, FlowKind kind, decimal amount) => new(Guid.NewGuid(), Item, new LocalDate(y, m, d), kind, Eur(amount), null, null, null);

    // investments-data-model.md §12.1
    private static readonly Valuation[] PeaValues = [Value(2024, 12, 31, 21_000m), Value(2025, 12, 31, 25_000m)];
    private static readonly Flow[] PeaFlows = [Flow(2024, 12, 31, FlowKind.Opening, 20_000m), Flow(2025, 6, 15, FlowKind.Contribution, 2_000m)];

    private static EnvelopeFigures At(IReadOnlyList<Valuation> values, IReadOnlyList<Flow> flows, LocalDate d,
        ValuationCadence cadence = ValuationCadence.Monthly, int? staleAfter = null)
        => EnvelopeMath.At(values, flows, Currency.Eur, cadence, staleAfter, d);

    [Fact]
    public void The_golden_case_gives_plus_3000_and_13_6_percent()
    {
        var f = At(PeaValues, PeaFlows, new LocalDate(2025, 12, 31));

        Assert.Equal((Eur(25_000m), new LocalDate(2025, 12, 31)), (f.Value, f.AsOf));
        Assert.Equal(Eur(22_000m), f.NetContributions);
        Assert.Equal(Eur(3_000m), f.Gain);
        Assert.Equal(13.6m, f.GainPct);
        Assert.False(f.MissingOpening);
        Assert.Null(f.StaleDays);
    }

    [Fact]
    public void The_golden_year_performance_is_plus_2000()
    {
        var p = EnvelopeMath.Period(PeaValues, PeaFlows, Currency.Eur, new LocalDate(2024, 12, 31), new LocalDate(2025, 12, 31));

        Assert.Equal((Eur(21_000m), Eur(25_000m), Eur(2_000m), Eur(2_000m)), (p.StartValue, p.EndValue, p.NetContributions, p.Performance));
    }

    [Fact]
    public void A_flow_on_the_value_date_counts_and_one_after_only_since()
    {
        var flows = PeaFlows.Append(Flow(2025, 12, 31, FlowKind.Contribution, 500m)).Append(Flow(2026, 1, 10, FlowKind.Contribution, 700m))
            .Append(Flow(2026, 1, 12, FlowKind.Withdrawal, 50m)).ToList();

        var f = At(PeaValues, flows, new LocalDate(2026, 1, 20));

        Assert.Equal(Eur(22_500m), f.NetContributions);
        Assert.Equal(Eur(2_500m), f.Gain);
        Assert.Equal((Eur(700m), Eur(50m)), (f.ContributionsSince, f.WithdrawalsSince));
    }

    [Fact]
    public void A_value_before_any_flow_is_all_gain_with_an_undefined_percent_and_missing_opening()
    {
        var f = At([Value(2025, 6, 30, 1_000m)], [], new LocalDate(2025, 6, 30));

        Assert.Equal((Eur(0m), Eur(1_000m)), (f.NetContributions, f.Gain));
        Assert.Null(f.GainPct);
        Assert.True(f.MissingOpening);
    }

    [Fact]
    public void Withdrawals_above_contributions_leave_the_percent_undefined()
    {
        var f = At([Value(2025, 6, 30, 100m)], [Flow(2025, 1, 1, FlowKind.Opening, 1_000m), Flow(2025, 3, 1, FlowKind.Withdrawal, 1_500m)], new LocalDate(2025, 6, 30));

        Assert.Equal(Eur(-500m), f.NetContributions);
        Assert.Equal(Eur(600m), f.Gain);
        Assert.Null(f.GainPct);
        Assert.False(f.MissingOpening);
    }

    [Fact]
    public void Without_any_value_every_figure_is_unknown_and_since_counts_every_flow()
    {
        var f = At([], [Flow(2025, 1, 1, FlowKind.Contribution, 100m)], new LocalDate(2025, 6, 30));

        Assert.Null(f.Value);
        Assert.Null(f.AsOf);
        Assert.Null(f.NetContributions);
        Assert.Null(f.Gain);
        Assert.Null(f.GainPct);
        Assert.Null(f.Fees);
        Assert.Null(f.GainAfterFees);
        Assert.Null(f.Income);
        Assert.Equal(Eur(100m), f.ContributionsSince);
        Assert.False(f.MissingOpening);
        Assert.Null(f.StaleDays);
    }

    [Fact]
    public void A_value_after_d_is_ignored()
    {
        var f = At(PeaValues, PeaFlows, new LocalDate(2025, 6, 30));

        Assert.Equal((Eur(21_000m), new LocalDate(2024, 12, 31)), (f.Value, f.AsOf));
    }

    [Fact]
    public void Fees_and_income_stay_apart_from_the_gain()
    {
        var flows = PeaFlows.Append(Flow(2025, 3, 1, FlowKind.Expense, 120m)).Append(Flow(2025, 4, 1, FlowKind.Income, 80m)).ToList();

        var f = At(PeaValues, flows, new LocalDate(2025, 12, 31));

        Assert.Equal(Eur(3_000m), f.Gain);
        Assert.Equal((Eur(120m), Eur(2_880m), Eur(80m)), (f.Fees, f.GainAfterFees, f.Income));
    }

    [Theory]
    [InlineData(ValuationCadence.Monthly, null, 45, false)]
    [InlineData(ValuationCadence.Monthly, null, 46, true)]
    [InlineData(ValuationCadence.Quarterly, null, 121, true)]
    [InlineData(ValuationCadence.Yearly, null, 400, false)]
    [InlineData(ValuationCadence.Manual, null, 5000, false)]
    [InlineData(ValuationCadence.Monthly, 10, 11, true)]
    [InlineData(ValuationCadence.Manual, 10, 11, true)]
    public void Staleness_follows_the_cadence_or_the_override(ValuationCadence cadence, int? staleAfter, int daysAfter, bool stale)
    {
        var d = new LocalDate(2025, 1, 1).PlusDays(daysAfter);

        var f = At([Value(2025, 1, 1, 10m)], [Flow(2025, 1, 1, FlowKind.Opening, 10m)], d, cadence, staleAfter);

        Assert.Equal(stale ? daysAfter : null, f.StaleDays);
    }

    [Theory]
    [InlineData("13.65", "13.7")]
    [InlineData("-13.65", "-13.7")]
    [InlineData("13.64", "13.6")]
    [InlineData("0.05", "0.1")]
    public void The_percent_rounds_half_away_from_zero(string exact, string rounded)
        => Assert.Equal(decimal.Parse(rounded, System.Globalization.CultureInfo.InvariantCulture),
            EnvelopeMath.RoundPercent(decimal.Parse(exact, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void A_period_without_a_start_value_is_unknown()
    {
        var p = EnvelopeMath.Period(PeaValues, PeaFlows, Currency.Eur, new LocalDate(2024, 6, 30), new LocalDate(2025, 12, 31));

        Assert.Null(p.StartValue);
        Assert.Null(p.Performance);
        Assert.Equal(Eur(25_000m), p.EndValue);
    }

    [Fact]
    public void Period_performance_is_additive_over_random_series()
    {
        var random = new Random(20260929);
        for (var run = 0; run < 300; run++)
        {
            var start = new LocalDate(2024, 1, 1);
            var values = Enumerable.Range(0, random.Next(3, 12))
                .Select(i => Value(2024, 1, 1, random.Next(-5_000, 50_000)) with { AsOf = start.PlusDays(i * 30 + random.Next(0, 20)) })
                .GroupBy(v => v.AsOf).Select(g => g.First()).ToList();
            var flows = Enumerable.Range(0, random.Next(0, 15))
                .Select(_ => Flow(2024, 1, 1, (FlowKind)random.Next(0, 5), random.Next(1, 3_000)) with { OccurredOn = start.PlusDays(random.Next(0, 400)) })
                .ToList();
            var dates = values.Select(v => v.AsOf).OrderBy(d => d).ToList();
            var a = dates[0];
            var b = dates[random.Next(0, dates.Count)];
            var c = dates[^1];

            var ac = EnvelopeMath.Period(values, flows, Currency.Eur, a, c).Performance!.Value;
            var ab = EnvelopeMath.Period(values, flows, Currency.Eur, a, b).Performance!.Value;
            var bc = EnvelopeMath.Period(values, flows, Currency.Eur, b, c).Performance!.Value;

            Assert.Equal(ac.Amount, ab.Amount + bc.Amount);
        }
    }

    [Fact]
    public void The_series_gives_each_value_with_the_net_contributions_up_to_its_date()
    {
        var points = EnvelopeMath.Series(PeaValues, PeaFlows, Currency.Eur);

        Assert.Equal(
            [(new LocalDate(2024, 12, 31), Eur(21_000m), Eur(20_000m)), (new LocalDate(2025, 12, 31), Eur(25_000m), Eur(22_000m))],
            points.Select(p => (p.AsOf, p.Value, p.NetContributions)));
    }

    [Fact]
    public void The_series_is_ordered_by_date_whatever_the_input_order_and_empty_without_values()
    {
        var points = EnvelopeMath.Series([PeaValues[1], PeaValues[0]], PeaFlows, Currency.Eur);

        Assert.Equal([new LocalDate(2024, 12, 31), new LocalDate(2025, 12, 31)], points.Select(p => p.AsOf));
        Assert.Empty(EnvelopeMath.Series([], PeaFlows, Currency.Eur));
    }
}
