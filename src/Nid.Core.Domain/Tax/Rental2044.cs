using System.Globalization;

namespace Nid.Core.Domain.Tax;

/// <summary>Rental 2044 spec §3: one property's amounts by form line, in euros, before rounding.</summary>
public sealed record Rental2044Input(
    Guid Property, string Name, decimal L211, decimal L221, decimal L222, decimal L223,
    decimal L224, decimal L227, decimal L229, decimal L250);

/// <summary>Spec §3: a property's column, each line rounded to the euro (half up); 215 = 211, 230 = 0, 240 the charges, 263 the result.</summary>
public sealed record Rental2044Column(
    Guid Property, string Name, decimal L211, decimal L215, decimal L221, decimal L222,
    decimal L223, decimal L224, decimal L227, decimal L229, decimal L230, decimal L240, decimal L250, decimal L263);

/// <summary>Spec §3: the form's totals. <c>Result</c> is line 420, <c>GlobalDeficit</c> 440 (4BC), <c>CarriedDeficit</c> 441 (4BB), <c>Taxable</c> 4BA.</summary>
public sealed record Rental2044Totals(
    decimal Receipts, decimal Charges, decimal Interest, decimal Result,
    decimal InterestDeficit, decimal ChargesDeficit, decimal GlobalDeficit, decimal CarriedDeficit, decimal Taxable);

public static class Rental2044
{
    private static decimal Euros(decimal amount) => Math.Round(amount, 0, MidpointRounding.AwayFromZero);

    private static readonly StringComparer French = StringComparer.Create(new CultureInfo("fr-FR"), true);

    /// <summary>Spec §3: the columns, ordered by name (French culture) then property.</summary>
    public static IReadOnlyList<Rental2044Column> Columns(IEnumerable<Rental2044Input> inputs) => inputs
        .OrderBy(i => i.Name, French).ThenBy(i => i.Property)
        .Select(i =>
        {
            var (l211, l221, l222, l223, l224, l227, l229, l250) =
                (Euros(i.L211), Euros(i.L221), Euros(i.L222), Euros(i.L223), Euros(i.L224), Euros(i.L227), Euros(i.L229), Euros(i.L250));
            var l240 = l221 + l222 + l223 + l224 + l227 + l229;
            return new Rental2044Column(i.Property, i.Name, l211, l211, l221, l222, l223, l224, l227, l229, 0m, l240, l250, l211 - l240 - l250);
        }).ToList();

    /// <summary>Spec §3: sums of the rounded lines; a deficit due to interest is carried, the rest goes on the global income up to the cap.</summary>
    public static Rental2044Totals Totals(IReadOnlyList<Rental2044Column> columns, decimal deficitCap)
    {
        var (receipts, charges, interest) = (columns.Sum(c => c.L215), columns.Sum(c => c.L240), columns.Sum(c => c.L250));
        var result = columns.Sum(c => c.L263);
        if (result >= 0m)
            return new Rental2044Totals(receipts, charges, interest, result, 0m, 0m, 0m, 0m, result);
        var interestDeficit = Math.Max(0m, interest - receipts);
        var global = GlobalDeficit(receipts, charges, interest, deficitCap);
        return new Rental2044Totals(receipts, charges, interest, result, interestDeficit, -result - interestDeficit, global, -result - global, 0m);
    }

    /// <summary>Spec §3: interest only offsets rents; the charges' deficit goes on the global income, capped.</summary>
    public static decimal GlobalDeficit(decimal receipts, decimal charges, decimal interest, decimal cap) =>
        Math.Min(cap, Math.Max(0m, charges - Math.Max(0m, receipts - interest)));
}
