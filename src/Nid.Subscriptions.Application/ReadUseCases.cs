using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.Subscriptions.Contracts;
using Nid.Subscriptions.Domain;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Subscriptions.Application;

/// <summary>Twelve months ahead unless the caller asks otherwise; bounded so a caller cannot walk forever.</summary>
public static class Horizons
{
    public const int MaxYearsAhead = 10;

    public static LocalDate Resolve(LocalDate today, LocalDate? requested)
    {
        var ceiling = today.PlusYears(MaxYearsAhead);
        var horizon = requested ?? today.PlusMonths(12);
        return horizon > ceiling ? ceiling : horizon;
    }
}

public sealed record ListSubscriptionsQuery(
    VisibilityScope Scope, LocalDate? From, LocalDate? To, LocalDate? Horizon) : IQuery<SubscriptionList>;

public sealed record GetSubscriptionQuery(
    ItemId ItemId, LocalDate? From, LocalDate? To, LocalDate? Horizon) : IQuery<SubscriptionDetail>, IItemScoped
{
    public AccessKind Access => AccessKind.Read;
}

[RequestHandler<ListSubscriptionsQuery, SubscriptionList>]
public sealed class ListSubscriptionsHandler(
    IVisibleItems visible, ISubscriptionStore store, IItemRegistry items, IActorAccessor actor, IClock clock)
    : IRequestHandler<ListSubscriptionsQuery, SubscriptionList>
{
    public async ValueTask<Result<SubscriptionList>> HandleAsync(ListSubscriptionsQuery query, CancellationToken ct = default)
    {
        var person = actor.Current!.PersonId!.Value;
        var today = clock.GetCurrentInstant().InUtc().Date;
        var horizon = Horizons.Resolve(today, query.Horizon);
        var window = CostWindow.Resolve(today, query.From, query.To);

        var ids = await visible.ForAsync(person, query.Scope, ct);
        var inputs = await store.LoadManyAsync(ids.ToList(), ct);

        var summaries = new List<SubscriptionSummary>(inputs.Count);
        var timelines = new List<Timeline>(inputs.Count);

        foreach (var input in inputs)
        {
            if (!SubscriptionTimeline.Build(input, today, horizon).TryGetValue(out var timeline))
                continue; // a refused subscription is reported by its own detail endpoint, not by hiding the list

            timelines.Add(timeline!);
            var item = await items.FindAsync(input.Subscription.Id, ct);
            summaries.Add(SubscriptionViews.Summarise(input, timeline!, item?.DisplayName ?? "", window));
        }

        var (cash, amortized, shared) = HouseholdCost.Combine(timelines, window);
        return Result.Success(new SubscriptionList(
            summaries, cash, amortized, shared,
            HouseholdCost.Divergences(shared).Select(FindingView.From).ToList()));
    }
}

[RequestHandler<GetSubscriptionQuery, SubscriptionDetail>]
public sealed class GetSubscriptionHandler(
    ISubscriptionStore store, IItemRegistry items, IClock clock)
    : IRequestHandler<GetSubscriptionQuery, SubscriptionDetail>
{
    public async ValueTask<Result<SubscriptionDetail>> HandleAsync(GetSubscriptionQuery query, CancellationToken ct = default)
    {
        var input = await store.LoadAsync(query.ItemId, ct);
        if (input is null)
            return Result.Failure<SubscriptionDetail>(new NotFoundFailure("subscription", query.ItemId.ToString()));

        var today = clock.GetCurrentInstant().InUtc().Date;
        var horizon = Horizons.Resolve(today, query.Horizon);
        var window = CostWindow.Resolve(today, query.From, query.To);

        var built = SubscriptionTimeline.Build(input, today, horizon);
        if (!built.TryGetValue(out var timeline))
            return Result.Failure<SubscriptionDetail>(
                built.TryGetFailure(out var failure)
                    ? failure!
                    : new Failure("subscriptions.unknown", "The subscription could not be read.", null));

        var item = await items.FindAsync(query.ItemId, ct);
        return Result.Success(SubscriptionViews.Detail(input, timeline!, item?.DisplayName ?? "", window));
    }
}
