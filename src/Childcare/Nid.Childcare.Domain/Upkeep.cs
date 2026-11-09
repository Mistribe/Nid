using Nid.Childcare.Contracts;
using NodaTime;

namespace Nid.Childcare.Domain;

/// <summary>Childcare spec §3: the upkeep allowance of each present day, by its length, and the month's meals. Pure.</summary>
public static class Upkeep
{
    /// <summary>The contract's 9-hour amount prorated, never under the legal minimum prorated nor the daily floor.</summary>
    public static decimal ForDay(LocalDate day, LocalTime arrival, LocalTime departure, decimal upkeepPer9h)
    {
        var rules = ChildcareRules.For(day);
        var hours = Mensualisation.Hours(arrival, departure);
        return Mensualisation.Round(Math.Max(ChildcareRules.UpkeepDailyFloor, Math.Max(upkeepPer9h, rules.Upkeep9hMinimum) * hours / 9m));
    }

    public static (decimal Upkeep, decimal Meals) Month(IReadOnlyList<StatementDay> monthDays, ContractTerms terms)
    {
        var present = monthDays.Where(d => d.Present).ToList();
        var (lunch, snack) = MealCharges(present, terms);
        return (present.Sum(d => ForDay(d.Date, d.Arrival!.Value, d.Departure!.Value, terms.UpkeepPer9h)), lunch + snack);
    }

    /// <summary>Each meal ticked on a present day at its own price; a meal the employer provides stays ticked and costs nothing.</summary>
    public static (decimal Lunch, decimal Snack) MealCharges(IReadOnlyList<StatementDay> days, ContractTerms terms)
    {
        decimal Charge(Func<StatementDay, bool> ticked, decimal? price, MealProvider provider)
            => provider == MealProvider.Nanny ? Mensualisation.Round(days.Count(d => d.Present && ticked(d)) * (price ?? 0m)) : 0m;
        return (Charge(d => d.Lunch, terms.LunchPrice, terms.LunchProvider), Charge(d => d.Snack, terms.SnackPrice, terms.SnackProvider));
    }
}
