using Nid.Investments.Contracts;
using NodaTime;

namespace Nid.Investments.Domain;

/// <summary>#184: a release still to come, its share of the envelope's value.</summary>
public sealed record Release(LocalDate On, decimal Amount);

/// <summary>#184: what can be withdrawn on a date, and what is released after it, earliest first.</summary>
public sealed record Availability(decimal AvailableToday, IReadOnlyList<Release> Releases);

/// <summary>
/// #184: an employee savings plan's tranches split its value pro rata of their printed amounts, since the value moves after the statement.
/// Each release is rounded to the cent; what is available takes the remainder, so the parts sum to the value exactly.
/// </summary>
public static class EnvelopeAvailability
{
    /// <summary>Null without tranches: the whole plan stays locked.</summary>
    public static Availability? At(IReadOnlyList<AvailabilityTranche>? tranches, decimal value, LocalDate today)
    {
        if (tranches is not { Count: > 0 }) return null;
        var total = tranches.Sum(t => t.Amount);
        if (total <= 0m) return null;
        var releases = tranches.Where(t => t.AvailableOn > today).OrderBy(t => t.AvailableOn)
            .Select(t => new Release(t.AvailableOn, Math.Round(value * t.Amount / total, 2, MidpointRounding.AwayFromZero))).ToList();
        return new Availability(value - releases.Sum(r => r.Amount), releases);
    }
}
