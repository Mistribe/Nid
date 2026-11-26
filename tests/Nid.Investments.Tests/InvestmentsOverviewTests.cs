using System.Globalization;
using Nid.Core.Contracts;
using Nid.Investments.Application;
using Nid.Investments.Contracts;
using Nid.Investments.Domain;
using NodaTime;

namespace Nid.Investments.Tests;

/// <summary>Investments dashboard spec §3.4: the overview's figures on a fixture household.</summary>
public class InvestmentsOverviewTests
{
    private static readonly LocalDate Today = new(2026, 10, 22);
    private static readonly PersonId Alice = EnvelopeFacts.DefaultOwner;
    private static readonly PersonId Bob = PersonId.New();
    private static readonly IReadOnlyList<PersonInfo> Persons =
        [new(Bob, PersonKind.Adult, "Bob", null, 0), new(Alice, PersonKind.Adult, "Alice", null, 0)];

    /// <summary>1 USD = 0.9 EUR today, and no older rate.</summary>
    private static readonly RateTable Rates = new([new FxRate(Today, Currency.Eur, 0.9m, FxRateSource.Ecb)], Today, 7);

    private static LocalDate D(int y, int m, int d) => new(y, m, d);

    /// <summary>investments-data-model.md §12.1's PEA, shared 50/50, valued again in September.</summary>
    private static EnvelopePerformance.Facts Life() => new EnvelopeFacts("Assurance-vie", EnvelopeKind.LifeInsurance, "EUR", (Alice, 5_000), (Bob, 5_000))
        .Flow(D(2024, 12, 31), FlowKind.Opening, 20_000m).Flow(D(2025, 6, 15), FlowKind.Contribution, 2_000m)
        .Value(D(2024, 12, 31), 21_000m).Value(D(2025, 12, 31), 25_000m).Value(D(2026, 9, 30), 27_000m).Build();

    /// <summary>Another investment that sent 1 000 to the PEA on 1 March: both legs are transfers.</summary>
    private static (EnvelopePerformance.Facts Savings, EnvelopePerformance.Facts Pea) Transfer()
    {
        var savings = new EnvelopeFacts("Autre épargne", EnvelopeKind.Other)
            .Flow(D(2024, 12, 31), FlowKind.Opening, 10_000m).Flow(D(2026, 3, 1), FlowKind.Withdrawal, 1_000m, "transfer")
            .Value(D(2024, 12, 31), 10_000m).Value(D(2025, 12, 31), 10_300m).Value(D(2026, 9, 30), 9_450m).Build();
        var pea = new EnvelopeFacts("PEA", EnvelopeKind.Pea)
            .Flow(D(2025, 12, 31), FlowKind.Opening, 5_000m).Flow(D(2026, 3, 1), FlowKind.Contribution, 1_000m, "transfer")
            .Value(D(2025, 12, 31), 5_000m).Value(D(2026, 9, 30), 6_600m).Build();
        return (savings, pea);
    }

    /// <summary>Monthly, last valued at the end of June: stale today (114 days).</summary>
    private static EnvelopePerformance.Facts Stale() => new EnvelopeFacts("CTO", EnvelopeKind.Brokerage)
        .Flow(D(2025, 6, 1), FlowKind.Opening, 900m).Value(D(2025, 6, 1), 900m).Value(D(2026, 6, 30), 1_000m).Build();

    private static EnvelopePerformance.Facts NoValue() => new EnvelopeFacts("PER", EnvelopeKind.Per).Build();

    /// <summary>Closed on 31 March 2026 after its last value of 0.</summary>
    private static EnvelopePerformance.Facts Closed() => new EnvelopeFacts("Ancien", EnvelopeKind.Other) { ClosedOn = D(2026, 3, 31) }
        .Flow(D(2024, 12, 31), FlowKind.Opening, 2_000m).Flow(D(2026, 3, 31), FlowKind.Withdrawal, 2_050m)
        .Value(D(2024, 12, 31), 2_000m).Value(D(2026, 3, 31), 0m).Build();

    private static InvestmentsOverview Build(IReadOnlyList<EnvelopePerformance.Facts> facts, OverviewSpan span = OverviewSpan.TwelveMonths,
        IReadOnlyList<EnvelopeWatchView>? holdings = null)
        => InvestmentsOverviewMath.Build(facts, Currency.Eur, Rates, Today, InvestmentsOverviewMath.Grid(span, Today), span, holdings ?? [], Persons);

    private static decimal N(AmountView? money) => decimal.Parse(money!.Amount, CultureInfo.InvariantCulture);

    [Fact]
    public void The_household_figures_add_up_the_open_envelopes_and_leave_the_transfers_out_of_what_was_paid_in()
    {
        var (savings, pea) = Transfer();

        var o = Build([Life(), savings, pea, Stale(), NoValue(), Closed()]);

        Assert.Equal((5, 1, "EUR"), (o.OpenCount, o.ClosedCount, o.Currency));
        Assert.Equal(27_000m + 9_450m + 6_600m + 1_000m, N(o.Value));
        Assert.Equal((D(2026, 6, 30), 1, 1), (o.OldestAsOf, o.StaleCount, o.WithoutValueCount));
        // Net contributions keep each envelope's own: the transfer legs cancel.
        Assert.Equal(22_000m + 9_000m + 6_000m + 900m, N(o.NetContributions));
        // D5: the 1 000 the PEA received from the savings account is not money paid in.
        Assert.Equal(22_000m + 10_000m + 5_000m + 900m, N(o.PaidIn));
        Assert.Equal(5_000m + 450m + 600m + 100m, N(o.Gain));
        Assert.Equal(EnvelopeMath.RoundPercent(6_150m / 37_900m * 100m), o.GainPct);
        Assert.Empty(o.Findings);
    }

    [Fact]
    public void Since_january_is_the_dashboards()
    {
        var (savings, pea) = Transfer();
        var stale = Stale();

        var o = Build([Life(), savings, pea, stale]);

        // AV +2 000, savings +150 (9 450 − 10 300 + 1 000 sent), PEA +600; the CTO's 31 December value is the one of June 2025,
        // older than its threshold: it is left out, as on the dashboard.
        var periods = new[] { Life(), savings, pea, stale }.Select(f => EnvelopesNetWorth.SinceJanuary(f, Today)).ToList();
        Assert.Null(periods[3]);
        Assert.Equal(2_000m + 150m + 600m, N(o.SinceJanuary));
        Assert.Equal(1, o.WithoutStartCount);
        Assert.Equal(EnvelopeMath.RoundPercent(2_750m / (25_000m + 10_300m + 5_000m + 0m) * 100m), o.SinceJanuaryPct);
    }

    [Fact]
    public void Since_january_ends_on_the_estimate_as_the_performance_page()
    {
        // #754: valued 27 000 on 30 September, estimated 27 500 today: both screens count +2 500 since 1 January.
        var life = Life();
        var estimates = new Dictionary<ItemId, Application.Prices.EnvelopeEstimate>
        {
            [life.Item.Id] = new(life.Item.Id, Money.Create(27_500m, Currency.Eur).TryGetValue(out var m) ? m : throw new InvalidOperationException(),
                Today, D(2026, 9, 30), false, [], null, []),
        };

        var o = InvestmentsOverviewMath.Build([life], Currency.Eur, Rates, Today, InvestmentsOverviewMath.Grid(OverviewSpan.TwelveMonths, Today),
            OverviewSpan.TwelveMonths, [], Persons, estimates);
        var p = InvestmentsPerformanceMath.Build([life], Currency.Eur, Rates, Today, D(2026, 1, 1), Today, estimates).ToView();

        Assert.Equal(2_500m, N(o.SinceJanuary));
        Assert.Equal(N(p.Performance), N(o.SinceJanuary));
        Assert.Equal(2_500m, EnvelopesNetWorth.SinceJanuary(life, Today, m)!.Performance!.Value.Amount);
    }

    [Fact]
    public void Since_january_counts_the_envelopes_and_the_rates_of_the_performance_tab()
    {
        // #768: a CTO first valued this year is left out of both; one closed in March counts in both (+50); dollars convert the
        // 1 January value at that date's rate (1 000 × 0.9) and the rest at today's (1 200 × 0.8): +60, not 200 × 0.8.
        var rates = new RateTable([new FxRate(D(2025, 12, 31), Currency.Eur, 0.9m, FxRateSource.Ecb), new FxRate(Today, Currency.Eur, 0.8m, FxRateSource.Ecb)], Today, 7);
        var entered = new EnvelopeFacts("CTO", EnvelopeKind.Brokerage).Flow(D(2025, 3, 1), FlowKind.Opening, 1_000m).Value(D(2026, 9, 30), 1_800m).Build();
        var closed = new EnvelopeFacts("Ancien", EnvelopeKind.Other) { ClosedOn = D(2026, 3, 31) }
            .Flow(D(2024, 12, 31), FlowKind.Opening, 2_000m).Flow(D(2026, 3, 31), FlowKind.Withdrawal, 2_050m)
            .Value(D(2025, 12, 31), 2_000m).Value(D(2026, 3, 31), 0m).Build();
        var dollars = new EnvelopeFacts("Brokerage", EnvelopeKind.Brokerage, "USD")
            .Flow(D(2025, 1, 1), FlowKind.Opening, 1_000m).Value(D(2025, 12, 31), 1_000m).Value(D(2026, 9, 30), 1_200m).Build();
        EnvelopePerformance.Facts[] facts = [Life(), entered, closed, dollars];

        var o = InvestmentsOverviewMath.Build(facts, Currency.Eur, rates, Today, InvestmentsOverviewMath.Grid(OverviewSpan.TwelveMonths, Today),
            OverviewSpan.TwelveMonths, [], Persons);
        var p = InvestmentsPerformanceMath.Build(facts, Currency.Eur, rates, Today, D(2026, 1, 1), Today).ToView();

        Assert.Equal(2_000m + 50m + 60m, N(o.SinceJanuary));
        Assert.Equal(N(p.Performance), N(o.SinceJanuary));
        Assert.Equal(1, o.WithoutStartCount);
        Assert.True(p.Envelopes.Single(e => e.Name == "CTO").NoStartValue);
    }

    [Fact]
    public void One_envelopes_annual_return_is_the_households()
    {
        var life = Life();

        var o = Build([life]);

        var own = EnvelopeMath.At(life.Envelope.Kind, life.Values, life.Flows, Currency.Eur, ValuationCadence.Monthly, null, Today).AnnualReturnPct;
        Assert.NotNull(own);
        Assert.Equal(own, o.AnnualReturnPct);
    }

    [Fact]
    public void The_kinds_the_persons_and_the_rows_add_up_to_the_value()
    {
        var (savings, pea) = Transfer();

        var o = Build([Life(), savings, pea, NoValue()]);

        Assert.Equal(["life_insurance", "other", "pea"], o.ByKind.Select(k => k.Kind));
        Assert.Equal(N(o.Value), o.ByKind.Sum(k => N(k.Value)));
        // D7: Bob, created first, owns half of the life insurance; Alice the rest.
        Assert.Equal([Bob.Value, Alice.Value], o.ByPerson.Select(p => p.PersonId));
        Assert.Equal((13_500m, 2_500m), (N(o.ByPerson[0].Value), N(o.ByPerson[0].Gain)));
        Assert.Equal(N(o.Value), o.ByPerson.Sum(p => N(p.Value)));
        // Largest value first, the envelope without a value last.
        Assert.Equal(["Assurance-vie", "Autre épargne", "PEA", "PER"], o.Envelopes.Select(e => e.Name));
        Assert.Null(o.Envelopes[^1].ValueConverted);
    }

    [Fact]
    public void The_last_point_is_the_value_and_a_closed_envelope_counts_before_its_closing()
    {
        var closed = Closed();

        var o = Build([Life(), closed]);

        Assert.Equal(13, o.Points.Count);
        Assert.Equal((Today, N(o.Value)), (o.Points[^1].On, N(o.Points[^1].Value)));
        var february = o.Points.Single(p => p.On == D(2026, 2, 28));
        Assert.Equal(25_000m + 2_000m, N(february.Value));
        Assert.Equal(22_000m + 2_000m, N(february.NetContributions));
        Assert.Equal(["life_insurance", "other"], february.ByKind.Select(k => k.Kind));
        Assert.DoesNotContain(o.Points.Single(p => p.On == D(2026, 3, 31)).ByKind, k => k.Kind == "other");
    }

    [Fact]
    public void All_starts_at_the_first_reading()
    {
        var o = Build([Life()], OverviewSpan.All);

        Assert.Equal(D(2024, 12, 31), o.Points[0].On);
        Assert.Equal(D(2024, 12, 31), o.FirstAsOf);
    }

    /// <summary>Paid into by contributions, valued once on 18 October.</summary>
    private static EnvelopePerformance.Facts Tontine(LocalDate first) => new EnvelopeFacts("Tontine", EnvelopeKind.LifeInsurance)
        .Flow(first, FlowKind.Contribution, 1_000m).Flow(D(2026, 4, 10), FlowKind.Contribution, 2_000m).Value(D(2026, 10, 18), 3_500m).Build();

    [Fact]
    public void Before_its_first_value_an_envelope_counts_at_the_amount_paid_in_from_each_contributions_date()
    {
        var o = Build([Tontine(D(2025, 11, 15))]);

        Assert.Equal(
            [
                (D(2025, 10, 31), (decimal?)null, (decimal?)null, false), (D(2025, 11, 30), 1_000m, 1_000m, true),
                (D(2026, 3, 31), 1_000m, 1_000m, true), (D(2026, 4, 30), 3_000m, 3_000m, true), (D(2026, 9, 30), 3_000m, 3_000m, true),
                (Today, 3_500m, 3_000m, false),
            ],
            o.Points.Where(p => p.On.Month is 10 or 11 or 3 or 4 or 9)
                .Select(p => (p.On, p.Value is null ? (decimal?)null : N(p.Value), p.NetContributions is null ? (decimal?)null : N(p.NetContributions), p.PaidIn)));
    }

    [Fact]
    public void All_starts_at_the_first_contribution_when_it_is_older_than_any_value()
    {
        var o = Build([Life(), Tontine(D(2020, 3, 1))], OverviewSpan.All);

        Assert.Equal((D(2020, 3, 31), 1_000m, true), (o.Points[0].On, N(o.Points[0].Value), o.Points[0].PaidIn));
        Assert.Equal(D(2024, 12, 31), o.FirstAsOf);
    }

    [Fact]
    public void An_envelope_in_dollars_is_converted_at_each_dates_rate_and_a_missing_rate_says_so()
    {
        var dollars = new EnvelopeFacts("Brokerage", EnvelopeKind.Brokerage, "USD")
            .Flow(D(2025, 1, 1), FlowKind.Opening, 1_000m).Value(D(2025, 1, 1), 1_000m).Value(D(2026, 10, 1), 1_200m).Build();

        var o = Build([Life(), dollars]);

        Assert.Equal(27_000m + 1_080m, N(o.Value));
        Assert.Equal(1_080m, N(o.Envelopes.Single(e => e.Name == "Brokerage").ValueConverted));
        Assert.Equal(1_200m, N(o.Envelopes.Single(e => e.Name == "Brokerage").Figures.Value));
        // No rate before today: every earlier point is unknown, and the finding names it.
        Assert.Null(o.Points[0].Value);
        Assert.Contains(o.Findings, f => f.Code == FxCodes.RateMissing && f.Reference!.StartsWith("USD:", StringComparison.Ordinal));
    }

    [Fact]
    public void The_watch_list_names_each_envelopes_state_then_the_holdings_by_date()
    {
        var missing = new EnvelopeFacts("Sans ouverture", EnvelopeKind.Other).Value(D(2026, 10, 1), 500m).Build();
        var maturity = new EnvelopeWatchView("holding_maturity", ItemId.New(), Guid.NewGuid(), D(2026, 12, 5));

        var o = Build([Stale(), NoValue(), missing], holdings: [maturity]);

        Assert.Equal(["stale_value", "missing_opening", "holding_maturity", "no_value"], o.Watch.Select(w => w.Code));
        Assert.Equal(D(2026, 6, 30), o.Watch[0].On);
    }

    [Fact]
    public void Without_an_envelope_the_overview_is_empty()
    {
        var o = Build([]);

        Assert.Equal((0, 0), (o.OpenCount, o.ClosedCount));
        Assert.Null(o.Value);
        Assert.Null(o.GainPct);
        Assert.Empty(o.Envelopes);
        Assert.All(o.Points, p => Assert.Null(p.Value));
    }

    [Fact]
    public void A_checking_account_and_a_livret_count_in_the_value_apart_from_the_investments_figures()
    {
        // #356: the livret sends 1 000 to the PEA (one transfer, two legs); the checking account has no flow, only balances.
        var livret = new EnvelopeFacts("Livret A", EnvelopeKind.Savings)
            .Flow(D(2024, 12, 31), FlowKind.Opening, 10_000m).Flow(D(2026, 3, 1), FlowKind.Withdrawal, 1_000m, "transfer", "transfer:1")
            .Value(D(2024, 12, 31), 10_000m).Value(D(2026, 9, 30), 9_200m).Build();
        var checking = new EnvelopeFacts("Courant", EnvelopeKind.Checking).Value(D(2026, 9, 30), 2_500m).Build();
        var pea = new EnvelopeFacts("PEA", EnvelopeKind.Pea)
            .Flow(D(2025, 12, 31), FlowKind.Opening, 5_000m).Flow(D(2026, 3, 1), FlowKind.Contribution, 1_000m, "transfer", "transfer:1")
            .Value(D(2025, 12, 31), 5_000m).Value(D(2026, 9, 30), 6_600m).Build();

        var o = Build([livret, checking, pea]);

        Assert.Equal((18_300m, 11_700m, 6_600m), (N(o.Value), N(o.Available), N(o.Invested)));
        // The 1 000 from the livret is paid into the investments; the checking account's 2 500 is no gain.
        Assert.Equal((6_000m, 6_000m, 600m), (N(o.PaidIn), N(o.NetContributions), N(o.Gain)));
        Assert.Equal(6_000m, N(o.Points[^1].NetContributions));
        Assert.Equal(["checking", "pea", "savings"], o.ByKind.Select(k => k.Kind).Order());
        var row = o.Envelopes.Single(e => e.Kind == "checking");
        Assert.Null(row.RatePct);
        // #374: its balance is no gain, and no money paid in is missing.
        Assert.Equal((null, null, false), (row.Figures.Gain, row.GainConverted, row.Figures.MissingOpening));
        Assert.DoesNotContain(o.Watch, w => w.Code == "missing_opening");
    }

    [Fact]
    public void Without_an_investment_the_investment_figures_are_empty()
    {
        var o = Build([new EnvelopeFacts("Courant", EnvelopeKind.Checking).Value(D(2026, 9, 30), 2_500m).Build()]);

        Assert.Equal((2_500m, 2_500m), (N(o.Value), N(o.Available)));
        Assert.Null(o.Gain);
        Assert.Null(o.AnnualReturnPct);
    }
}
