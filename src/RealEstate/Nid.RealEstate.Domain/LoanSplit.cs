using Nid.RealEstate.Contracts;

namespace Nid.RealEstate.Domain;

/// <summary>A loan that can take a part: its current rate and its balance at the repayment's date (after the payment due that day).</summary>
public sealed record SplitCandidate(Guid LoanId, decimal Rate, decimal Balance);

/// <summary>A candidate in the split's order and what it takes (0 once the amount is spent).</summary>
public sealed record SplitPart(Guid LoanId, decimal Amount, bool Settles);

public static class LoanSplit
{
    /// <summary>
    /// D9: the candidates in the rule's order — the highest rate first, or the smallest balance first, or the chosen loan alone
    /// — a tie going to the smallest balance, then the identifier; each takes the lesser of what is left and its balance, so a
    /// loan that takes its whole balance is settled. <paramref name="chosen"/> is read only with <see cref="SplitRule.Chosen"/>;
    /// the candidates it leaves out take nothing. The caller has checked that the amount does not exceed their balances.
    /// </summary>
    public static IReadOnlyList<SplitPart> Split(IReadOnlyList<SplitCandidate> candidates, decimal amount, SplitRule rule, Guid? chosen = null)
    {
        var ordered = rule switch
        {
            SplitRule.HighestRate => candidates.OrderByDescending(c => c.Rate).ThenBy(c => c.Balance).ThenBy(c => c.LoanId),
            SplitRule.SmallestBalance => candidates.OrderBy(c => c.Balance).ThenBy(c => c.LoanId),
            _ => candidates.OrderBy(c => c.LoanId == chosen ? 0 : 1).ThenBy(c => c.Balance).ThenBy(c => c.LoanId),
        };
        var left = amount;
        var parts = new List<SplitPart>();
        foreach (var c in ordered)
        {
            var part = rule == SplitRule.Chosen && c.LoanId != chosen ? 0m : Math.Min(left, c.Balance);
            left -= part;
            parts.Add(new SplitPart(c.LoanId, part, part > 0m && part == c.Balance));
        }
        return parts;
    }
}
