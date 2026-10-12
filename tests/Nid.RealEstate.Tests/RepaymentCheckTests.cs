using Nid.Core.Contracts;
using Nid.RealEstate.Application;
using Nid.RealEstate.Contracts;
using NodaTime;
using UnambitiousFx.Functional.Failures;

namespace Nid.RealEstate.Tests;

/// <summary>Spec 3A2 §4: a repayment route answers at onDate, amount, effect, penalty; a fee route at kind, amount, onDate.</summary>
public class RepaymentCheckTests
{
    private static readonly LocalDate March2030 = new(2030, 3, 15);

    private static StoredRepayment Stored(LocalDate on, decimal amount, Guid? feeId = null)
        => new(Guid.CreateVersion7(), new EarlyRepayment(on, amount, RepaymentEffect.ReduceTerm), feeId, null, 1);

    /// <summary>What the repayment routes do: the draft as a repayment with its penalty, then the loan rebuilt.</summary>
    private static Failure? Write(StoredLoan loan, RepaymentDraft draft)
    {
        var written = RepaymentCheck.Repayment(Guid.CreateVersion7(), draft, draft.Penalty is null ? null : Guid.CreateVersion7());
        return RepaymentCheck.Build(Currency.Eur, loan, written, RepaymentCheck.Penalty(written, draft));
    }

    private static RepaymentDraft Draft(LocalDate on, decimal amount, decimal? penalty = null, RepaymentEffect effect = RepaymentEffect.ReduceTerm)
        => new(on, amount, effect, penalty, null);

    private static (string Code, string? Field) Located(Failure? failure) => (failure!.Code, Loans.FieldOf(failure));

    [Fact]
    public void A_repayment_refusal_answers_at_the_routes_own_fields()
    {
        var loan = Loans.Stored(repayments: [Stored(new LocalDate(2025, 3, 15), 5_000m)]);

        Assert.Equal((LoanCodes.EventAboveBalance, "amount"), Located(Write(loan, Draft(March2030, 500_000m))));
        Assert.Equal((LoanCodes.InvalidLoan, "amount"), Located(Write(loan, Draft(March2030, 0m))));
        Assert.Equal((LoanCodes.InvalidLoan, "onDate"), Located(Write(loan, Draft(Loans.Disbursed.PlusDays(-1), 10m))));
        Assert.Equal((LoanCodes.InvalidLoan, "penalty"), Located(Write(loan, Draft(March2030, 1_000m, penalty: 0m))));
        Assert.Null(Write(loan, Draft(March2030, 1_000m, penalty: 300m)));
    }

    [Fact]
    public void A_repayment_that_leaves_a_later_one_short_answers_at_its_amount()
    {
        // 140 000 fits the golden case's March 2030 balance (about 152 000); 30 000 repaid in 2026 brings it near 128 000.
        var loan = Loans.Stored(repayments: [Stored(March2030, 140_000m)]);
        Assert.Equal((LoanCodes.EventAboveBalance, "amount"),
            Located(Write(loan, Draft(new LocalDate(2026, 3, 15), 30_000m, effect: RepaymentEffect.ReducePayment))));
    }

    [Fact]
    public void A_correction_replaces_the_repayment_and_its_old_penalty()
    {
        var penalty = new StoredFee(Guid.CreateVersion7(), new LoanFee(FeeKind.EarlyRepaymentPenalty, 300m, March2030), null, 2);
        var stored = Stored(March2030, 100_000m, penalty.Id);
        var loan = Loans.Stored([penalty], [stored]);

        // 120 000 fits alone; beside the 100 000 it replaces, it would not.
        var corrected = stored with { Repayment = stored.Repayment with { Amount = 120_000m }, FeeId = null };
        Assert.Null(RepaymentCheck.Build(Currency.Eur, loan, corrected, null));
    }

    [Fact]
    public void A_draft_has_two_decimals_and_a_bounded_note()
    {
        Assert.Equal("amount", Loans.FieldOf(RepaymentCheck.Rules(Draft(March2030, 10.005m))!));
        Assert.Equal("penalty", Loans.FieldOf(RepaymentCheck.Rules(Draft(March2030, 10m, penalty: 1.234m))!));
        Assert.Equal("note", Loans.FieldOf(RepaymentCheck.Rules(new RepaymentDraft(March2030, 10m, RepaymentEffect.ReduceTerm, null, new string('x', 2001)))!));
        Assert.Null(RepaymentCheck.Rules(Draft(March2030, 10m, penalty: 1.5m)));
    }

    [Fact]
    public void A_fee_has_a_known_kind_a_positive_amount_and_two_decimals()
    {
        Assert.Equal("kind", Loans.FieldOf(FeeCheck.Rules(new FeeDraft((FeeKind)9, 10m, March2030, null))!));
        Assert.Equal("amount", Loans.FieldOf(FeeCheck.Rules(new FeeDraft(FeeKind.File, 0m, March2030, null))!));
        Assert.Equal("amount", Loans.FieldOf(FeeCheck.Rules(new FeeDraft(FeeKind.File, 10.125m, March2030, null))!));
        Assert.Null(FeeCheck.Rules(new FeeDraft(FeeKind.EarlyRepaymentPenalty, 10m, March2030, null))); // precision 10
    }

    [Fact]
    public void A_penalty_or_a_fee_changed_since_is_not_writable_through_the_fee_routes()
    {
        var penalty = new StoredFee(Guid.CreateVersion7(), new LoanFee(FeeKind.EarlyRepaymentPenalty, 300m, March2030), null, 2);
        var file = new StoredFee(Guid.CreateVersion7(), new LoanFee(FeeKind.File, 1_000m, Loans.Disbursed), null, 3);
        var loan = Loans.Stored([penalty, file], [Stored(March2030, 10_000m, penalty.Id)]);

        Assert.Equal((LoanCodes.FeeLinked, "feeId"), Located(FeeCheck.Writable(loan, penalty.Id, 2)));
        Assert.Equal((LoanCodes.FeeConflict, (string?)null), Located(FeeCheck.Writable(loan, file.Id, 2)));
        Assert.IsType<NotFoundFailure>(FeeCheck.Writable(loan, Guid.NewGuid(), 1));
        Assert.Null(FeeCheck.Writable(loan, file.Id, 3));
    }
}
