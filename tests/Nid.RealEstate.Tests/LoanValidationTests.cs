using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;

namespace Nid.RealEstate.Tests;

public class LoanValidationTests
{
    private static (string Code, string? Field)? Refusal(LoanTerms terms)
        => LoanValidation.Check(terms) is { } f ? (f.Code, Loans.FieldOf(f)) : null;

    [Fact]
    public void A_valid_loan_passes() => Assert.Null(Refusal(Loans.Golden()));

    public static TheoryData<string, LoanTerms> Invalid() => new()
    {
        { "principal", Loans.Golden() with { Principal = 0m } },
        { "firstPaymentDate", Loans.Golden() with { FirstPaymentDate = Loans.Disbursed.PlusDays(-1) } },
        { "totalPayments", Loans.Golden() with { TotalPayments = 0 } },
        { "totalPayments", Loans.Golden() with { TotalPayments = 601 } },
        { "phases", Loans.Golden() with { Phases = [] } },
        { "phases[1].seq", Loans.Golden() with { Phases = [new LoanPhase(1, PhaseMode.Annuity, 0.03m, RateType.Fixed, 12), new LoanPhase(3, PhaseMode.Annuity, 0.03m, RateType.Fixed)] } },
        { "phases[0].payments", Loans.Golden() with { Phases = [new LoanPhase(1, PhaseMode.Annuity, 0.03m, RateType.Fixed), new LoanPhase(2, PhaseMode.Annuity, 0.03m, RateType.Fixed)] } },
        { "phases", Loans.Golden() with { Phases = [new LoanPhase(1, PhaseMode.Annuity, 0.03m, RateType.Fixed, 200), new LoanPhase(2, PhaseMode.Annuity, 0.03m, RateType.Fixed, 50)] } },
        { "phases[0].annualRate", Loans.Golden() with { Phases = [new LoanPhase(1, PhaseMode.Annuity, -0.01m, RateType.Fixed)] } },
        { "phases[0].annualRate", Loans.Golden() with { Phases = [new LoanPhase(1, PhaseMode.Annuity, 1m, RateType.Fixed)] } },
        { "phases[0].paymentOverride", Loans.Golden() with { Phases = [new LoanPhase(1, PhaseMode.Annuity, 0.03m, RateType.Fixed, PaymentOverride: 0m)] } },
        { "insurances[0].monthlyAmount", Loans.Golden() with { Insurances = [new LoanInsurance(InsuranceMode.FixedMonthly, null, null, Loans.First)] } },
        { "insurances[0].annualRate", Loans.Golden() with { Insurances = [new LoanInsurance(InsuranceMode.RateOnRemaining, null, null, Loans.First)] } },
        { "insurances[0].endsOn", Loans.Golden() with { Insurances = [new LoanInsurance(InsuranceMode.FixedMonthly, 10m, null, Loans.First, Loans.First)] } },
        { "fees[0].amount", Loans.Golden() with { Fees = [new LoanFee(FeeKind.File, 0m, Loans.Disbursed)] } },
        { "events[0].amount", Loans.Golden() with { Events = [new EarlyRepayment(Loans.First, 0m, RepaymentEffect.ReduceTerm)] } },
        { "events[0].onDate", Loans.Golden() with { Events = [new EarlyRepayment(Loans.Disbursed.PlusDays(-1), 10m, RepaymentEffect.ReduceTerm)] } },
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void An_invalid_loan_is_refused_at_its_field(string field, LoanTerms terms)
    {
        var refusal = Refusal(terms);
        Assert.NotNull(refusal);
        Assert.Equal(LoanCodes.InvalidLoan, refusal!.Value.Code);
        Assert.Equal(field, refusal.Value.Field);
    }
}
