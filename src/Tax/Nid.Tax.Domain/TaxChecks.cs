using Nid.Tax.Contracts;
using static Nid.Tax.Domain.TaxWire;

namespace Nid.Tax.Domain;

/// <summary>Tax estimate spec §7 and §8: what a year, a profile and the simulation's inputs must satisfy; the handlers refuse the rest.</summary>
public static class TaxChecks
{
    /// <summary>The estimate covers the revenues of the first year of the rules to next year.</summary>
    public static bool YearOk(int year, int thisYear) => year >= TaxRules.FirstYear && year <= thisYear + 1;

    /// <summary>A payslip's employee contributions: between 0 and 60 % of the gross, as a fraction.</summary>
    public static bool RateOk(decimal rate) => rate is >= 0m and <= 0.60m;

    /// <summary>An amount the estimate takes: not negative and below ten million euros.</summary>
    public static bool AmountOk(decimal amount) => amount is >= 0m and < AmountCeiling;

    /// <summary>A gain or a loss (#234): below ten million euros either way.</summary>
    public static bool SignedAmountOk(decimal amount) => amount is > -AmountCeiling and < AmountCeiling;

    private const decimal AmountCeiling = 10_000_000m;

    public static bool ProfileOk(TaxProfile p)
        => p.Statuses.Values.All(s => TryStatus(s, out _))
           && p.Dependents is null or (>= 0 and <= 20) && p.ChildrenUnder6 is >= 0 and <= 20
           && (p.RentalRegime is null || TryRegime(p.RentalRegime, out _))
           && p.Pinel.All(c => c.InitialYears is 6 or 9 && c.TotalYears is 6 or 9 or 12 && c.TotalYears >= c.InitialYears
                               && c.StartYear is null or (>= 2014 and <= 2040))
           && p.Pinel.Select(c => c.Property).Distinct().Count() == p.Pinel.Count
           && new[] { p.ChildcareOutsideHome, p.HomeEmployment, p.Donations, p.DonationsAid, p.RentalDeficitCarried, p.DonationsAidLate, p.RentalEnergyWorks, p.DeductibleCsg, p.AlimonyPaid, p.AlimonyAdultChildren, p.CapitalLossesCarried }.All(AmountOk)
           && p.AlimonyAdultChildrenCount is >= 0 and <= 20 && (p.AlimonyAdultChildren == 0m || p.AlimonyAdultChildrenCount > 0)
           && p.AlternateCustody is >= 0 and <= 20 && (p.Dependents is null || p.AlternateCustody <= p.Dependents)
           && p.DisabledDependents is >= 0 and <= 20 && (p.Dependents is null || p.DisabledDependents <= p.Dependents)
           && p.AlternateUnder6 >= 0 && p.AlternateUnder6 <= p.ChildrenUnder6 && p.AlternateUnder6 <= p.AlternateCustody
           && (p.ContributionRates?.Values.All(RateOk) ?? true) && (p.ActualExpenses?.Values.All(AmountOk) ?? true)
           && (p.PerUnusedCeilings?.Values.All(AmountOk) ?? true)
           && (p.ExemptIncomes?.Values.All(AmountOk) ?? true)
           && (p.Department is null || TaxCalendar.IsDepartment(p.Department))
           && (p.PerCeilings?.Values.All(AmountOk) ?? true)
           && (p.PerCeilingsExpiring?.All(e => AmountOk(e.Value) && p.PerCeilings?.TryGetValue(e.Key, out var ceiling) == true && e.Value <= ceiling) ?? true);

    /// <summary>One or two declarants with figures not negative; the dependents' subsets among them.</summary>
    public static bool InputsOk(TaxInputs i)
        => i.Declarants.Count is >= 1 and <= 2
           && !i.Declarants.Any(d => d.ContributionRate is { } r && !RateOk(r) || d.SalaryTaxable < 0m || d.PerCeiling < 0m || d.PreviousYearIncome < 0m || d.PerExpiring < 0m)
           && i.Dependents is >= 0 and <= 20 && i.ChildrenUnder6 >= 0 && i.ChildrenUnder6 <= i.Dependents
           && i.AlimonyAdultChildrenCount is >= 0 and <= 20 && i.AlternateCustody >= 0 && i.AlternateCustody <= i.Dependents
           && i.DisabledDependents >= 0 && i.DisabledDependents <= i.Dependents
           && i.AlternateUnder6 >= 0 && i.AlternateUnder6 <= Math.Min(i.ChildrenUnder6, i.AlternateCustody);
}
