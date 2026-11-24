using Nid.Core.Contracts;
using Nid.Tax.Contracts;
using Nid.Tax.Domain;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Tax.Application;

/// <summary>
/// Tax-per spec §4.6: « À venir » on 31 December, once it falls in the window. For each declarant the reader sees (all in Foyer, the
/// reader alone in Moi), the PER ceiling left to pay in and the part of it lost that day; under pooling the ceiling left is the
/// household's, one event in Foyer only. In euros, about no item; nothing when nothing is left.
/// </summary>
public sealed class PerDeadline(TaxEstimates estimates, IPersonRegistry persons)
{
    /// <summary>The pooled event's label, as it has no person's name.</summary>
    public const string Household = "household";

    public async Task<Result<IReadOnlyList<DashboardEventView>>> EventsAsync(
        PersonId reader, VisibilityScope scope, NodaTime.LocalDate today, NodaTime.LocalDate until, CancellationToken ct)
    {
        var on = new NodaTime.LocalDate(today.Year, 12, 31);
        if (on > until) return Result.Success<IReadOnlyList<DashboardEventView>>([]);
        var computed = await estimates.ComputeAsync(today.Year, scope, today, ct);
        if (computed.TryGetFailure(out var failure)) return Result.Failure<IReadOnlyList<DashboardEventView>>(failure!);
        computed.TryGetValue(out var r);
        var pooled = r!.Per?.Pooled == true;
        DashboardEventView Event(string kind, string name, string? label, decimal amount)
            => new(on, kind, null, "tax", name, label, TaxEstimates.Euros(amount));

        var events = new List<DashboardEventView>();
        foreach (var (adult, d) in TaxEstimates.Adults(await persons.ListAsync(ct)).Zip(r.Declarants))
        {
            if (d.Per is not { } per || scope is VisibilityScope.Me or VisibilityScope.Mine && adult.Id != reader) continue;
            if (!pooled && per.Remaining > 0m) events.Add(Event(UpcomingKinds.PerDeadline, adult.DisplayName, null, per.Remaining));
            // What keeps the expiring ceiling: the year's own ceiling is used first (spec §4.3).
            if (per.Minimum > 0m) events.Add(Event(UpcomingKinds.PerCeilingExpiring, adult.DisplayName, null, per.Minimum));
        }
        if (pooled && scope == VisibilityScope.Household && r.Per!.Remaining > 0m)
            events.Add(Event(UpcomingKinds.PerDeadline, "", Household, r.Per.Remaining));
        return Result.Success<IReadOnlyList<DashboardEventView>>(events);
    }
}
