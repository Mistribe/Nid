using System.Globalization;
using Nid.Core.Contracts;
using Nid.Investments.Application;
using Nid.Investments.Contracts;
using Nid.Investments.Domain;
using NodaTime;

namespace Nid.Investments.Tests;

/// <summary>Investments dashboard spec §4.4: the performance of a period on a fixture household.</summary>
public class InvestmentsPerformanceTests
{
    private static readonly LocalDate Today = new(2026, 10, 22);
    private static readonly LocalDate From = new(2025, 12, 31);
    private static readonly LocalDate To = new(2026, 9, 30);

    /// <summary>1 USD = 0.9 EUR at the period's start and end, 0.8 today.</summary>
    private static readonly RateTable Rates = new(
        [new FxRate(From, Currency.Eur, 0.9m, FxRateSource.Ecb), new FxRate(To, Currency.Eur, 0.9m, FxRateSource.Ecb),
            new FxRate(Today, Currency.Eur, 0.8m, FxRateSource.Ecb)], Today, 7);

    private static LocalDate D(int y, int m, int d) => new(y, m, d);

    /// <summary>+600 in January, −500 in February, +1 900 by the end of September: +2 000 over the period.</summary>
    private static EnvelopePerformance.Facts Life() => new EnvelopeFacts("Assurance-vie", EnvelopeKind.LifeInsurance)
        .Flow(D(2024, 12, 31), FlowKind.Opening, 20_000m)
        .Value(D(2024, 12, 31), 21_000m).Value(D(2025, 12, 31), 25_000m).Value(D(2026, 1, 31), 25_600m).Value(D(2026, 2, 28), 25_100m)
        .Value(D(2026, 9, 30), 27_000m).Build();

    /// <summary>The savings account sends 1 000 to the PEA on 1 March; +150 and +600 by the end of September.</summary>
    private static (EnvelopePerformance.Facts Savings, EnvelopePerformance.Facts Pea) Transfer(bool savingsStartsLate = false)
    {
        var savings = new EnvelopeFacts("Livret A", EnvelopeKind.Savings)
            .Flow(D(2024, 12, 31), FlowKind.Opening, 10_000m).Flow(D(2026, 3, 1), FlowKind.Withdrawal, 1_000m, "transfer");
        savings = savingsStartsLate ? savings.Value(D(2026, 2, 1), 10_300m) : savings.Value(D(2024, 12, 31), 10_000m).Value(D(2025, 12, 31), 10_300m);
        var pea = new EnvelopeFacts("PEA", EnvelopeKind.Pea)
            .Flow(D(2025, 12, 31), FlowKind.Opening, 5_000m).Flow(D(2026, 3, 1), FlowKind.Contribution, 1_000m, "transfer")
            .Value(D(2025, 12, 31), 5_000m).Value(D(2026, 9, 30), 6_600m).Build();
        return (savings.Value(D(2026, 9, 30), 9_450m).Build(), pea);
    }

    private static InvestmentsPerformance Build(params EnvelopePerformance.Facts[] facts)
        => InvestmentsPerformanceMath.Build(facts, Currency.Eur, Rates, Today, From, To).ToView();

    private static InvestmentsPerformance Between(LocalDate from, LocalDate to, params EnvelopePerformance.Facts[] facts)
        => InvestmentsPerformanceMath.Build(facts, Currency.Eur, Rates, Today, from, to).ToView();

    private static decimal N(AmountView? money) => decimal.Parse(money!.Amount, CultureInfo.InvariantCulture);

    private static decimal? Nullable(AmountView? money) => money is null ? null : N(money);

    private static void AssertTheEquality(InvestmentsPerformance p)
        => Assert.Equal(N(p.EndValue), N(p.StartValue) + N(p.Contributions) - N(p.Withdrawals) + N(p.Transfers) + N(p.Performance));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_review_part_matches_the_view(bool rateMissing)
    {
        var (savings, pea) = Transfer(savingsStartsLate: true);
        // Without the rate of the start, the dollars' start (and every household total) is unknown.
        var rates = rateMissing ? new RateTable([new FxRate(To, Currency.Eur, 0.9m, FxRateSource.Ecb)], Today, 7) : Rates;
        var dollars = new EnvelopeFacts("Brokerage", EnvelopeKind.Brokerage, "USD").Value(From, 1_000m).Value(To, 1_100m).Build();
        var figures = InvestmentsPerformanceMath.Build([Life(), savings, pea, dollars], Currency.Eur, rates, Today, From, To);

        var view = figures.ToView();
        var part = figures.ToReviewPart();

        Assert.Equal((Nullable(view.StartValue), Nullable(view.EndValue), Nullable(view.Contributions), Nullable(view.Withdrawals), Nullable(view.Performance), view.PeriodReturnPct),
            (part.StartValue, part.EndValue, part.Contributions, part.Withdrawals, part.Gain, part.PeriodReturnPct));
        Assert.Equal(view.Envelopes.Select(e => (e.Id, e.Name, e.Kind, Nullable(e.StartValue), Nullable(e.EndValue), Nullable(e.NetContributions), Nullable(e.PerformanceConverted), e.PeriodReturnPct, e.StaleStart, e.NoStartValue)),
            part.Envelopes.Select(e => (e.Id, e.Name, e.Kind, e.StartValue, e.EndValue, e.NetContributions, e.Gain, e.PeriodReturnPct, e.StaleStart, e.NoStartValue)));
        Assert.Equal(view.Findings.Select(f => f.Code), part.Findings);
        Assert.Equal(rateMissing, part.StartValue is null);
        Assert.Equal(rateMissing, part.Findings.Count > 0);
        Assert.Contains(part.Envelopes, e => e.NoStartValue);
    }

    [Fact]
    public void The_change_in_value_adds_up_and_a_transfer_between_two_counted_envelopes_cancels()
    {
        var (savings, pea) = Transfer();

        var p = Build(Life(), savings, pea);

        Assert.Equal((25_000m + 10_300m + 5_000m, 27_000m + 9_450m + 6_600m), (N(p.StartValue), N(p.EndValue)));
        Assert.Equal((0m, 0m, 0m), (N(p.Contributions), N(p.Withdrawals), N(p.Transfers)));
        Assert.Equal(2_000m + 150m + 600m, N(p.Performance));
        AssertTheEquality(p);
        Assert.Equal(EnvelopeMath.RoundPercent(2_750m / 40_300m * 100m), p.PerformancePct);
        Assert.Equal(["Assurance-vie", "PEA", "Livret A"], p.Envelopes.Select(e => e.Name));
        Assert.Equal("2000.00", p.Envelopes[0].Performance);
        Assert.All(p.Envelopes, e => Assert.False(e.StaleStart));
    }

    [Fact]
    public void An_envelope_valued_late_starts_at_its_amount_paid_in_and_its_transfer_cancels()
    {
        var (savings, pea) = Transfer(savingsStartsLate: true);

        var p = Build(savings, pea);

        // Opened before the start, the savings account starts at its 10 000 paid in: no contribution of the period, flagged.
        Assert.Equal((15_000m, 0m, 0m), (N(p.StartValue), N(p.Contributions), N(p.Transfers)));
        Assert.Equal(450m + 600m, N(p.Performance));
        Assert.Equal([("PEA", false), ("Livret A", true)], p.Envelopes.Select(e => (e.Name, e.NoStartValue)));
        AssertTheEquality(p);
    }

    [Fact]
    public void An_envelope_entered_without_its_past_values_counts_only_the_periods_flows()
    {
        // Opened and paid into before the start, first valued after it: 1 000 + 500 paid in by then, 200 in the period.
        var entered = new EnvelopeFacts("CTO", EnvelopeKind.Brokerage)
            .Flow(D(2025, 3, 1), FlowKind.Opening, 1_000m).Flow(D(2025, 9, 1), FlowKind.Contribution, 500m)
            .Flow(D(2026, 4, 1), FlowKind.Contribution, 200m).Value(D(2026, 9, 30), 1_800m).Build();

        var p = Build(entered);

        var row = Assert.Single(p.Envelopes);
        Assert.True(row.NoStartValue);
        Assert.Equal((1_500m, 1_800m, 200m, "100.00"), (N(row.StartValue), N(row.EndValue), N(row.NetContributions), row.Performance));
        Assert.Equal((1_500m, 200m, 0m), (N(p.StartValue), N(p.Contributions), N(p.Withdrawals)));
        Assert.Equal(N(p.Performance), p.Months.Sum(m => N(m.Performance)));
        // The return starts on the amount paid in, not on flows moved from before the start: 100 gained on about 1 640 engaged.
        Assert.InRange(row.PeriodReturnPct!.Value, 5m, 7m);
        AssertTheEquality(p);
    }

    [Fact]
    public void A_transfer_with_an_envelope_never_valued_is_shown_apart()
    {
        var (_, pea) = Transfer();
        var unvalued = new EnvelopeFacts("Livret A", EnvelopeKind.Savings)
            .Flow(D(2024, 12, 31), FlowKind.Opening, 10_000m).Flow(D(2026, 3, 1), FlowKind.Withdrawal, 1_000m, "transfer").Build();

        var p = Build(unvalued, pea);

        Assert.Equal(["PEA"], p.Envelopes.Select(e => e.Name));
        Assert.Equal(1_000m, N(p.Transfers));
        Assert.Equal(600m, N(p.Performance));
        AssertTheEquality(p);
    }

    [Fact]
    public void The_months_add_up_to_the_period_and_name_the_best_and_the_worst()
    {
        var (savings, pea) = Transfer();
        var quarterly = new EnvelopeFacts("Trimestriel", EnvelopeKind.Other) { Cadence = ValuationCadence.Quarterly }
            .Flow(D(2025, 9, 30), FlowKind.Opening, 1_000m).Value(D(2025, 12, 31), 1_000m).Value(D(2026, 6, 30), 1_100m).Build();

        var p = Build(Life(), savings, pea, quarterly);

        Assert.Equal(["2026-01", "2026-02", "2026-03", "2026-04", "2026-05", "2026-06", "2026-07", "2026-08", "2026-09"], p.Months.Select(m => m.Month));
        Assert.Equal(N(p.Performance), p.Months.Sum(m => N(m.Performance)));
        // A quarterly value carries its performance on the month of the value.
        Assert.Equal(100m, N(p.Months.Single(m => m.Month == "2026-06").Performance));
        Assert.Equal(("2026-09", "2026-02"), (p.BestMonth, p.WorstMonth));
    }

    [Fact]
    public void An_envelope_without_a_start_counts_from_zero_and_one_with_a_stale_start_counts_flagged()
    {
        var late = new EnvelopeFacts("Nouveau", EnvelopeKind.Pea).Flow(D(2026, 5, 1), FlowKind.Opening, 3_000m).Value(D(2026, 5, 1), 3_000m)
            .Value(D(2026, 9, 30), 3_300m).Build();
        var stale = new EnvelopeFacts("CTO", EnvelopeKind.Brokerage).Flow(D(2025, 6, 1), FlowKind.Opening, 900m)
            .Value(D(2025, 6, 1), 900m).Value(D(2026, 6, 30), 1_000m).Build();

        var p = Build(Life(), late, stale);

        Assert.Equal([("Assurance-vie", false), ("Nouveau", false), ("CTO", true)], p.Envelopes.Select(e => (e.Name, e.StaleStart)));
        Assert.Equal(2_000m + 300m + 100m, N(p.Performance));
        Assert.Equal(N(p.Performance), p.Months.Sum(m => N(m.Performance)));
        AssertTheEquality(p);
    }

    [Fact]
    public void An_envelope_in_dollars_is_converted_at_the_rate_of_the_periods_end()
    {
        var dollars = new EnvelopeFacts("Brokerage", EnvelopeKind.Brokerage, "USD")
            .Flow(D(2025, 1, 1), FlowKind.Opening, 1_000m).Value(D(2025, 12, 31), 1_000m).Value(D(2026, 9, 30), 1_200m).Build();

        var p = Build(Life(), dollars);

        Assert.Equal(2_000m + 180m, N(p.Performance));
        Assert.Equal(("200.00", 180m), (p.Envelopes.Single(e => e.Name == "Brokerage").Performance, N(p.Envelopes.Single(e => e.Name == "Brokerage").PerformanceConverted)));
        Assert.Empty(p.Findings);
    }

    [Fact]
    public void The_reference_is_the_savings_accounts_and_absent_without_one()
    {
        var (savings, pea) = Transfer();

        var with = Build(Life(), savings, pea);
        var without = Build(Life(), pea);

        var own = EnvelopeMath.At(savings.Values, savings.Flows, Currency.Eur, ValuationCadence.Monthly, null, Today).AnnualReturnPct;
        Assert.NotNull(own);
        Assert.Equal(own, with.Reference!.AnnualReturnPct);
        Assert.Null(without.Reference);
    }

    [Fact]
    public void A_closed_envelope_counts_only_while_it_was_open()
    {
        var closed = new EnvelopeFacts("Ancien", EnvelopeKind.Savings) { ClosedOn = D(2025, 11, 30) }
            .Flow(D(2024, 12, 31), FlowKind.Opening, 2_000m).Value(D(2024, 12, 31), 2_000m).Value(D(2025, 11, 30), 0m).Build();

        var p = Build(Life(), closed);

        Assert.Equal(["Assurance-vie"], p.Envelopes.Select(e => e.Name));
    }

    [Fact]
    public void An_envelope_opened_in_the_period_counts_from_zero()
    {
        var opened = new EnvelopeFacts("PEA", EnvelopeKind.Pea).Flow(D(2025, 3, 1), FlowKind.Opening, 1_000m).Value(D(2025, 12, 31), 1_100m).Build();

        var p = Between(D(2024, 12, 31), D(2025, 12, 31), opened);

        var row = Assert.Single(p.Envelopes);
        Assert.False(row.NoStartValue);
        Assert.Equal((0m, 1_100m, 1_000m, "100.00"), (N(row.StartValue), N(row.EndValue), N(row.NetContributions), row.Performance));
        Assert.Equal((0m, 1_100m, 100m), (N(p.StartValue), N(p.EndValue), N(p.Performance)));
        AssertTheEquality(p);
    }

    [Fact]
    public void A_stale_start_is_flagged_not_excluded()
    {
        var stale = new EnvelopeFacts("CTO", EnvelopeKind.Brokerage).Flow(From.PlusDays(-200), FlowKind.Opening, 1_000m)
            .Value(From.PlusDays(-200), 1_000m).Value(To, 1_100m).Build();

        var p = Build(Life(), stale);

        var row = p.Envelopes.Single(e => e.Name == "CTO");
        Assert.True(row.StaleStart);
        Assert.Equal(100m, N(row.PerformanceConverted));
        Assert.False(p.Envelopes.Single(e => e.Name == "Assurance-vie").StaleStart);
    }

    [Fact]
    public void The_start_converts_at_its_own_date()
    {
        var rates = new RateTable([new FxRate(From, Currency.Eur, 0.90m, FxRateSource.Ecb), new FxRate(To, Currency.Eur, 0.95m, FxRateSource.Ecb)], Today, 7);
        var dollars = new EnvelopeFacts("Brokerage", EnvelopeKind.Brokerage, "USD").Value(From, 1_000m).Value(To, 1_000m).Build();

        var p = InvestmentsPerformanceMath.Build([dollars], Currency.Eur, rates, Today, From, To).ToView();

        Assert.Equal((900m, 950m, 50m), (N(p.StartValue), N(p.EndValue), N(p.Performance)));
        var row = Assert.Single(p.Envelopes);
        Assert.Equal(("0.00", 900m, 950m, 50m), (row.Performance, N(row.StartValue), N(row.EndValue), N(row.PerformanceConverted)));
        AssertTheEquality(p);
    }

    [Fact]
    public void Today_ends_on_the_estimate()
    {
        var life = Life();
        var estimate = new Application.Prices.EnvelopeEstimate(life.Item.Id,
            Money.Create(27_500m, Currency.Eur).TryGetValue(out var m) ? m : throw new InvalidOperationException(), Today, To, false, [], null, []);

        var p = InvestmentsPerformanceMath.Build([life], Currency.Eur, Rates, Today, From, Today,
            new Dictionary<ItemId, Application.Prices.EnvelopeEstimate> { [life.Item.Id] = estimate }).ToView();

        Assert.Equal((27_500m, 2_500m), (N(p.EndValue), N(p.Performance)));
        Assert.Equal(27_500m, N(Assert.Single(p.Envelopes).EndValue));
        Assert.Equal(N(p.Performance), p.Months.Sum(m => N(m.Performance)));
        AssertTheEquality(p);
    }

    [Fact]
    public void A_closed_envelope_ends_on_its_closing_value_not_an_estimate()
    {
        // Closed on 1 October with its last holdings still priced: an estimate of today is no value of a closed envelope.
        var closed = new EnvelopeFacts("Ancien PEA", EnvelopeKind.Pea) { ClosedOn = D(2026, 10, 1) }
            .Flow(D(2024, 12, 31), FlowKind.Opening, 1_000m).Value(From, 1_000m).Value(D(2026, 10, 1), 0m).Build();
        var estimate = new Application.Prices.EnvelopeEstimate(closed.Item.Id,
            Money.Create(1_200m, Currency.Eur).TryGetValue(out var m) ? m : throw new InvalidOperationException(), Today, To, false, [], null, []);

        var p = InvestmentsPerformanceMath.Build([closed], Currency.Eur, Rates, Today, From, Today,
            new Dictionary<ItemId, Application.Prices.EnvelopeEstimate> { [closed.Item.Id] = estimate }).ToView();

        Assert.Equal(0m, N(Assert.Single(p.Envelopes).EndValue));
    }

    [Fact]
    public void The_households_period_return_is_on_its_converted_flows()
    {
        var euros = new EnvelopeFacts("PEA", EnvelopeKind.Pea).Value(From, 1_000m).Value(To, 1_050m).Build();
        var dollars = new EnvelopeFacts("Brokerage", EnvelopeKind.Brokerage, "USD").Value(From, 1_000m).Value(To, 1_000m).Build();

        var p = Build(euros, dollars);

        // 1 000 € and 900 € in, 1 050 € and 900 € out: +2,6 %; adding the dollars unconverted would give +2,5 %.
        Assert.Equal(2.6m, p.PeriodReturnPct);
        Assert.Equal([5.0m, 0.0m], p.Envelopes.Select(e => e.PeriodReturnPct));
    }

    [Fact]
    public void A_missing_rate_leaves_the_households_period_return_unknown()
    {
        var rates = new RateTable([new FxRate(To, Currency.Eur, 0.9m, FxRateSource.Ecb)], Today, 7);
        var dollars = new EnvelopeFacts("Brokerage", EnvelopeKind.Brokerage, "USD").Value(From, 1_000m).Value(To, 1_100m).Build();

        var p = InvestmentsPerformanceMath.Build([Life(), dollars], Currency.Eur, rates, Today, From, To).ToView();

        Assert.Null(p.PeriodReturnPct);
        Assert.Null(p.StartValue);
        Assert.NotEmpty(p.Findings);
        // In its own currency, the envelope's return is still known.
        Assert.Equal(10.0m, p.Envelopes.Single(e => e.Name == "Brokerage").PeriodReturnPct);
    }

    [Fact]
    public void The_period_return_is_money_weighted()
    {
        var pea = new EnvelopeFacts("PEA", EnvelopeKind.Pea).Flow(D(2024, 12, 31), FlowKind.Opening, 1_000m).Value(D(2024, 12, 31), 1_000m)
            .Flow(D(2025, 7, 1), FlowKind.Contribution, 1_000m).Value(D(2025, 12, 31), 2_100m).Build();

        var p = Between(D(2024, 12, 31), D(2025, 12, 31), pea);

        Assert.InRange(p.PeriodReturnPct!.Value, 6.5m, 6.9m);
        Assert.Equal(p.PeriodReturnPct, Assert.Single(p.Envelopes).PeriodReturnPct);
    }
}
