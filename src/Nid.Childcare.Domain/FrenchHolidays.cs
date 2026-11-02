using NodaTime;

namespace Nid.Childcare.Domain;

/// <summary>The eleven national public holidays of France (no Alsace-Moselle days).</summary>
public static class FrenchHolidays
{
    public static IReadOnlySet<LocalDate> In(int year)
    {
        var easter = Easter(year);
        return new HashSet<LocalDate>
        {
            new(year, 1, 1), new(year, 5, 1), new(year, 5, 8), new(year, 7, 14), new(year, 8, 15),
            new(year, 11, 1), new(year, 11, 11), new(year, 12, 25),
            easter.PlusDays(1), easter.PlusDays(39), easter.PlusDays(50),
        };
    }

    /// <summary>Easter Sunday, anonymous Gregorian algorithm (Meeus/Jones/Butcher).</summary>
    private static LocalDate Easter(int y)
    {
        int a = y % 19, b = y / 100, c = y % 100, d = b / 4, e = b % 4, f = (b + 8) / 25, g = (b - f + 1) / 3;
        int h = (19 * a + b - d - g + 15) % 30, i = c / 4, k = c % 4, l = (32 + 2 * e + 2 * i - h - k) % 7, m = (a + 11 * h + 22 * l) / 451;
        return new LocalDate(y, (h + l - 7 * m + 114) / 31, (h + l - 7 * m + 114) % 31 + 1);
    }
}
