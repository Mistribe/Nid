using Nid.Investments.Contracts;

namespace Nid.Investments.Domain;

/// <summary>Holdings spec §3–§4. Pure, exact decimals, in the envelope's currency.</summary>
public static class HoldingMath
{
    /// <summary>ISO 6166: two letters, nine letters or digits, a Luhn check digit over the letters read as 10–35.</summary>
    public static bool IsIsin(string? isin)
    {
        if (isin is not { Length: 12 } || !char.IsAsciiLetterUpper(isin[0]) || !char.IsAsciiLetterUpper(isin[1])
            || !isin[2..11].All(c => char.IsAsciiDigit(c) || char.IsAsciiLetterUpper(c)) || !char.IsAsciiDigit(isin[11]))
            return false;
        var digits = string.Concat(isin.Select(c => char.IsAsciiDigit(c) ? c.ToString() : (c - 'A' + 10).ToString()));
        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var d = digits[digits.Length - 1 - i] - '0';
            if (i % 2 == 1)
            {
                d *= 2;
                if (d > 9) d -= 9;
            }
            sum += d;
        }
        return sum % 10 == 0;
    }

    public static decimal Total(HoldingSnapshot snapshot) => snapshot.Lines.Sum(l => l.Value);

    /// <summary>Each line's share of the snapshot's total, rounded to 0.1 %; null when the total is zero.</summary>
    public static decimal? Weight(HoldingSnapshot snapshot, SnapshotLine line)
        => Total(snapshot) is var total && total == 0 ? null : EnvelopeMath.RoundPercent(line.Value / total * 100m);

    /// <summary>
    /// The value as printed against units × price (more than 0.01 apart), and the total against the envelope's valuation of the same
    /// date (more than 1 apart). Warnings only: the household validates (spec D6).
    /// </summary>
    public static IReadOnlyList<HoldingWarning> Warnings(HoldingSnapshot snapshot, decimal? valuationSameDay)
    {
        var warnings = snapshot.Lines
            .Where(l => l is { Units: { } u, UnitPrice: { } p } && Math.Abs(u * p - l.Value) > 0.01m)
            .Select(l => new HoldingWarning(HoldingWarnings.ValueMismatch, l.HoldingId))
            .ToList();
        if (valuationSameDay is { } valuation && Math.Abs(Total(snapshot) - valuation) > 1m)
            warnings.Add(new HoldingWarning(HoldingWarnings.SumMismatch));
        return warnings;
    }
}
