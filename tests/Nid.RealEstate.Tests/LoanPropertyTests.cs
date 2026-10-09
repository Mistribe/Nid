using Nid.Core.Contracts;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;

namespace Nid.RealEstate.Tests;

/// <summary>loans.md §12's properties over 500 generated valid loans (fixed seed).</summary>
public class LoanPropertyTests
{
    private static readonly decimal[] Rates = [0m, 0.0076m, 0.0125m, 0.035m, 0.042m, 0.076m, 0.08m];

    public static IEnumerable<object[]> Generated()
    {
        var rng = new Random(20261009);
        for (var n = 0; n < 500; n++)
        {
            var total = rng.Next(12, 361);
            var principal = rng.Next(5_000_00, 400_000_00) / 100m;
            var rate = Rates[rng.Next(Rates.Length)];
            LoanPhase[] phases = rng.Next(4) switch
            {
                0 => [new LoanPhase(1, PhaseMode.DeferredTotal, rate, RateType.Fixed, rng.Next(1, total / 4 + 1)), new LoanPhase(2, PhaseMode.Annuity, rate, RateType.Fixed)],
                1 => [new LoanPhase(1, PhaseMode.DeferredPartial, rate, RateType.Fixed, rng.Next(1, total / 4 + 1)), new LoanPhase(2, PhaseMode.Annuity, rate, RateType.Fixed)],
                _ => [new LoanPhase(1, PhaseMode.Annuity, rate, RateType.Fixed)],
            };
            var events = rng.Next(3) == 0
                ? new[] { new EarlyRepayment(Loans.First.PlusMonths(rng.Next(total / 2, total - 1)), Math.Round(principal / (4 * total), 2), rng.Next(2) == 0 ? RepaymentEffect.ReduceTerm : RepaymentEffect.ReducePayment) }
                : [];
            var fees = new[] { new LoanFee(FeeKind.File, 500m, Loans.Disbursed) };
            var insurances = rng.Next(2) == 0 ? new[] { new LoanInsurance(InsuranceMode.RateOnRemaining, null, 0.003m, Loans.First) } : [];
            yield return [new LoanTerms(Currency.Eur, principal, Loans.Disbursed, Loans.First, total, InterestRounding.HalfUp, FirstPeriod.Full, phases, insurances, fees, events)];
        }
    }

    [Theory]
    [MemberData(nameof(Generated))]
    public void Every_generated_loan_keeps_the_schedules_invariants(LoanTerms t)
    {
        var s = Loans.Ok(LoanScheduleBuilder.Build(t));
        Assert.All(s.Rows, r => Assert.True(r.Balance >= 0m, $"row {r.Number} balance {r.Balance}"));
        Assert.Equal(0m, s.Rows[^1].Balance);

        var capitalised = s.Rows.Where(r => r.Payment == 0m && r.Interest > 0m).Sum(r => r.Interest);
        var early = s.Rows.Sum(r => r.EarlyRepayment ?? 0m);
        Assert.Equal(t.Principal + capitalised - early, s.Rows.Sum(r => r.Principal));

        foreach (var phase in s.Rows.Where(r => r.EarlyRepayment is null).GroupBy(r => r.PhaseSeq))
        {
            var annuity = t.Phases.Single(p => p.Seq == phase.Key).Mode == PhaseMode.Annuity;
            if (!annuity) continue;
            var interests = phase.Select(r => r.Interest).ToList();
            for (var i = 1; i < interests.Count; i++)
                Assert.True(interests[i] <= interests[i - 1], $"interest rose at row {phase.ElementAt(i).Number}");
        }

        // Σ payments + early repayments − principal = Σ interest (capitalised interest is repaid inside later payments).
        Assert.Equal(s.Rows.Sum(r => r.Interest), s.Rows.Sum(r => r.Payment) + early - t.Principal);
        var figures = LoanMath.At(t, s, s.Rows[^1].DueDate);
        Assert.Equal(s.Rows.Sum(r => r.Interest) + s.Rows.Sum(r => r.Insurance) + t.Fees.Sum(f => f.Amount), figures.TotalCost);
    }
}
