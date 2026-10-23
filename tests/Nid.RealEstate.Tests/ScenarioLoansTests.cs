using Nid.Core.Contracts;
using Nid.RealEstate.Application;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;

namespace Nid.RealEstate.Tests;

/// <summary>Spec §3: a scenario's hypothetical loans go through the real engine.</summary>
public class ScenarioLoansTests
{
    private static readonly LocalDate On = new(2027, 3, 10);
    private static readonly ScenarioInsurance NoInsurance = new(ScenarioInsuranceKind.FixedMonthly, 0m);

    private static IReadOnlyList<SimulatedLoan> Run(params ScenarioLoan[] loans)
    {
        Assert.True(new ScenarioLoans().Simulate(Currency.Eur, On, loans).TryGetValue(out var simulated));
        return simulated!;
    }

    [Fact]
    public void A_scenario_loan_has_the_schedule_of_the_same_loan_entered_for_real()
    {
        var terms = new LoanTerms(
            Currency.Eur, 200_000m, On, On.PlusMonths(1), 240, InterestRounding.HalfUp, FirstPeriod.Full,
            [new LoanPhase(1, PhaseMode.Annuity, 0.035m, RateType.Fixed)],
            [new LoanInsurance(InsuranceMode.RateOnInitial, null, 0.003m, On)], [], []);
        Assert.True(LoanScheduleBuilder.Build(terms).TryGetValue(out var real));

        var loan = new ScenarioLoan("Main", 200_000m, 0.035m, 240, null, new(ScenarioInsuranceKind.RateOnInitial, 0.003m), 0m, 0m);
        var rows = Run(loan).Single().Rows;

        Assert.Equal(real!.Rows.Count, rows.Count);
        for (var i = 0; i < rows.Count; i++)
            Assert.Equal(
                (real.Rows[i].Number, real.Rows[i].DueDate, real.Rows[i].Payment, real.Rows[i].Interest, real.Rows[i].Principal, real.Rows[i].Insurance, real.Rows[i].Balance),
                (rows[i].Number, rows[i].DueDate, rows[i].Payment, rows[i].Interest, rows[i].Principal, rows[i].Insurance, rows[i].Balance));
    }

    [Fact]
    public void A_zero_rate_ptz_in_total_deferral_pays_nothing_first() // Review Focus 5
    {
        var rows = Run(new ScenarioLoan("PTZ", 50_000m, 0m, 240, new(DeferralKind.Total, 60), NoInsurance, 0m, 0m)).Single().Rows;

        Assert.All(rows.Take(60), r => Assert.Equal(0m, r.Payment));
        Assert.Equal(277.78m, rows[60].Payment);
        Assert.Equal(0m, rows[^1].Balance);
    }

    [Fact]
    public void Fees_are_summed_per_loan()
        => Assert.Equal(3400m, Run(new ScenarioLoan("Main", 100_000m, 0.03m, 180, null, NoInsurance, 1_000m, 2_400m)).Single().Fees);

    [Fact]
    public void An_engine_refusal_names_the_loan()
    {
        var fine = new ScenarioLoan("Main", 100_000m, 0.03m, 180, null, NoInsurance, 0m, 0m);
        var impossible = fine with { Months = 0 };
        Assert.True(new ScenarioLoans().Simulate(Currency.Eur, On, [fine, impossible]).TryGetFailure(out var failure));
        Assert.Equal("scenario.invalid_field", failure!.Code);
        Assert.Equal("loans[1]", LoanPaths.FieldOf(failure));
    }
}
