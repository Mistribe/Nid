using Nid.Banking.Contracts;

namespace Nid.Banking.Domain;

/// <summary>Spec §4.2: the booked balance at the end of the day, else the available one, flagged as a fallback.</summary>
public static class BalancePick
{
    private static readonly string[] Preferred = ["CLBD", "ITBD", "CLAV", "ITAV", "XPCD"];

    /// <summary>The first of CLBD, ITBD, CLAV, ITAV, XPCD the bank gave; <c>null</c> when it gave none of them.</summary>
    public static (ProviderBalance Balance, bool Fallback)? Choose(IReadOnlyList<ProviderBalance> balances)
    {
        foreach (var type in Preferred)
            if (balances.FirstOrDefault(b => b.Type == type) is { } found)
                return (found, IsFallback(type));
        return null;
    }

    /// <summary>Anything but a booked balance: the valuation's note says it is the available one.</summary>
    public static bool IsFallback(string type) => type is not ("CLBD" or "ITBD");
}
