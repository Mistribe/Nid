namespace Nid.RealEstate.Application;

internal static class Sums
{
    /// <summary>Null with nothing to sum (no figure is not a figure of 0), or when one amount is unknown.</summary>
    public static decimal? SumOrNull(IEnumerable<decimal?> amounts)
    {
        decimal? total = null;
        foreach (var a in amounts)
        {
            if (a is not { } value) return null;
            total = (total ?? 0m) + value;
        }
        return total;
    }
}
