using Nid.Assets.Contracts;
using NodaTime;

namespace Nid.Assets.Domain;

/// <summary>
/// #650: one part of a vehicle's cost of ownership: <c>running:&lt;category&gt;</c>, <c>subscription</c>, <c>credit</c> or <c>depreciation</c>.
/// A null <paramref name="Amount"/> is a part Nid has no figure for: shown as such, left out of the total.
/// <paramref name="Source"/> and <paramref name="Name"/> name the linked subscription or loan.
/// </summary>
public sealed record CostPart(string Code, decimal? Amount, Guid? Source = null, string? Name = null);

/// <param name="Months">The period's length in average months (30.4375 days), from <paramref name="From"/> to <paramref name="To"/> inclusive.</param>
/// <param name="Incomplete">A part is missing: <paramref name="Total"/> counts the others only.</param>
/// <param name="PerKm">Null without two mileage readings that tell the distance driven.</param>
public sealed record OwnershipCost(LocalDate From, LocalDate To, decimal Months, IReadOnlyList<CostPart> Parts,
    decimal Total, decimal Monthly, bool Incomplete, decimal? PerKm);

public static class OwnershipMath
{
    private const decimal DaysPerMonth = 30.4375m;
    private const int MinReadingDays = 30;

    /// <summary>
    /// The cost of [<paramref name="from"/>, <paramref name="to"/>], started no earlier than the purchase: the running costs by category (fuel
    /// always shown, missing when none), each linked subscription and loan (one missing subscription part when none is linked; no credit part
    /// without a loan), and the value lost between the period's ends. The capital of a loan is never a cost: the depreciation counts the car.
    /// </summary>
    public static OwnershipCost Compute(LocalDate from, LocalDate to, LocalDate? purchasedOn,
        IReadOnlyDictionary<string, decimal> runningByCategory, IReadOnlyList<(Guid Id, string Name, decimal? Cost)> subscriptions,
        IReadOnlyList<(Guid Id, string Name, decimal? Cost)> loans, Func<LocalDate, AssetValue?> valueAt, IReadOnlyList<MileageReading> readings)
    {
        var start = purchasedOn is { } bought && bought > from ? bought : from;
        var parts = new List<CostPart>();
        foreach (var category in AssetCostCategories.All)
        {
            var amount = runningByCategory.GetValueOrDefault(category);
            if (amount != 0m || category == AssetCostCategories.Fuel)
                parts.Add(new CostPart($"running:{category}", amount != 0m ? amount : null));
        }
        parts.AddRange(subscriptions.Count == 0 ? [new CostPart("subscription", null)] : subscriptions.Select(s => new CostPart("subscription", s.Cost, s.Id, s.Name)));
        parts.AddRange(loans.Select(l => new CostPart("credit", l.Cost, l.Id, l.Name)));
        parts.Add(new CostPart("depreciation", valueAt(start) is { } first && valueAt(to) is { } last ? first.Amount - last.Amount : null));

        var months = Math.Max(1m, decimal.Round((Period.DaysBetween(start, to) + 1) / DaysPerMonth, 2, MidpointRounding.AwayFromZero));
        var total = parts.Sum(p => p.Amount ?? 0m);
        var monthly = decimal.Round(total / months, 2, MidpointRounding.AwayFromZero);
        return new OwnershipCost(start, to, months, parts, total, monthly, parts.Any(p => p.Amount is null), PerKm(monthly, start, to, readings));
    }

    /// <summary>The readings in the period and the nearest one on each side; the farthest two, 30 days apart and going up, give the km a month.</summary>
    private static decimal? PerKm(decimal monthly, LocalDate from, LocalDate to, IReadOnlyList<MileageReading> readings)
    {
        var inside = readings.Where(r => r.On >= from && r.On <= to);
        var around = inside.Concat(readings.Where(r => r.On < from).OrderBy(r => r.On).TakeLast(1))
            .Concat(readings.Where(r => r.On > to).OrderBy(r => r.On).Take(1)).OrderBy(r => r.On).ToList();
        if (around.Count < 2)
            return null;
        var (first, last) = (around[0], around[^1]);
        var days = Period.DaysBetween(first.On, last.On);
        if (days < MinReadingDays || last.Km <= first.Km)
            return null;
        var kmPerMonth = (last.Km - first.Km) / (days / DaysPerMonth);
        return decimal.Round(monthly / kmPerMonth, 2, MidpointRounding.AwayFromZero);
    }
}
