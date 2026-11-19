namespace Nid.Banking.Domain;

/// <summary>
/// The seed category a bank's structured transaction code points at (spec §Hints). Only unambiguous codes: an ISO 20022 code
/// (<c>Domn/Fmly/SubFmly</c>, upper-case, segment-wise) or an OFX <c>TRNTYPE</c> token. Free text never matches.
/// </summary>
public static class CategoryHints
{
    private static readonly HashSet<string> OfxFees = ["FEE", "SRVCHG"];

    public static string? KeyFor(string? bankCode, decimal amount)
    {
        if (bankCode is not { Length: > 0 })
            return null;
        if (bankCode is "ATM")
            return "internal.cash";
        if (OfxFees.Contains(bankCode))
            return "bank";
        var parts = bankCode.Split('/');
        if (parts.Any(p => p.Length == 0 || !p.All(c => c is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_')))
            return null;
        return parts switch
        {
            ["PMNT", "CCRD", "CWDL", ..] => "internal.cash",
            ["ACMT", "MDOP", "CHRG", ..] or ["PMNT", _, "CHRG", ..] => "bank",
            ["PMNT", "RCDT", "SALA", ..] when amount > 0 => "income.salary",
            _ => null,
        };
    }
}
