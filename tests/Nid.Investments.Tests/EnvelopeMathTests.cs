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
        Assert.Equal(14.2m, f.AnnualReturnPct);
        Assert.False(f.MissingOpening);
        Assert.Null(f.StaleDays);
    }

    [Fact]
    public void SavedBetween_counts_contributions_minus_withdrawals_in_the_window()
    {
        var flows = new[]
        {
            Flow(2025, 1, 1, FlowKind.Contribution, 300m), Flow(2025, 6, 30, FlowKind.Contribution, 200m),
            Flow(2025, 3, 10, FlowKind.Withdrawal, 100m), Flow(2024, 12, 31, FlowKind.Contribution, 50m),
            Flow(2025, 7, 1, FlowKind.Contribution, 75m), Flow(2025, 2, 1, FlowKind.Expense, 20m), Flow(2025, 2, 1, FlowKind.Income, 30m),
        };

        Assert.Equal(400m, EnvelopeMath.SavedBetween(flows, new LocalDate(2025, 1, 1), new LocalDate(2025, 6, 30)));
    }

    [Fact]
    public void SavedBetween_leaves_openings_out()
    {
        var flows = new[] { Flow(2025, 3, 1, FlowKind.Opening, 10_000m) };

        Assert.Equal(0m, EnvelopeMath.SavedBetween(flows, new LocalDate(2025, 1, 1), new LocalDate(2025, 6, 30)));
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
    public void The_percent_is_over_the_total_paid_in_so_withdrawals_do_not_inflate_it()
    {
        var f = At([Value(2025, 6, 30, 100m)], [Flow(2025, 1, 1, FlowKind.Opening, 1_000m), Flow(2025, 3, 1, FlowKind.Withdrawal, 1_500m)], new LocalDate(2025, 6, 30));

        Assert.Equal(Eur(-500m), f.NetContributions);
        Assert.Equal(Eur(600m), f.Gain);
        Assert.Equal(60.0m, f.GainPct);
        Assert.False(f.MissingOpening);
    }

    [Fact]
    public void Issue_20_a_life_insurance_with_large_withdrawals_reads_about_2_2_percent_a_year()
    {
        var flows = Enumerable.Range(0, 30).Select(i => Flow(2020, 8, 1, FlowKind.Contribution, 200m) with { OccurredOn = new LocalDate(2020, 8, 1).PlusMonths(i) })
            .Append(Flow(2020, 7, 1, FlowKind.Opening, 30_000m)).Append(Flow(2024, 6, 1, FlowKind.Withdrawal, 12_500m)).ToList();

        var f = At([Value(2026, 10, 1, 27_900m)], flows, new LocalDate(2026, 10, 1));

        Assert.Equal(Eur(4_400m), f.Gain);
        Assert.Equal(12.2m, f.GainPct);
        Assert.Equal(2.2m, f.AnnualReturnPct);
    }

    [Fact]
    public void A_steady_ten_percent_over_one_year_is_ten_percent_a_year()
    {
        var f = At([Value(2024, 1, 1, 1_100m)], [Flow(2023, 1, 1, FlowKind.Opening, 1_000m)], new LocalDate(2024, 1, 1));

        Assert.Equal(10.0m, f.AnnualReturnPct);
    }

    [Fact]
    public void The_annual_return_is_not_meaningful_under_twelve_months_or_without_a_payment()
    {
        Assert.Null(At([Value(2025, 12, 30, 1_100m)], [Flow(2025, 1, 1, FlowKind.Opening, 1_000m)], new LocalDate(2025, 12, 30)).AnnualReturnPct);
        Assert.Null(At([Value(2026, 6, 30, 1_000m)], [Flow(2025, 1, 1, FlowKind.Income, 50m)], new LocalDate(2026, 6, 30)).AnnualReturnPct);
    }

    [Fact]
    public void A_value_of_zero_leaves_payments_only_and_no_annual_return()
    {
        var f = At([Value(2026, 1, 1, 0m)], [Flow(2024, 1, 1, FlowKind.Opening, 1_000m)], new LocalDate(2026, 1, 1));

        Assert.Null(f.AnnualReturnPct);
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
    public void A_period_before_the_first_value_starts_at_zero_and_counts_the_opening()
    {
        var p = EnvelopeMath.Period(PeaValues, PeaFlows, Currency.Eur, new LocalDate(2024, 6, 30), new LocalDate(2025, 12, 31));

        Assert.Null(p.StartAsOf);
        Assert.False(p.NoStartValue);
        Assert.Equal((Eur(0m), Eur(25_000m), Eur(22_000m), Eur(3_000m)), (p.StartValue, p.EndValue, p.NetContributions, p.Performance));
    }

    [Fact]
    public void A_period_without_a_start_value_but_older_flows_starts_at_the_amount_paid_in()
    {
        // Entered without its past valuations: the opening is before the start, the first value after it.
        var p = EnvelopeMath.Period([Value(2025, 12, 31, 25_000m)], PeaFlows, Currency.Eur, new LocalDate(2025, 3, 31), new LocalDate(2025, 12, 31));

        Assert.Null(p.StartAsOf);
        Assert.True(p.NoStartValue);
        Assert.Equal((Eur(20_000m), Eur(25_000m), Eur(2_000m), Eur(3_000m)), (p.StartValue, p.EndValue, p.NetContributions, p.Performance));
    }

    [Fact]
    public void Income_before_the_start_is_not_money_in_and_an_opening_after_it_counts_from_zero()
    {
        var flows = new[] { Flow(2025, 2, 1, FlowKind.Income, 50m), Flow(2025, 6, 15, FlowKind.Opening, 20_000m) };
        var p = EnvelopeMath.Period([Value(2025, 12, 31, 21_000m)], flows, Currency.Eur, new LocalDate(2025, 3, 31), new LocalDate(2025, 12, 31));

        Assert.False(p.NoStartValue);
        Assert.Equal((Eur(0m), Eur(20_000m), Eur(1_000m)), (p.StartValue, p.NetContributions, p.Performance));
    }

    [Fact]
    public void An_opening_on_the_start_date_without_a_value_that_day_opens_the_period()
    {
        var from = new LocalDate(2025, 3, 31);
        var p = EnvelopeMath.Period([Value(2025, 12, 31, 21_000m)], [Flow(2025, 3, 31, FlowKind.Opening, 20_000m)], Currency.Eur, from, new LocalDate(2025, 12, 31));

        Assert.False(p.NoStartValue);
        Assert.Equal((Eur(0m), Eur(20_000m), Eur(1_000m)), (p.StartValue, p.NetContributions, p.Performance));
    }

    [Fact]
    public void A_period_without_an_end_value_is_unknown()
    {
        var p = EnvelopeMath.Period(PeaValues, PeaFlows, Currency.Eur, new LocalDate(2024, 6, 30), new LocalDate(2024, 12, 30));

        Assert.Equal((null, null, null), (p.StartValue, p.EndValue, p.Performance));
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
    public void The_series_is_ordered_by_date_whatever_the_input_order_and_empty_without_values_or_flows()
    {
        var points = EnvelopeMath.Series([PeaValues[1], PeaValues[0]], PeaFlows, Currency.Eur);

        Assert.Equal([new LocalDate(2024, 12, 31), new LocalDate(2025, 12, 31)], points.Select(p => p.AsOf));
        Assert.All(points, p => Assert.False(p.PaidIn));
        Assert.Empty(EnvelopeMath.Series([], [], Currency.Eur));
    }

    [Fact]
    public void Before_its_first_value_the_series_counts_the_amount_paid_in_on_each_date_of_money_in_or_out()
    {
        Flow[] flows =
        [
            Flow(2020, 1, 15, FlowKind.Opening, 1_000m), Flow(2020, 6, 1, FlowKind.Contribution, 500m), Flow(2020, 6, 1, FlowKind.Contribution, 200m),
            Flow(2021, 1, 1, FlowKind.Withdrawal, 100m), Flow(2022, 3, 1, FlowKind.Expense, 10m), Flow(2026, 10, 18, FlowKind.Contribution, 300m),
        ];

        var points = EnvelopeMath.Series([Value(2026, 10, 18, 2_000m)], flows, Currency.Eur);

        Assert.Equal(
            [
                (new LocalDate(2020, 1, 15), Eur(1_000m), Eur(1_000m), true), (new LocalDate(2020, 6, 1), Eur(1_700m), Eur(1_700m), true),
                (new LocalDate(2021, 1, 1), Eur(1_600m), Eur(1_600m), true), (new LocalDate(2026, 10, 18), Eur(2_000m), Eur(1_900m), false),
            ],
            points.Select(p => (p.AsOf, p.Value, p.NetContributions, p.PaidIn)));
        // Never valued: every point is the amount paid in.
        Assert.All(EnvelopeMath.Series([], PeaFlows, Currency.Eur), p => Assert.True(p.PaidIn && p.Value == p.NetContributions));
    }
}
