using System.Globalization;
using Nid.RealEstate.Application;
using Nid.RealEstate.Contracts;

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
}
