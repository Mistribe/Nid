using Nid.Core.Contracts;
using Nid.Investments.Application;
using Nid.Investments.Contracts;
using Nid.Investments.Domain;
using NodaTime;

namespace Nid.Investments.Tests;

/// <summary>Issues #399, #400, #401: ceilings, the fortnight rule against service-public.gouv.fr's examples, tax dates.</summary>
public class RegulatedSavingsTests
{
    private static readonly ItemId Item = ItemId.New();
    private static LocalDate D(int y, int m, int d) => new(y, m, d);
    private static Money Eur(decimal amount) => Money.Create(amount, Currency.Eur).TryGetValue(out var m) ? m : throw new InvalidOperationException();
    private static Flow Flow(LocalDate on, FlowKind kind, decimal amount) => new(Guid.NewGuid(), Item, on, kind, Eur(amount), null, null, null);
    private static Valuation Value(LocalDate on, decimal amount) => new(Guid.NewGuid(), Item, on, Eur(amount), null);

    [Fact]
    public void Ceilings_are_dated_rules_with_their_source()
    {
        Assert.Equal(22_950m, RegulatedSavings.Ceiling(RegulatedKind.LivretA, D(2026, 11, 5))!.Value);
        Assert.Equal(19_125m, RegulatedSavings.Ceiling(RegulatedKind.LivretA, D(2012, 12, 31))!.Value);
        Assert.Null(RegulatedSavings.Ceiling(RegulatedKind.LivretA, D(2012, 9, 30)));
        Assert.Equal(12_000m, RegulatedSavings.Ceiling(RegulatedKind.Ldds, D(2026, 11, 5))!.Value);
        Assert.Equal(10_000m, RegulatedSavings.Ceiling(RegulatedKind.Lep, D(2026, 11, 5))!.Value);
        Assert.Equal(1_600m, RegulatedSavings.Ceiling(RegulatedKind.LivretJeune, D(2026, 11, 5))!.Value);
        Assert.Contains("R. 221-2", RegulatedSavings.Ceiling(RegulatedKind.LivretA, D(2026, 11, 5))!.Source);
    }

    [Fact]
    public void Rates_change_on_their_date_and_the_ldds_follows_the_livret_a()
    {
        Assert.Equal(1.7m, RegulatedSavings.Rate(RegulatedKind.LivretA, D(2026, 1, 31))!.Value);
        Assert.Equal(1.5m, RegulatedSavings.Rate(RegulatedKind.LivretA, D(2026, 2, 1))!.Value);
        Assert.Equal(1.7m, RegulatedSavings.Rate(RegulatedKind.Ldds, D(2026, 8, 1))!.Value);
        Assert.Equal(2.5m, RegulatedSavings.Rate(RegulatedKind.Lep, D(2026, 11, 5))!.Value);
        Assert.Null(RegulatedSavings.Rate(RegulatedKind.LivretJeune, D(2026, 11, 5)));
    }

    // 2026: 1,7 % in January, 1,5 % from 1 February, 1,7 % from 1 August (2 + 12 + 10 fortnights).
    [Fact]
    public void A_balance_kept_all_year_earns_the_year_s_mean_rate()
    {
        var e = RegulatedSavings.Interest(RegulatedKind.LivretA, [Value(D(2026, 1, 2), 10_000m)], [], D(2026, 12, 31))!;
        Assert.Equal(160.00m, e.Expected); // 10 000 × (2 × 1,7 + 12 × 1,5 + 10 × 1,7) / 24 %: the 1,6 % announced for 2026
    }

    [Theory]
    // « Dépôt entre le 1er et le 15 mai : pris en compte le 16 mai »: 5 fortnights at 1,5 %, 10 at 1,7 %.
    [InlineData(10, 10.21)]
    // « Dépôt entre le 16 et le 31 mai : pris en compte le 1er juin »: 4 at 1,5 %, 10 at 1,7 %.
    [InlineData(20, 9.58)]
    public void A_deposit_counts_from_the_next_fortnight(int day, decimal expected)
    {
        var e = RegulatedSavings.Interest(RegulatedKind.LivretA, [], [Flow(D(2026, 5, day), FlowKind.Contribution, 1_000m)], D(2026, 11, 5))!;
        Assert.Equal(expected, e.Expected);
    }

    [Theory]
    // « Retrait entre le 1er et le 15 mai : solde arrêté au 30 avril »: 2 000 to April, 1 000 from 1 May.
    [InlineData(10, 21.17)]
    // « Retrait entre le 16 et le 31 mai : solde arrêté au 15 mai »: 2 000 to 15 May.
    [InlineData(20, 21.79)]
    public void A_withdrawal_stops_earning_from_the_start_of_its_fortnight(int day, decimal expected)
    {
        var e = RegulatedSavings.Interest(RegulatedKind.LivretA, [Value(D(2026, 1, 2), 2_000m)],
            [Flow(D(2026, 5, day), FlowKind.Withdrawal, 1_000m)], D(2026, 11, 5))!;
        Assert.Equal(expected, e.Expected);
    }

    [Fact]
    public void Interest_to_date_counts_the_fortnights_over()
    {
        var e = RegulatedSavings.Interest(RegulatedKind.LivretA, [Value(D(2026, 1, 2), 10_000m)], [], D(2026, 11, 5))!;
        Assert.Equal(131.67m, e.ToDate); // to 31 October: 2 × 1,7 + 12 × 1,5 + 6 × 1,7 = 31,6, × 10 000 / 24 %
        Assert.Equal(160.00m, e.Expected);
        Assert.Equal(1.7m, e.RatePct);
        Assert.Equal(2026, e.Year);
    }

    [Fact]
    public void Interest_ignores_the_flows_recorded_after_today()
    {
        // Already recorded, still to come: the estimate holds no money in or out after today.
        Flow[] later = [Flow(D(2026, 11, 20), FlowKind.Contribution, 5_000m), Flow(D(2026, 12, 1), FlowKind.Withdrawal, 8_000m)];
        var e = RegulatedSavings.Interest(RegulatedKind.LivretA, [Value(D(2026, 1, 2), 10_000m)], later, D(2026, 11, 5))!;
        Assert.Equal((131.67m, 160.00m), (e.ToDate, e.Expected));
    }

    [Fact]
    public void No_estimate_without_a_balance_or_a_regulated_rate()
    {
        Assert.Null(RegulatedSavings.Interest(RegulatedKind.LivretA, [], [], D(2026, 11, 5)));
        Assert.Null(RegulatedSavings.Interest(RegulatedKind.LivretJeune, [Value(D(2026, 1, 2), 1_000m)], [], D(2026, 11, 5)));
        Assert.Null(RegulatedSavings.Interest(RegulatedKind.LivretA, [Value(D(2019, 1, 2), 1_000m)], [], D(2019, 11, 5))); // before the table
    }

    [Fact]
    public void The_balance_reads_from_the_nearest_valuation()
    {
        Valuation[] values = [Value(D(2026, 1, 2), 5_000m)];
        Flow[] flows = [Flow(D(2026, 1, 1), FlowKind.Contribution, 200m), Flow(D(2026, 3, 1), FlowKind.Contribution, 300m), Flow(D(2026, 4, 1), FlowKind.Withdrawal, 100m)];
        Assert.Equal(4_800m, RegulatedSavings.BalanceAt(values, flows, D(2025, 12, 31)));
        Assert.Equal(5_200m, RegulatedSavings.BalanceAt(values, flows, D(2026, 4, 30)));
        Assert.Equal(300m, RegulatedSavings.BalanceAt([], [Flow(D(2026, 3, 1), FlowKind.Opening, 300m)], D(2026, 4, 30)));
        Assert.Null(RegulatedSavings.BalanceAt([], [], D(2026, 4, 30)));
    }

    [Fact]
    public void The_room_left_is_the_ceiling_minus_the_balance_and_never_negative()
    {
        // The household's livrets: Livret A (Alice) can take about 3 600 €, the Livret A (Bob), over through interest, none.
        var alice = new EnvelopeFacts("Livret A Alice", EnvelopeKind.Savings) { Regulated = RegulatedKind.LivretA }.Value(D(2026, 1, 2), 19_350m).Build();
        var bob = new EnvelopeFacts("Livret A Bob", EnvelopeKind.Savings) { Regulated = RegulatedKind.LivretA }.Value(D(2026, 1, 2), 23_104.60m).Build();
        var plain = new EnvelopeFacts("Compte", EnvelopeKind.Savings).Value(D(2026, 1, 2), 30_000m).Build();
        var today = D(2026, 11, 5);

        Assert.Equal(("3600", false), (EnvelopeInsights.Ceiling(alice, today)!.Room.Amount, EnvelopeInsights.Ceiling(alice, today)!.Reached));
        Assert.Equal(("0", true), (EnvelopeInsights.Ceiling(bob, today)!.Room.Amount, EnvelopeInsights.Ceiling(bob, today)!.Reached));
        Assert.Null(EnvelopeInsights.Ceiling(plain, today));
    }

    [Fact]
    public void The_watch_names_a_livret_at_its_ceiling_then_the_room_on_the_others()
    {
        var alice = new EnvelopeFacts("Livret A Alice", EnvelopeKind.Savings) { Regulated = RegulatedKind.LivretA }.Value(D(2026, 1, 2), 19_350m).Build();
        var bob = new EnvelopeFacts("Livret A Bob", EnvelopeKind.Savings) { Regulated = RegulatedKind.LivretA }.Value(D(2026, 1, 2), 23_104.60m).Build();
        var today = D(2026, 11, 5);

        var watch = InvestmentsOverviewMath.InsightWatch([alice, bob], [], today).ToList();
        Assert.Contains(watch, w => w.Code == OverviewCodes.CeilingReached && w.EnvelopeId == bob.Item.Id);
        Assert.Contains(watch, w => w.Code == OverviewCodes.CeilingRoom && w.EnvelopeId == alice.Item.Id && w.Amount!.Amount == "3600");
        Assert.Empty(InvestmentsOverviewMath.InsightWatch([alice], [], today)); // none at its ceiling: nothing to watch
    }

    [Fact]
    public void A_life_insurance_reaches_8_years_from_its_first_deposit_or_opening()
    {
        Flow[] flows = [Flow(D(2020, 10, 15), FlowKind.Opening, 10_000m)];
        Assert.Equal(new TaxMilestone(TaxMilestones.LifeInsurance8Years, D(2028, 10, 15)),
            TaxMilestones.Next(EnvelopeKind.LifeInsurance, null, flows, null, D(2026, 11, 5)));
        Assert.Equal(D(2028, 10, 1), TaxMilestones.Next(EnvelopeKind.LifeInsurance, D(2020, 10, 1), flows, null, D(2026, 11, 5))!.On);
        Assert.Null(TaxMilestones.Next(EnvelopeKind.LifeInsurance, D(2010, 1, 1), [], null, D(2026, 11, 5))); // past
        Assert.Null(TaxMilestones.Next(EnvelopeKind.LifeInsurance, null, [], null, D(2026, 11, 5))); // no start
    }

    [Fact]
    public void A_pea_reaches_5_years_and_a_per_the_owner_s_retirement()
    {
        Assert.Equal(new TaxMilestone(TaxMilestones.Pea5Years, D(2027, 1, 20)), TaxMilestones.Next(EnvelopeKind.Pea, D(2022, 1, 20), [], null, D(2026, 11, 5)));
        Assert.Equal(new TaxMilestone(TaxMilestones.PerRetirement, D(2049, 3, 2)), TaxMilestones.Next(EnvelopeKind.Per, D(2020, 1, 1), [], D(2049, 3, 2), D(2026, 11, 5)));
        Assert.Null(TaxMilestones.Next(EnvelopeKind.Per, D(2020, 1, 1), [], null, D(2026, 11, 5)));
        Assert.Null(TaxMilestones.Next(EnvelopeKind.Savings, D(2020, 1, 1), [], null, D(2026, 11, 5)));
    }

    [Fact]
    public void A_per_s_date_is_its_main_owner_s_retirement_and_the_watch_looks_90_days_ahead()
    {
        var owner = PersonId.New();
        var per = new EnvelopeFacts("PER", EnvelopeKind.Per, "EUR", (owner, 10_000)).Build();
        PersonInfo[] persons = [new(owner, PersonKind.Adult, "Alice", null, 0, BirthDate: D(1963, 1, 20), RetirementAge: 64)];
        Assert.Equal(D(2027, 1, 20), EnvelopeInsights.Milestone(per, persons, D(2026, 11, 5))!.On);
        Assert.Single(InvestmentsOverviewMath.InsightWatch([per], persons, D(2026, 11, 5)), w => w.Code == OverviewCodes.TaxMilestone && w.Milestone == TaxMilestones.PerRetirement);
        Assert.Empty(InvestmentsOverviewMath.InsightWatch([per], persons, D(2026, 10, 1))); // 111 days ahead
    }
}
