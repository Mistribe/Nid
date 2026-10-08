using Nid.Core.Contracts;
using Nid.Subscriptions.Contracts;
using Nid.Subscriptions.Domain;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Subscriptions.Application;

/// <summary>
/// 2D1 spec §4: this calendar month's amortized and cash cost of the reader's visible active subscriptions, by the
/// household totals' rules (a purchase granting to several subscriptions is counted once in cash).
/// </summary>
public sealed class SubscriptionsCommitments(IVisibleItems visible, ISubscriptionStore store) : ICommitmentsContribution
{
    public async Task<Result<CommitmentsPart>> CommitmentsAsync(PersonId reader, VisibilityScope scope, LocalDate today, CancellationToken ct)
    {
        var window = CostWindow.Resolve(today, null, null);
        var horizon = Horizons.Resolve(today, null);
        var inputs = await store.LoadManyAsync((await visible.ForAsync(reader, scope, ct)).ToList(), ct);

        var timelines = new List<Timeline>();
        foreach (var input in inputs)
            if (SubscriptionTimeline.Build(input, today, horizon).TryGetValue(out var timeline))
                timelines.Add(timeline!); // as the list: a refused subscription is reported by its own detail

        return Result.Success(new CommitmentsPart(
            timelines.Count,
            Sum(HouseholdCost.AmortizedPieces(timelines, window)),
            Sum(HouseholdCost.CashPieces(timelines, window))));
    }

    private static IReadOnlyDictionary<Currency, decimal> Sum(IEnumerable<CostPiece> pieces)
        => pieces.GroupBy(p => p.Currency).ToDictionary(g => g.Key, g => g.Sum(p => p.Amount));
}
