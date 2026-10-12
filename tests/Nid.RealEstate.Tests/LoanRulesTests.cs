using System.Globalization;
using Nid.RealEstate.Application;
using Nid.RealEstate.Contracts;
using NodaTime;

namespace Nid.RealEstate.Tests;

/// <summary>Spec 3A2 §3's rules beyond the engine, and precisions 2–4.</summary>
public class LoanRulesTests
{
    private static LoanContract Contract(decimal principal = 200_000m, decimal rate = 0.035m, decimal? insurance = null, string? note = null)
        => new(principal, Loans.Disbursed, Loans.First, 240, InterestRounding.HalfUp, FirstPeriod.Full,
            [new LoanPhaseEntry(new LoanPhase(1, PhaseMode.Annuity, rate, RateType.Fixed), note)],
            insurance is { } monthly ? [new LoanInsuranceEntry(new LoanInsurance(InsuranceMode.FixedMonthly, monthly, null, Loans.First), null)] : []);

    [Theory]
    [InlineData("1000.005", "An amount has at most two decimals.")]
    [InlineData("1000000000000", "An amount is below 10^12.")]
    [InlineData("-1000000000000", "An amount is below 10^12.")]
    public void An_amount_has_two_decimals_and_stays_below_ten_to_the_twelfth(string amount, string message)
    {
        var refused = LoanRules.Amount(decimal.Parse(amount, CultureInfo.InvariantCulture), "principal");
        Assert.Equal((LoanCodes.InvalidLoan, "principal", message), (refused!.Code, Loans.FieldOf(refused), refused.Message));
    }

    [Fact]
    public void An_amount_within_bounds_passes()
    {
        Assert.Null(LoanRules.Amount(999_999_999_999.99m, "principal"));
        Assert.Null(LoanRules.Amount((decimal?)null, "penalty"));
    }

    [Fact]
    public void A_rate_keeps_six_decimals_and_a_seventh_is_refused()
    {
        Assert.Null(LoanRules.Contract(Contract(rate: 0.012345m)));
        Assert.Equal("phases[0].annualRate", Loans.FieldOf(LoanRules.Contract(Contract(rate: 0.0350001m))!));
    }

    [Fact]
    public void A_contract_names_the_field_of_its_first_refusal()
    {
        Assert.Equal("principal", Loans.FieldOf(LoanRules.Contract(Contract(principal: 0.001m))!));
        Assert.Equal("insurances[0].monthlyAmount", Loans.FieldOf(LoanRules.Contract(Contract(insurance: 20.005m))!));
        Assert.Equal("phases[0].note", Loans.FieldOf(LoanRules.Contract(Contract(note: new string('x', 2001)))!));
    }

    [Fact]
    public void Names_lenders_and_notes_are_bounded()
    {
        Assert.Equal("name", Loans.FieldOf(LoanRules.Name("  ")!));
        Assert.Equal("name", Loans.FieldOf(LoanRules.Name(new string('x', 101))!));
        Assert.Equal("lender", Loans.FieldOf(LoanRules.Details(new string('x', 101), null)!));
        Assert.Equal("note", Loans.FieldOf(LoanRules.Details(null, new string('x', 2001))!));
        Assert.Null(LoanRules.Details("Banque", "note"));
    }

    [Fact]
    public void An_insurance_rate_and_monthly_amount_are_bounded_whatever_its_mode() // spec 3B1 §4.2
    {
        LoanContract With(LoanInsurance insurance) => Contract() with { Insurances = [new LoanInsuranceEntry(insurance, null)] };

        Assert.Equal("insurances[0].annualRate", Loans.FieldOf(LoanRules.Contract(With(new LoanInsurance(InsuranceMode.FixedMonthly, 20m, 1m, Loans.First)))!));
        Assert.Equal("insurances[0].annualRate", Loans.FieldOf(LoanRules.Contract(With(new LoanInsurance(InsuranceMode.FixedMonthly, 20m, -0.001m, Loans.First)))!));
        Assert.Equal("insurances[0].monthlyAmount", Loans.FieldOf(LoanRules.Contract(With(new LoanInsurance(InsuranceMode.RateOnInitial, 0m, 0.003m, Loans.First)))!));
        Assert.Null(LoanRules.Contract(With(new LoanInsurance(InsuranceMode.RateOnInitial, null, 0.003m, Loans.First))));
    }

    [Fact]
    public void Every_contract_date_lies_in_years_1900_to_2200() // spec 3B1 §4.2
    {
        Assert.Equal("disbursedOn", Loans.FieldOf(LoanRules.Contract(Contract() with { DisbursedOn = new LocalDate(1899, 12, 31) })!));
        Assert.Equal("firstPaymentDate", Loans.FieldOf(LoanRules.Contract(Contract() with { FirstPaymentDate = new LocalDate(2201, 1, 1) })!));
        var late = new LoanInsurance(InsuranceMode.FixedMonthly, 20m, null, Loans.First, new LocalDate(2201, 1, 1));
        Assert.Equal("insurances[0].endsOn", Loans.FieldOf(LoanRules.Contract(Contract() with { Insurances = [new LoanInsuranceEntry(late, null)] })!));
        Assert.Null(LoanRules.Contract(Contract() with { DisbursedOn = new LocalDate(1900, 1, 1), FirstPaymentDate = new LocalDate(2200, 12, 31) }));
    }

    [Fact]
    public void A_contract_has_at_most_twenty_insurances() // spec 3B1 §4.2
    {
        var one = new LoanInsuranceEntry(new LoanInsurance(InsuranceMode.FixedMonthly, 1m, null, Loans.First), null);
        Assert.Null(LoanRules.Contract(Contract() with { Insurances = Enumerable.Repeat(one, 20).ToList() }));
        Assert.Equal("insurances", Loans.FieldOf(LoanRules.Contract(Contract() with { Insurances = Enumerable.Repeat(one, 21).ToList() })!));
    }
}
