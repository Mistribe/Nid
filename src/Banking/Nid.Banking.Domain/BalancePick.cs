using Nid.Banking.Contracts;
using Nid.Core.Contracts;

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

/// <summary>#355: the currency of an account the bank sent in <c>XXX</c>, from its balances.</summary>
public static class BankCurrency
{
    /// <summary>The one currency every stated balance is in; <c>null</c> when they name several, or none.</summary>
    public static Currency? Infer(IReadOnlyList<ProviderBalance> balances)
    {
        var named = balances.Select(b => b.Currency.ToUpperInvariant()).Where(c => c != Currency.None.Code).Distinct().ToList();
        return named is [var only] && Currency.TryCreate(only, out var currency) ? currency : null;
    }
}
