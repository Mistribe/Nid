namespace Nid.Banking.Domain;

/// <summary>
/// The seed category a bank's structured transaction code points at (spec §Hints). Only unambiguous codes: an ISO 20022 code
/// (<c>Domn/Fmly/SubFmly</c>, or Enable Banking's <c>Fmly/SubFmly</c> without the domain; upper-case, segment-wise) or an OFX
/// <c>TRNTYPE</c> token. Free text and proprietary codes (<c>12/32</c>) never match.
/// </summary>
public static class CategoryHints
{
    private static readonly HashSet<string> OfxFees = ["FEE", "SRVCHG"];

    public static string? KeyFor(string? bankCode, decimal amount)
    {
        if (bankCode is not { Length: > 0 })
            return null;
        // Cash withdrawals and bank fees are debits: a credit with the same code (a fee refund) says nothing.
        if (bankCode is "ATM")
            return amount < 0 ? "internal.cash" : null;
        if (OfxFees.Contains(bankCode))
            return amount < 0 ? "bank" : null;
        var parts = bankCode.Split('/');
        if (parts.Any(p => p.Length == 0 || !p.All(c => c is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_')))
            return null;
        return parts switch
        {
            ["PMNT", "CCRD", "CWDL", ..] or ["CCRD", "CWDL"] when amount < 0 => "internal.cash",
            ["ACMT", "MDOP", "CHRG", ..] or ["PMNT", _, "CHRG", ..] or [_, "CHRG"] when amount < 0 => "bank",
            ["PMNT", "RCDT", "SALA", ..] or ["RCDT", "SALA"] when amount > 0 => "income.salary",
            _ => null,
        };
    }
}
