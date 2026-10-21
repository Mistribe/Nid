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
    VisibilityScope Scope, LocalDate? From, LocalDate? To, LocalDate? Horizon, ListFilter? Filter = null,
    ListGroup Group = ListGroup.None) : IQuery<SubscriptionList>;

public sealed record GetSubscriptionQuery(
    ItemId ItemId, LocalDate? From, LocalDate? To, LocalDate? Horizon) : IQuery<SubscriptionDetail>, IItemScoped
{
    public AccessKind Access => AccessKind.Read;
}

[RequestHandler<ListSubscriptionsQuery, SubscriptionList>]
public sealed class ListSubscriptionsHandler(
    SubscriptionSet set, IActorAccessor actor, IClock clock, IFxRates fx, IHouseholdSettings household)
    : IRequestHandler<ListSubscriptionsQuery, SubscriptionList>
{
    public async ValueTask<Result<SubscriptionList>> HandleAsync(ListSubscriptionsQuery query, CancellationToken ct = default)
    {
        var person = actor.Current!.PersonId!.Value;
        var today = clock.GetCurrentInstant().InUtc().Date;
        var horizon = Horizons.Resolve(today, query.Horizon);
        var window = CostWindow.Resolve(today, query.From, query.To);

        var all = await set.LoadAsync(person, query.Scope, today, horizon, ct);

        // The filter narrows the rows and, with them, the household totals below.
        var filter = query.Filter ?? ListFilter.None;
        var built = all.Where(b => filter.Matches(b.DisplayName, b.Provider, b.Timeline.ExpiresOn(), today)).ToList();

        var timelines = built.Select(b => b.Timeline).ToList();
        var reference = await household.ReferenceCurrencyAsync(ct);
        var householdCash = HouseholdCost.CashPieces(timelines, window);
        var householdAmortized = HouseholdCost.AmortizedPieces(timelines, window);
        var year = CostWindow.Year(today);
        var householdAnnual = HouseholdCost.AmortizedPieces(timelines, year);
        var rates = await Conversion.LoadAsync(
            fx, timelines.SelectMany(t => t.CashCostPieces(window)).Concat(householdAmortized).Concat(householdAnnual), reference, today, ct);

        var summaries = built.Select(b => SubscriptionViews.Summarise(
            b.Input, b.Timeline, b.DisplayName, b.Provider, window,
            Conversion.Of(b.Timeline.CashCostPieces(window), b.Timeline.AmortizedCostPieces(window), reference, rates), b.Owners,
            FxConversion.Convert(b.Timeline.AmortizedCostPieces(year), rates, reference))).ToList();

        var (cash, amortized, shared) = HouseholdCost.Combine(timelines, window);
        var householdConverted = Conversion.Of(householdCash, householdAmortized, reference, rates);
        var annual = FxConversion.Convert(householdAnnual, rates, reference);
        return Result.Success(new SubscriptionList(
            summaries, cash, amortized,
            householdConverted.CashView, householdConverted.AmortizedView, householdConverted.IsEstimate,
            shared,
            ListGrouping.Group(query.Group, summaries),
            HouseholdCost.Divergences(shared).Select(FindingView.From).Concat(SubscriptionViews.FxFindings(householdConverted, annual)).ToList(),
            annual.Total is { } t ? MoneyView.From(t) : null));
    }
}

public sealed record GetSubscriptionsOverviewQuery(VisibilityScope Scope) : IQuery<SubscriptionsOverview>;

[RequestHandler<GetSubscriptionsOverviewQuery, SubscriptionsOverview>]
public sealed class GetSubscriptionsOverviewHandler(
    SubscriptionSet set, IActorAccessor actor, IClock clock, IFxRates fx, IHouseholdSettings household, IPersonRegistry persons)
    : IRequestHandler<GetSubscriptionsOverviewQuery, SubscriptionsOverview>
{
    public async ValueTask<Result<SubscriptionsOverview>> HandleAsync(GetSubscriptionsOverviewQuery query, CancellationToken ct = default)
    {
        var person = actor.Current!.PersonId!.Value;
        var today = clock.GetCurrentInstant().InUtc().Date;
        // `months` runs over the 12 months after this one: the walk must reach the end of the last (today + 12 months would stop mid-month).
        var horizon = new LocalDate(today.Year, today.Month, 1).PlusMonths(13);

        var built = await set.LoadAsync(person, query.Scope, today, horizon, ct);
        var reference = await household.ReferenceCurrencyAsync(ct);
        var rates = await Conversion.LoadAsync(fx, OverviewMath.RatePieces(built, today), reference, today, ct);
        return Result.Success(OverviewMath.Build(built, await persons.ListAsync(ct), reference, rates, today));
    }
}

public sealed record GetSubscriptionChargesQuery(VisibilityScope Scope, YearMonth? Month) : IQuery<SubscriptionCharges>;

[RequestHandler<GetSubscriptionChargesQuery, SubscriptionCharges>]
public sealed class GetSubscriptionChargesHandler(
    SubscriptionSet set, IActorAccessor actor, IClock clock, IFxRates fx, IHouseholdSettings household)
    : IRequestHandler<GetSubscriptionChargesQuery, SubscriptionCharges>
{
    public async ValueTask<Result<SubscriptionCharges>> HandleAsync(GetSubscriptionChargesQuery query, CancellationToken ct = default)
    {
        var person = actor.Current!.PersonId!.Value;
        var today = clock.GetCurrentInstant().InUtc().Date;
        var month = query.Month ?? new YearMonth(today.Year, today.Month);
        var first = month.OnDayOfMonth(1);
        var ceiling = today.PlusYears(Horizons.MaxYearsAhead);
        var beyond = first > ceiling; // decided before any PlusMonths: it throws past year 9999
        // The walk reaches the 1st of the month after the requested one, capped at the ceiling.
        var truncated = !beyond && first.PlusMonths(1) > ceiling;
        IReadOnlyList<BuiltSubscription> built = beyond ? [] : await set.LoadAsync(person, query.Scope, today, Horizons.Resolve(today, first.PlusMonths(1)), ct);
        var reference = await household.ReferenceCurrencyAsync(ct);
        var rates = await Conversion.LoadAsync(fx, MonthCharges.RatePieces(built, month), reference, today, ct);
        return Result.Success(MonthCharges.Build(built, month, reference, rates, beyond, truncated));
    }
}

[RequestHandler<GetSubscriptionQuery, SubscriptionDetail>]
public sealed class GetSubscriptionHandler(
    ISubscriptionStore store, IItemRegistry items, IClock clock, IFxRates fx, IHouseholdSettings household, IProviderStore providers,
    IVisibleItems visible, IAccessPolicy policy, IActorAccessor actor)
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
        if (item is null)
            return Result.Failure<SubscriptionDetail>(new NotFoundFailure("subscription", query.ItemId.ToString()));
        var provider = (await ProviderLookup.ForAsync(store, providers, [query.ItemId], ct)).GetValueOrDefault(query.ItemId);

        var reader = actor.Current!.PersonId!.Value;
        var rows = await store.ReadAllAsync([query.ItemId], ct);
        var stored = rows.Subscriptions.Single();
        var visibleItems = await visible.ForAsync(reader, VisibilityScope.Me, ct);
        var editing = new SubscriptionEditing(
            item!.Visibility == Visibility.Private ? "private" : "shared",
            item.Owners.Select(o => new OwnerView(o.PersonId.Value, o.ShareBp)).ToList(),
            stored.Users.Select(u => u.Value).ToList(),
            stored.Note,
            rows.Phases.OrderBy(p => p.Phase.StartsOn).Select(p => new PhaseView(
                p.Phase.Id.Value, p.Phase.StartsOn, p.Phase.EndsOn, MoneyView.From(p.Phase.Price), DurationView.From(p.Phase.Every),
                DurationView.From(p.Phase.CoverageEvery), p.Phase.MaxCycles, PayerView.From(p.Phase.Payer), p.Note)).ToList(),
            rows.Purchases.OrderBy(p => p.Purchase.Date).ThenBy(p => p.Purchase.Id.Value).Select(p => new PurchaseView(
                p.Purchase.Id.Value, p.Purchase.Date, p.Purchase.Label, MoneyView.From(p.Purchase.Price), PayerView.From(p.Purchase.Payer),
                p.Purchase.Kind == PurchaseKind.PrepaidTerm ? "prepaid_term" : p.Purchase.Kind.ToString().ToLowerInvariant(),
                p.Purchase.AllocatedAmount?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                // 1E2a design §2.4: a grant to what the reader cannot read is left out, as in the export (1D2 §5.1).
                p.Purchase.Grants.Where(g => visibleItems.Contains(g.SubscriptionId)).Select(g => new GrantView(
                    g.SubscriptionId.Value, DurationView.From(g.Duration), g.Source.ToString().ToLowerInvariant(), g.StartsOn)).ToList(),
                p.Note)).ToList(),
            !(await policy.CheckAsync(reader, query.ItemId, AccessKind.Write, ct)).TryGetFailure(out _));
        return Result.Success(SubscriptionViews.Detail(
            input, timeline, item.DisplayName, provider, window, Conversion.Of(cashPieces, amortizedPieces, reference, rates), editing));
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
