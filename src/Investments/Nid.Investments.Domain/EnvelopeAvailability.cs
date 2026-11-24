using Nid.Investments.Contracts;
using NodaTime;

namespace Nid.Investments.Domain;

/// <summary>#184: a release still to come, its share of the envelope's value.</summary>
public sealed record Release(LocalDate On, decimal Amount);

/// <summary>
/// #184: what can be withdrawn on a date, and what is released after it, earliest first. #466: <c>Locked</c>, what stays locked with no
/// known date (a plan without tranches); <c>EarlyReleased</c>, what its early-release case frees, today or on the case's date.
/// </summary>
public sealed record Availability(decimal AvailableToday, IReadOnlyList<Release> Releases, decimal Locked = 0m, decimal EarlyReleased = 0m);

/// <summary>
/// #184: an employee savings plan's tranches split its value pro rata of their printed amounts, since the value moves after the statement.
/// Each release is rounded to the cent; what is available takes the remainder, so the parts sum to the value exactly.
/// </summary>
public static class EnvelopeAvailability
{
    /// <summary>
    /// Null without tranches nor early release: the whole plan stays locked. #466: an early release frees its amount (the whole plan
    /// without one) on its date, taken from what would only be released after that date, earliest first, then from what has no date.
    /// </summary>
    public static Availability? At(IReadOnlyList<AvailabilityTranche>? tranches, decimal value, LocalDate today, EarlyRelease? early = null)
    {
        var total = tranches?.Sum(t => t.Amount) ?? 0m;
        if (total <= 0m && early is null) return null;
        var releases = total <= 0m ? [] : tranches!.Where(t => t.AvailableOn > today).OrderBy(t => t.AvailableOn)
            .Select(t => new Release(t.AvailableOn, Math.Round(value * t.Amount / total, 2, MidpointRounding.AwayFromZero))).ToList();
        var locked = total <= 0m ? value : 0m;
        var available = value - locked - releases.Sum(r => r.Amount);
        if (early is null) return new Availability(available, releases);

        var allowed = early.Amount is { } a ? Math.Round(a, 2, MidpointRounding.AwayFromZero) : decimal.MaxValue;
        var left = allowed;
        var kept = new List<Release>();
        foreach (var r in releases)
        {
            var part = r.On > early.On ? Math.Min(r.Amount, left) : 0m;
            left -= part;
            if (part < r.Amount) kept.Add(r with { Amount = r.Amount - part });
        }
        var fromLocked = Math.Min(locked, left);
        var freed = allowed - left + fromLocked;
        if (early.On <= today) return new Availability(available + freed, kept, locked - fromLocked, freed);
        if (freed > 0m) kept.Add(new Release(early.On, freed));
        var merged = kept.GroupBy(r => r.On).Select(g => new Release(g.Key, g.Sum(r => r.Amount))).OrderBy(r => r.On).ToList();
        return new Availability(available, merged, locked - fromLocked, freed);
    }
}
