using Nid.Investments.Contracts;
using NodaTime;

namespace Nid.Investments.Domain;

/// <summary>Holdings exposure spec C4 (entry) and D7 (staleness).</summary>
public static class BreakdownRules
{
    private const string Invalid = "request.invalid";

    /// <summary>The field errors of a breakdown, empty when valid; paths are <c>regions</c>, <c>regions[i].code</c>, <c>regions[i].pct</c>, same for sectors.</summary>
    public static IReadOnlyList<(string Path, string Code, string Message)> Check(IReadOnlyList<BreakdownPart> regions, IReadOnlyList<BreakdownPart>? sectors)
    {
        var errors = new List<(string, string, string)>();
        CheckPart("regions", regions, BreakdownRegions.All, errors);
        if (sectors is not null)
            CheckPart("sectors", sectors, BreakdownSectors.All, errors);
        return errors;
    }

    /// <summary>Older than 365 days, an equity never (its exposure is its issuer's, not a fund's mix).</summary>
    public static bool IsStale(LocalDate asOf, string holdingKind, LocalDate today)
        => holdingKind != "equity" && Period.Between(asOf, today, PeriodUnits.Days).Days > BreakdownCodes.StaleAfterDays;

    private static void CheckPart(string path, IReadOnlyList<BreakdownPart> parts, IReadOnlyList<string> known, List<(string, string, string)> errors)
    {
        var clean = true;
        for (var i = 0; i < parts.Count; i++)
        {
            var (code, pct) = parts[i];
            if (!known.Contains(code))
                { errors.Add(($"{path}[{i}].code", Invalid, $"'{code}' is not a known code.")); clean = false; }
            else if (parts.Take(i).Any(p => p.Code == code))
                { errors.Add(($"{path}[{i}].code", Invalid, $"'{code}' appears once.")); clean = false; }
            if (pct <= 0 || Math.Round(pct, 1) != pct)
                { errors.Add(($"{path}[{i}].pct", Invalid, "A share is above 0, with one decimal at most.")); clean = false; }
        }
        if (clean && parts.Sum(p => p.Pct) != 100m)
            errors.Add((path, BreakdownCodes.Not100, "The shares add up to exactly 100."));
    }
}
