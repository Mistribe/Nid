using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.Subscriptions.Application.Catalog;
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
    IVisibleItems visible, ISubscriptionStore store, IItemRegistry items, IActorAccessor actor, IClock clock,
    IFxRates fx, IHouseholdSettings household, IProviderStore providers)
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

        var built = new List<(SubscriptionInput Input, Timeline Timeline, string DisplayName)>(inputs.Count);
        foreach (var input in inputs)
        {
            if (!SubscriptionTimeline.Build(input, today, horizon).TryGetValue(out var timeline))
                continue; // a refused subscription is reported by its own detail endpoint, not by hiding the list

            var item = await items.FindAsync(input.Subscription.Id, ct);
            built.Add((input, timeline!, item?.DisplayName ?? ""));
        }

        // One provider query for the whole list; every subscription in it is visible, so is its provider (design §5).
        var providerViews = await ProviderLookup.ForAsync(store, providers, built.Select(b => b.Input.Subscription.Id).ToList(), ct);

        var timelines = built.Select(b => b.Timeline).ToList();
        var reference = await household.ReferenceCurrencyAsync(ct);
        var householdCash = HouseholdCost.CashPieces(timelines, window);
        var householdAmortized = HouseholdCost.AmortizedPieces(timelines, window);
        var rates = await Conversion.LoadAsync(
            fx, timelines.SelectMany(t => t.CashCostPieces(window)).Concat(householdAmortized), reference, today, ct);

        var summaries = built.Select(b => SubscriptionViews.Summarise(
            b.Input, b.Timeline, b.DisplayName, providerViews.GetValueOrDefault(b.Input.Subscription.Id), window,
            Conversion.Of(b.Timeline.CashCostPieces(window), b.Timeline.AmortizedCostPieces(window), reference, rates))).ToList();

        var (cash, amortized, shared) = HouseholdCost.Combine(timelines, window);
        var householdConverted = Conversion.Of(householdCash, householdAmortized, reference, rates);
        return Result.Success(new SubscriptionList(
            summaries, cash, amortized,
            householdConverted.CashView, householdConverted.AmortizedView, householdConverted.IsEstimate,
            shared,
            HouseholdCost.Divergences(shared).Select(FindingView.From).Concat(householdConverted.Findings).ToList()));
    }
}

[RequestHandler<GetSubscriptionQuery, SubscriptionDetail>]
public sealed class GetSubscriptionHandler(
    ISubscriptionStore store, IItemRegistry items, IClock clock, IFxRates fx, IHouseholdSettings household, IProviderStore providers)
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

        var reference = await household.ReferenceCurrencyAsync(ct);
        var cashPieces = timeline!.CashCostPieces(window);
        var amortizedPieces = timeline.AmortizedCostPieces(window);
        var rates = await Conversion.LoadAsync(fx, cashPieces.Concat(amortizedPieces), reference, today, ct);

        var item = await items.FindAsync(query.ItemId, ct);
        var provider = (await ProviderLookup.ForAsync(store, providers, [query.ItemId], ct)).GetValueOrDefault(query.ItemId);
        return Result.Success(SubscriptionViews.Detail(
            input, timeline, item?.DisplayName ?? "", provider, window, Conversion.Of(cashPieces, amortizedPieces, reference, rates)));
    }
}

/// <summary>One rate query per read, whatever the number of subscriptions (1C2a design §5.2).</summary>
internal static class Conversion
{
    public static Converted Of(IReadOnlyList<CostPiece> cash, IReadOnlyList<CostPiece> amortized, Currency reference, RateTable rates)
        => new(FxConversion.Convert(cash, rates, reference), FxConversion.Convert(amortized, rates, reference));

    public static async Task<RateTable> LoadAsync(IFxRates fx, IEnumerable<CostPiece> pieces, Currency reference, LocalDate today, CancellationToken ct)
        => FxConversion.Need(pieces, reference) is { } need
            ? await fx.LoadAsync(need.Quotes, need.From, need.To, ct)
            : RateTable.Empty(today, 0);
}
