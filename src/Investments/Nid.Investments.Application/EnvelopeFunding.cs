using Nid.Core.Contracts;
using Nid.Investments.Application.Prices;
using Nid.Investments.Contracts;
using Nid.Investments.Domain;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Investments.Application;

/// <summary>Savings goals spec §4: the same batched read path as <see cref="EnvelopesNetWorth"/>, narrowed to the asked envelopes.</summary>
public sealed class EnvelopeFunding(
    IVisibleItems visible, IEnvelopeStore envelopes, EnvelopePerformance performance, IItemRegistry items, EnvelopeEstimator estimator, IHouseholdCalendar calendar) : IEnvelopeFunding
{
    public async Task<Result<IReadOnlyList<EnvelopeFundingFigures>>> ReadAsync(
        PersonId reader, VisibilityScope scope, IReadOnlyList<ItemId> asked, LocalDate date, CancellationToken ct)
    {
        var visibleIds = await visible.ForAsync(reader, scope, includeArchived: true, ct);
        var found = await envelopes.FindManyAsync(asked.Distinct().Where(visibleIds.Contains).ToList(), ct);
        var accesses = await items.FindManyAsync(found.Select(e => e.ItemId).ToList(), ct);
        // No silent empty read: a goal edit would drop the links it no longer sees, a replace-import would fail later.
        if (await performance.FactsManyAsync(found, accesses, ct) is not { } facts)
            return Unreadable("The values and flows of the envelopes cannot be read.");
        // Issue #12: a past date reads what was known then: no estimate (the estimator prices today), closed once its ClosedOn has passed.
        var past = date < await calendar.TodayAsync(ct);
        IReadOnlyDictionary<ItemId, Prices.EnvelopeEstimate> estimates = past ? new Dictionary<ItemId, Prices.EnvelopeEstimate>()
            : await estimator.EstimateAsync(found.Where(e => e.ClosedOn is null).Select(e => e.ItemId).ToList(), ct);
        var since = date.PlusMonths(-12);
        var result = new List<EnvelopeFundingFigures>();
        foreach (var envelope in found)
        {
            if (!facts.TryGetValue(envelope.ItemId, out var f)) continue;
            var closed = past ? envelope.ClosedOn <= date : f.Item.ArchivedAt is not null;
            var figures = EnvelopeMath.At(f.Values, f.Flows, f.Currency, f.Envelope.Cadence, f.Envelope.StaleAfterDays, date);
            Money? value = null;
            LocalDate? asOf = null;
            var estimated = false;
            if (figures.Value is not null)
                (value, asOf, estimated, _) = TodayValue.Pick(figures, estimates.GetValueOrDefault(envelope.ItemId));
            var net = f.Flows.Where(x => x.OccurredOn > since && x.OccurredOn <= date && x.Amount.Currency == f.Currency).Sum(x => x.Kind switch
            {
                FlowKind.Opening or FlowKind.Contribution => x.Amount.Amount,
                FlowKind.Withdrawal => -x.Amount.Amount,
                _ => 0m,
            });
            if (!Money.Create(net, f.Currency).TryGetValue(out var netMoney))
                return Unreadable("The net contributions of an envelope cannot be expressed as an amount.");
            // A closed envelope reports no staleness, as EnvelopePerformance.GetAsync.
            result.Add(new EnvelopeFundingFigures(envelope.ItemId, f.Item.DisplayName, closed, value, asOf,
                !closed && figures.StaleDays is not null, estimated, netMoney));
        }
        return Result.Success<IReadOnlyList<EnvelopeFundingFigures>>(result);
    }

    private static Result<IReadOnlyList<EnvelopeFundingFigures>> Unreadable(string message)
        => Result.Failure<IReadOnlyList<EnvelopeFundingFigures>>(new UnambitiousFx.Functional.Failures.Failure("investments.funding_unreadable", message, null));
}
