using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;

namespace Nid.RealEstate.Tests;

/// <summary>Loans dashboard spec D9: the three rules, their ties, a settled loan and the rest on the next.</summary>
public class LoanSplitTests
{
    private static readonly Guid A = new("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = new("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid C = new("00000000-0000-0000-0000-00000000000c");

    private static IEnumerable<(Guid, decimal, bool)> Of(IReadOnlyList<SplitPart> parts) => parts.Select(p => (p.LoanId, p.Amount, p.Settles));

    [Fact]
    public void Highest_rate_goes_first_and_a_zero_rate_loan_last()
    {
        SplitCandidate[] loans = [new(A, 0m, 40_000m), new(B, 0.035m, 150_000m), new(C, 0.05m, 6_000m)];

        var parts = LoanSplit.Split(loans, 10_000m, SplitRule.HighestRate);

        // The car (5 %) is settled, the rest on the house; the PTZ at 0 % takes nothing.
        Assert.Equal([(C, 6_000m, true), (B, 4_000m, false), (A, 0m, false)], Of(parts));
    }

    [Fact]
    public void Smallest_balance_goes_first()
    {
        SplitCandidate[] loans = [new(A, 0m, 40_000m), new(B, 0.035m, 150_000m), new(C, 0.05m, 6_000m)];

        var parts = LoanSplit.Split(loans, 50_000m, SplitRule.SmallestBalance);

        Assert.Equal([(C, 6_000m, true), (A, 40_000m, true), (B, 4_000m, false)], Of(parts));
    }

    [Fact]
    public void Chosen_puts_everything_on_one_loan()
    {
        SplitCandidate[] loans = [new(A, 0m, 40_000m), new(B, 0.035m, 150_000m), new(C, 0.05m, 6_000m)];

        var parts = LoanSplit.Split(loans, 20_000m, SplitRule.Chosen, B);

        Assert.Equal((B, 20_000m, false), Of(parts).First());
        Assert.All(parts.Skip(1), p => Assert.Equal(0m, p.Amount));
    }

    [Fact]
    public void A_tie_on_the_rate_goes_to_the_smallest_balance_then_the_identifier()
    {
        SplitCandidate[] loans = [new(C, 0.03m, 1_000m), new(B, 0.03m, 500m), new(A, 0.03m, 500m)];

        var parts = LoanSplit.Split(loans, 600m, SplitRule.HighestRate);

        Assert.Equal([(A, 500m, true), (B, 100m, false), (C, 0m, false)], Of(parts));
    }

    [Fact]
    public void A_tie_on_the_balance_goes_to_the_identifier()
    {
        SplitCandidate[] loans = [new(B, 0.01m, 500m), new(A, 0.02m, 500m)];

        Assert.Equal([A, B], LoanSplit.Split(loans, 100m, SplitRule.SmallestBalance).Select(p => p.LoanId));
    }
}
