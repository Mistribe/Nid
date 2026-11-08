using System.Text.RegularExpressions;
using NodaTime;

namespace Nid.Core.Contracts;

/// <summary>
/// Issue #409: the yearly tax calendar of calendar year <c>Year</c>, written each year like the scale (<c>TaxRules</c>): the online
/// filing deadline of the revenues of <c>Year − 1</c> by zone of departments (01 to 19, 20 to 54 with Corsica, 55 and after with the
/// overseas ones), the month the notice's new withholding rate applies, the balance's first debit and the property tax's payment
/// deadline. <c>RulesYear</c> is the year the dates were published for: a later year carries them forward to the same days
/// (<see cref="CarriedForward"/>). Here rather than in Core's domain: RealEstate dates the property tax with it.
/// </summary>
public sealed partial record TaxCalendar(
    int Year, int RulesYear, LocalDate FilingNorth, LocalDate FilingCentre, LocalDate FilingSouth, LocalDate NewRate, LocalDate Balance,
    LocalDate PropertyTax)
{
    public bool CarriedForward => Year > RulesYear;

    /// <summary>
    /// 2025: impots.gouv.fr, 22 May, 28 May and 5 June. 2026: economie.gouv.fr (9 April 2026), 21 May, 28 May and 4 June. The new rate
    /// applies from September, the balance is debited from 25 September, the property tax is due on 15 October (paid online, 20 October).
    /// </summary>
    public static TaxCalendar For(int year) => year <= 2025
        ? Shift(new TaxCalendar(2025, 2025, new(2025, 5, 22), new(2025, 5, 28), new(2025, 6, 5), new(2025, 9, 1), new(2025, 9, 25), new(2025, 10, 15)), year)
        : Shift(new TaxCalendar(2026, 2026, new(2026, 5, 21), new(2026, 5, 28), new(2026, 6, 4), new(2026, 9, 1), new(2026, 9, 25), new(2026, 10, 15)), year);

    /// <summary>The deadline of the department's zone; null for an unknown department (the caller then takes <see cref="FilingNorth"/>, the earliest).</summary>
    public LocalDate? FilingDeadline(string? department) => Zone(department) switch
    {
        1 => FilingNorth,
        2 => FilingCentre,
        3 => FilingSouth,
        _ => null,
    };

    /// <summary>A French department's code: 01 to 95 (no 20), 2A, 2B, 971 to 976.</summary>
    public static bool IsDepartment(string? code) => code is not null && DepartmentCode().IsMatch(code) && code != "20";

    private static int? Zone(string? department) => !IsDepartment(department) ? null
        : department is "2A" or "2B" ? 2
        : int.Parse(department!, System.Globalization.CultureInfo.InvariantCulture) switch { <= 19 => 1, <= 54 => 2, _ => 3 };

    private static TaxCalendar Shift(TaxCalendar c, int year)
    {
        LocalDate On(LocalDate d) => new(year, d.Month, d.Day);
        return year == c.Year ? c : c with
        {
            Year = year, FilingNorth = On(c.FilingNorth), FilingCentre = On(c.FilingCentre), FilingSouth = On(c.FilingSouth),
            NewRate = On(c.NewRate), Balance = On(c.Balance), PropertyTax = On(c.PropertyTax),
        };
    }

    [GeneratedRegex("^(0[1-9]|[1-8][0-9]|9[0-5]|2A|2B|97[1-6])$")]
    private static partial Regex DepartmentCode();
}
