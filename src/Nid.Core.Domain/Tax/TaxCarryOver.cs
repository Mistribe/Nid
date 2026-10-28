using Nid.Core.Contracts;

namespace Nid.Core.Domain.Tax;

/// <summary>
/// Tax assessments spec §5: what the notice of revenue year <c>Year − 1</c> hands to the estimate of <c>Year</c>, per adult. A figure
/// the notice does not print is absent here, so the profile's own value stays.
/// </summary>
public sealed record TaxCarryOver(
    int Year, IReadOnlyDictionary<Guid, decimal> PerCeilings, IReadOnlyDictionary<Guid, decimal> PerCeilingsExpiring,
    IReadOnlyDictionary<Guid, decimal> PerUnusedCeilings, decimal? RentalDeficitCarried)
{
    /// <summary>
    /// The carry-over of the <paramref name="notices"/> of <paramref name="estimateYear"/> − 1 (already filtered by readability and
    /// scope), for the <paramref name="adults"/> only; null when nothing carries.
    /// </summary>
    public static TaxCarryOver? From(int estimateYear, IEnumerable<TaxAssessment> notices, IReadOnlyCollection<Guid> adults)
    {
        var ceilings = new Dictionary<Guid, decimal>();
        var expiring = new Dictionary<Guid, decimal>();
        var unused = new Dictionary<Guid, decimal>();
        decimal? deficit = null;
        foreach (var notice in notices.Where(n => n.Year == estimateYear - 1))
        {
            if (notice.RentalDeficitCarried is { } d)
                deficit = (deficit ?? 0m) + d;
            foreach (var p in notice.Declarants.Where(p => adults.Contains(p.Person.Value)))
            {
                var id = p.Person.Value;
                if (p.PerCeilingNext is { } next)
                    ceilings[id] = next;
                if (p.PerUnusedOldest is { } oldest)
                    expiring[id] = oldest;
                decimal?[] printed = [p.PerUnusedOldest, p.PerUnusedMiddle, p.PerUnusedLatest];
                if (printed.Any(v => v is not null))
                    unused[id] = printed.Sum(v => v ?? 0m);
            }
        }
        return ceilings.Count + expiring.Count + unused.Count == 0 && deficit is null
            ? null : new TaxCarryOver(estimateYear, ceilings, expiring, unused, deficit);
    }

    /// <summary>The profile with the PER carry-over laid over it (not the rental deficit); the stored profile is untouched.</summary>
    public TaxProfile Apply(TaxProfile profile) => profile with
    {
        PerCeilings = Merge(profile.PerCeilings, PerCeilings),
        PerCeilingsExpiring = Merge(profile.PerCeilingsExpiring, PerCeilingsExpiring),
        PerUnusedCeilings = Merge(profile.PerUnusedCeilings, PerUnusedCeilings),
    };

    private static Dictionary<Guid, decimal> Merge(IReadOnlyDictionary<Guid, decimal>? own, IReadOnlyDictionary<Guid, decimal> over)
    {
        var merged = own is null ? [] : own.ToDictionary(kv => kv.Key, kv => kv.Value);
        foreach (var (k, v) in over)
            merged[k] = v;
        return merged;
    }
}
