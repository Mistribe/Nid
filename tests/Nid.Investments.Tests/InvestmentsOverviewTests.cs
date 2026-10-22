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

    /// <summary>A savings account that sent 1 000 to the PEA on 1 March: both legs are transfers.</summary>
    private static (EnvelopePerformance.Facts Savings, EnvelopePerformance.Facts Pea) Transfer()
    {
        var savings = new EnvelopeFacts("Livret A", EnvelopeKind.Savings)
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
    private static EnvelopePerformance.Facts Closed() => new EnvelopeFacts("Ancien", EnvelopeKind.Savings) { ClosedOn = D(2026, 3, 31) }
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
    public void One_envelopes_annual_return_is_the_households()
    {
        var life = Life();

        var o = Build([life]);

        var own = EnvelopeMath.At(life.Values, life.Flows, Currency.Eur, ValuationCadence.Monthly, null, Today).AnnualReturnPct;
        Assert.NotNull(own);
        Assert.Equal(own, o.AnnualReturnPct);
    }

    [Fact]
    public void The_kinds_the_persons_and_the_rows_add_up_to_the_value()
    {
        var (savings, pea) = Transfer();

        var o = Build([Life(), savings, pea, NoValue()]);

        Assert.Equal(["life_insurance", "savings", "pea"], o.ByKind.Select(k => k.Kind));
        Assert.Equal(N(o.Value), o.ByKind.Sum(k => N(k.Value)));
        // D7: Bob, created first, owns half of the life insurance; Alice the rest.
        Assert.Equal([Bob.Value, Alice.Value], o.ByPerson.Select(p => p.PersonId));
        Assert.Equal((13_500m, 2_500m), (N(o.ByPerson[0].Value), N(o.ByPerson[0].Gain)));
        Assert.Equal(N(o.Value), o.ByPerson.Sum(p => N(p.Value)));
        // Largest value first, the envelope without a value last.
        Assert.Equal(["Assurance-vie", "Livret A", "PEA", "PER"], o.Envelopes.Select(e => e.Name));
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
        Assert.Equal(["life_insurance", "savings"], february.ByKind.Select(k => k.Kind));
        Assert.DoesNotContain(o.Points.Single(p => p.On == D(2026, 3, 31)).ByKind, k => k.Kind == "savings");
    }

    [Fact]
    public void All_starts_at_the_first_reading()
    {
        var o = Build([Life()], OverviewSpan.All);

        Assert.Equal(D(2024, 12, 31), o.Points[0].On);
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
        var missing = new EnvelopeFacts("Sans ouverture", EnvelopeKind.Savings).Value(D(2026, 10, 1), 500m).Build();
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
}
