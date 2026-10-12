using Nid.RealEstate.Contracts;

namespace Nid.RealEstate.Tests;

public class LoanWireTests
{
    [Fact]
    public void Enums_go_on_the_wire_in_snake_case_as_loans_md_writes_them()
    {
        Assert.Equal("half_up", LoanWire.Of(InterestRounding.HalfUp));
        Assert.Equal("prorata_actual_365", LoanWire.Of(FirstPeriod.ProrataActual365));
        Assert.Equal("deferred_partial", LoanWire.Of(PhaseMode.DeferredPartial));
        Assert.Equal("rate_on_remaining", LoanWire.Of(InsuranceMode.RateOnRemaining));
        Assert.Equal("early_repayment_penalty", LoanWire.Of(FeeKind.EarlyRepaymentPenalty));
        Assert.Equal("reduce_term", LoanWire.Of(RepaymentEffect.ReduceTerm));
    }

    [Fact]
    public void Every_value_reads_back_and_anything_else_is_refused()
    {
        RoundTrips<PhaseMode>();
        RoundTrips<RateType>();
        RoundTrips<InterestRounding>();
        RoundTrips<FirstPeriod>();
        RoundTrips<InsuranceMode>();
        RoundTrips<FeeKind>();
        RoundTrips<RepaymentEffect>();
        Assert.False(LoanWire.TryParse<FeeKind>("File", out _));
        Assert.False(LoanWire.TryParse<FeeKind>(null, out _));
    }

    private static void RoundTrips<T>() where T : struct, Enum
    {
        foreach (var value in Enum.GetValues<T>())
        {
            Assert.True(LoanWire.TryParse<T>(LoanWire.Of(value), out var back));
            Assert.Equal(value, back);
        }
    }
}
