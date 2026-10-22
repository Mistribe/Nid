using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using NodaTime;

namespace Nid.Investments.Application;

/// <summary>
/// Issue #104: an envelope's value by asset class and liquidity. Pro rata of its latest snapshot's lines when it has one, each
/// line classed by its holding's asset class, else by its kind; without a snapshot, by the envelope's kind. Guessed classes are approximate.
/// </summary>
internal static class EnvelopeAllocation
{
    public static IReadOnlyList<AllocationSlice> Of(
        EnvelopeKind kind, decimal value, HoldingSnapshot? snapshot, IReadOnlyDictionary<Guid, EnvelopeHolding> holdings)
    {
        var liquidity = Liquidity(kind);
        var lines = snapshot?.Lines.Where(l => l.Value != 0m).ToList() ?? [];
        var total = lines.Sum(l => l.Value);
        if (total <= 0m)
        {
            var (cls, approximate) = ClassOf(kind);
            return [new AllocationSlice(cls, liquidity, value, approximate)];
        }

        var parts = lines
            .Select(l => (Class: ClassOf(holdings.GetValueOrDefault(l.HoldingId)), l.Value))
            .GroupBy(p => p.Class)
            .Select(g => (Key: g.Key, Weight: g.Sum(p => p.Value)))
            .OrderBy(p => AllocationOrder(p.Key.Class)).ThenBy(p => p.Key.Approximate)
            .ToList();
        var slices = new List<AllocationSlice>();
        var given = 0m;
        for (var i = 0; i < parts.Count; i++)
        {
            // The last part takes the remainder, so the slices always sum to the value exactly.
            var amount = i == parts.Count - 1 ? value - given : value * parts[i].Weight / total;
            given += amount;
            slices.Add(new AllocationSlice(parts[i].Key.Class, liquidity, amount, parts[i].Key.Approximate));
        }
        return slices;
    }

    /// <summary>Current and savings accounts can be spent now; a PER waits for retirement; the rest takes a sale or a withdrawal.</summary>
    internal static string Liquidity(EnvelopeKind kind) => kind switch
    {
        EnvelopeKind.Checking or EnvelopeKind.Savings => AllocationLiquidity.Available,
        EnvelopeKind.Per => AllocationLiquidity.Locked,
        _ => AllocationLiquidity.Days,
    };

    private static (string Class, bool Approximate) ClassOf(EnvelopeKind kind) => kind switch
    {
        EnvelopeKind.Checking or EnvelopeKind.Savings => (AllocationClasses.Cash, false),
        EnvelopeKind.Pea or EnvelopeKind.Brokerage => (AllocationClasses.Equity, true),
        EnvelopeKind.LifeInsurance or EnvelopeKind.Per => (AllocationClasses.Mixed, true),
        _ => (AllocationClasses.Other, true),
    };

    private static (string Class, bool Approximate) ClassOf(EnvelopeHolding? holding)
    {
        if (holding?.AssetClass is { } declared && AllocationClasses.All.Contains(declared))
            return (declared, false);
        return holding?.Kind switch
        {
            "euro_fund" => (AllocationClasses.CapitalGuaranteed, false),
            "cash" => (AllocationClasses.Cash, false),
            "equity" => (AllocationClasses.Equity, false),
            "bond" => (AllocationClasses.Bond, false),
            "etf" => (AllocationClasses.Equity, true),
            "fund" => (AllocationClasses.Mixed, true),
            _ => (AllocationClasses.Other, true),
        };
    }

    private static int AllocationOrder(string cls) => AllocationClasses.All.ToList().IndexOf(cls);

    /// <summary>The snapshot the overview reads: the latest on or before <paramref name="today"/>.</summary>
    public static HoldingSnapshot? Latest(IEnumerable<HoldingSnapshot> snapshots, ItemId envelope, LocalDate today)
        => snapshots.Where(s => s.Envelope == envelope && s.AsOf <= today).MaxBy(s => s.AsOf);
}
