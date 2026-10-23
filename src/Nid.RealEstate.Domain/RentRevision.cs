using Nid.RealEstate.Contracts;
using NodaTime;

namespace Nid.RealEstate.Domain;

/// <summary>Rental spec §3: where a tenancy chain stands against its yearly rent revision.</summary>
public abstract record RevisionState;

/// <summary>Not indexed, before the first anniversary, or already revised at this anniversary.</summary>
public sealed record RevisionNone : RevisionState;

/// <summary>A revision is offered: <c>NewRent</c> = rent × To ÷ From, to the cent. <c>DpeUnknown</c>: the energy class is not filled in.</summary>
public sealed record RevisionDue(LocalDate Anniversary, IrlValue From, IrlValue To, decimal NewRent, LocalDate DefaultEffectiveOn, bool DpeUnknown) : RevisionState;

/// <summary>The quarter needed is not stored.</summary>
public sealed record RevisionIrlMissing(LocalDate Anniversary, IrlQuarter Missing) : RevisionState;

/// <summary>Energy class F or G: no revision.</summary>
public sealed record RevisionForbiddenDpe(LocalDate Anniversary) : RevisionState;

/// <summary>Rental spec §3: the revision window, the IRL pair and the arithmetic. Pure.</summary>
public static class RentRevision
{
    /// <summary>The chain's yearly date in <paramref name="year"/>; 29 February falls on 28 February in a common year.</summary>
    public static LocalDate Anniversary(LocalDate chainStart, int year)
        => new(year, chainStart.Month, Math.Min(chainStart.Day, CalendarSystem.Iso.GetDaysInMonth(year, chainStart.Month)));

    /// <summary>The latest anniversary on or before <paramref name="today"/>: the window is [it, it + 1 year).</summary>
    public static LocalDate Current(LocalDate chainStart, LocalDate today)
        => Anniversary(chainStart, today.Year) is var a && a <= today ? a : Anniversary(chainStart, today.Year - 1);

    public static RevisionState State(LocalDate chainStart, Tenancy current, bool indexed, int? referenceQuarter,
        bool currentIsRevisionOfThisAnniversary, string? energyClass, IReadOnlyList<IrlValue> irl, LocalDate today)
    {
        if (!indexed || referenceQuarter is not { } t || today < Anniversary(chainStart, chainStart.Year + 1)) return new RevisionNone();
        var anniversary = Current(chainStart, today);
        if (currentIsRevisionOfThisAnniversary) return new RevisionNone();
        if (energyClass is "F" or "G") return new RevisionForbiddenDpe(anniversary);

        var year = anniversary.Year;
        IrlValue? Find(int y) => irl.FirstOrDefault(v => v.Quarter == new IrlQuarter(y, t));
        // The latest quarter known at the anniversary: step back past one published after it, or not over by then (a T4
        // lease with a January anniversary goes back two years). A quarter over but not stored stops here: it is missing.
        bool Later(int y) => Find(y) is { } v ? (v.PublishedOn ?? v.FetchedOn) > anniversary : anniversary < new LocalDate(y, 3 * t, 1).PlusMonths(1);
        while (Later(year)) year--;
        if (Find(year) is not { } to) return new RevisionIrlMissing(anniversary, new IrlQuarter(year, t));
        if (Find(year - 1) is not { } from) return new RevisionIrlMissing(anniversary, new IrlQuarter(year - 1, t));
        var rent = Math.Round(current.RentMonthly * to.Value / from.Value, 2, MidpointRounding.AwayFromZero);
        return new RevisionDue(anniversary, from, to, rent, today, energyClass is null);
    }

    /// <summary>
    /// The previous anniversary left unrevised (no link starts since it), or null. In energy class F or G it was forbidden, not
    /// lost; a revision at the current anniversary closes the matter.
    /// </summary>
    public static LocalDate? Lost(LocalDate chainStart, IReadOnlyList<LocalDate> linkStarts, string? energyClass, LocalDate today)
    {
        var previous = Anniversary(chainStart, Current(chainStart, today).Year - 1);
        return energyClass is "F" or "G" || previous < Anniversary(chainStart, chainStart.Year + 1) || linkStarts.Any(s => s >= previous) ? null : previous;
    }

    /// <summary>The effective date lies in [anniversary, today] and before the tenancy's exclusive end.</summary>
    public static bool EffectiveOnAllowed(LocalDate effectiveOn, LocalDate anniversary, LocalDate today, LocalDate? endsOn)
        => effectiveOn >= anniversary && effectiveOn <= today && (endsOn is not { } end || effectiveOn < end);
}
