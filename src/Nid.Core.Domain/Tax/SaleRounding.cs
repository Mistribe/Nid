namespace Nid.Core.Domain.Tax;

/// <summary>Sale tax spec §3: each amount rounded to the euro at the end, like the return.</summary>
internal static class SaleRounding
{
    public static decimal Euros(decimal amount) => Math.Round(amount, 0, MidpointRounding.AwayFromZero);

    // Largest remainder: each share rounded down, the euros left going to the largest fractions, so the shares sum to the rounded total.
    public static decimal[] Apportion(IReadOnlyList<decimal> exact)
    {
        var shares = exact.Select(Math.Floor).ToArray();
        var left = (int)(Euros(exact.Sum()) - shares.Sum());
        foreach (var i in Enumerable.Range(0, exact.Count).OrderByDescending(i => exact[i] - shares[i]).Take(left)) shares[i]++;
        return shares;
    }
}
