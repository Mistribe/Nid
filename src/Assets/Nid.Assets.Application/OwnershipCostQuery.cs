using System.Globalization;
using Nid.Assets.Contracts;
using Nid.Assets.Domain;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.RealEstate.Contracts;
using Nid.Subscriptions.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Assets.Application;

/// <summary>#650: a vehicle's cost of ownership for <paramref name="Year"/>, or the 12 months to today when none.</summary>
public sealed record GetOwnershipCostQuery(ItemId ItemId, int? Year) : IQuery<OwnershipCostView>, IItemScoped
{
    public AccessKind Access => AccessKind.Read;
}

/// <param name="Code"><c>running:&lt;category&gt;</c>, <c>subscription</c>, <c>credit</c> or <c>depreciation</c>.</param>
/// <param name="Amount">Null when Nid has no figure for the part: it is left out of the total.</param>
/// <param name="SourceId">The linked subscription or loan.</param>
public sealed record CostPartView(string Code, MoneyView? Amount, Guid? SourceId, string? Name);

/// <param name="Incomplete">A part is missing: the total counts the others only.</param>
/// <param name="Years">Every year since the purchase or the first cost, newest first, and the one asked for.</param>
public sealed record OwnershipCostView(LocalDate From, LocalDate To, MoneyView Total, MoneyView Monthly, MoneyView? PerKm, bool Incomplete,
    IReadOnlyList<CostPartView> Parts, IReadOnlyList<int> Years);

[RequestHandler<GetOwnershipCostQuery, OwnershipCostView>]
public sealed class GetOwnershipCostHandler(
    IAssetRecordStore assets, IItemRegistry items, IValuations valuations, IFlows flows, IHouseholdCalendar calendar, IActorAccessor actor,
    ISubscriptionsOfItems subscriptions, IItemLoans loans)
    : IRequestHandler<GetOwnershipCostQuery, OwnershipCostView>
{
    public async ValueTask<Result<OwnershipCostView>> HandleAsync(GetOwnershipCostQuery query, CancellationToken ct = default)
    {
        var loaded = await AssetCosts.VehicleAsync(assets, items, query.ItemId, ct);
        if (!loaded.TryGetValue(out var found))
            return Result.Failure<OwnershipCostView>(loaded.TryGetFailure(out var missing) ? missing! : throw new InvalidOperationException());
        var today = await calendar.TodayAsync(ct);
        var details = found.Asset.Details;
        // A year is one the vehicle was held in: from its purchase to this one.
        var firstYear = details.PurchasedOn?.Year ?? ListAssetCostsHandler.FirstYear;
        if (query.Year is { } asked && (asked < Math.Max(firstYear, ListAssetCostsHandler.FirstYear) || asked > today.Year))
            return Result.Failure<OwnershipCostView>(AssetCheck.Shape("year", $"A year is one the vehicle was held in, {firstYear} to {today.Year}."));
        var (from, to) = query.Year is { } year
            ? (new LocalDate(year, 1, 1), LocalDate.Min(new LocalDate(year, 12, 31), today))
            : (today.PlusYears(-1).PlusDays(1), today);

        var all = await flows.ListAsync(query.ItemId, AssetCheck.Everything, new HashSet<FlowKind> { FlowKind.Expense }, ct);
        if (!all.TryGetValue(out var costs))
            return Result.Failure<OwnershipCostView>(all.TryGetFailure(out var unread) ? unread! : throw new InvalidOperationException());
        var series = await valuations.SeriesAsync(query.ItemId, AssetCheck.Everything, ct);
        if (!series.TryGetValue(out var points))
            return Result.Failure<OwnershipCostView>(series.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException());

        var reader = actor.Current!.PersonId!.Value;
        var start = OwnershipMath.Start(from, details.PurchasedOn);
        var linked = (await subscriptions.CostsAsync(reader, VisibilityScope.Me, [query.ItemId], start, to, ct)).GetValueOrDefault(query.ItemId) ?? [];
        var financing = (await loans.CostsAsync(reader, VisibilityScope.Me, [query.ItemId], start, to, ct)).GetValueOrDefault(query.ItemId) ?? [];
        var values = points!.Select(v => (v.AsOf, v.Value.Amount)).ToList();

        var cost = OwnershipMath.Compute(from, to, details.PurchasedOn,
            costs!.Where(f => f.OccurredOn >= start && f.OccurredOn <= to)
                .GroupBy(f => f.Category ?? AssetCostCategories.Other).ToDictionary(g => g.Key, g => g.Sum(f => f.Amount.Amount)),
            linked.Select(s => (s.Id.Value, s.Name, s.Cost?.Amount)).ToList(),
            financing.Select(l => (l.Loan.Value, l.Name, l.Cost?.Amount)).ToList(),
            on => AssetMath.At(details, values, on), await assets.ReadingsAsync(query.ItemId, ct));

        var currency = found.Item.Currency ?? "EUR";
        MoneyView Money(decimal x) => new(x.ToString("0.00", CultureInfo.InvariantCulture), currency);
        var first = details.PurchasedOn?.Year ?? costs!.Select(f => f.OccurredOn.Year).Append(today.Year).Min();
        var years = Enumerable.Range(first, today.Year - first + 1).Append(query.Year ?? today.Year).Distinct().OrderDescending().ToList();
        return Result.Success(new OwnershipCostView(cost.From, cost.To, Money(cost.Total), Money(cost.Monthly), cost.PerKm is { } km ? Money(km) : null,
            cost.Incomplete, cost.Parts.Select(p => new CostPartView(p.Code, p.Amount is { } a ? Money(a) : null, p.Source, p.Name)).ToList(), years));
    }
}
