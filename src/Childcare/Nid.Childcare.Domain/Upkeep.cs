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

    public static (decimal Upkeep, decimal Meals) Month(IReadOnlyList<StatementDay> monthDays, ContractTerms terms) => Month(monthDays, _ => terms);

    /// <summary>Each present day priced with the terms in force on it: a revision taking effect mid-month prices its days only.</summary>
    public static (decimal Upkeep, decimal Meals) Month(IReadOnlyList<StatementDay> monthDays, Func<LocalDate, ContractTerms> termsOn)
    {
        var present = monthDays.Where(d => d.Present).ToList();
        var (lunch, snack) = MealCharges(present, termsOn);
        return (present.Sum(d => ForDay(d.Date, d.Arrival!.Value, d.Departure!.Value, termsOn(d.Date).UpkeepPer9h)), lunch + snack);
    }

    public static (decimal Lunch, decimal Snack) MealCharges(IReadOnlyList<StatementDay> days, ContractTerms terms) => MealCharges(days, _ => terms);

    /// <summary>Each meal ticked on a present day at its own day's price; a meal the employer provides stays ticked and costs nothing.</summary>
    public static (decimal Lunch, decimal Snack) MealCharges(IReadOnlyList<StatementDay> days, Func<LocalDate, ContractTerms> termsOn)
    {
        decimal Charge(Func<StatementDay, bool> ticked, Func<ContractTerms, decimal?> price, Func<ContractTerms, MealProvider> provider)
            => Mensualisation.Round(days.Where(d => d.Present && ticked(d)).Select(d => termsOn(d.Date))
                .Sum(t => provider(t) == MealProvider.Nanny ? price(t) ?? 0m : 0m));
        return (Charge(d => d.Lunch, t => t.LunchPrice, t => t.LunchProvider), Charge(d => d.Snack, t => t.SnackPrice, t => t.SnackProvider));
    }
}
