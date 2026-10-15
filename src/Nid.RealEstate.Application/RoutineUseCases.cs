using System.Globalization;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.RealEstate.Application;

/// <summary>Spec 3E2 §2: what the month's routine proposes for the properties, as of <c>On</c> (default: the end of the previous month).</summary>
public sealed record GetPropertyRoutineQuery(LocalDate? On) : IQuery<PropertyRoutineView>;

/// <summary>A property's estimate line: its latest estimate on or before <c>on</c>, and whether a new one is due.</summary>
public sealed record PropertyRoutineEstimateView(ItemId Property, string Name, string Currency, string? LastValue, LocalDate? LastAsOf, bool Due);

/// <summary>A planned cost's occurrence in the month, to confirm as an expense; <c>AlreadyEntered</c> sums the month's expenses of its category.</summary>
public sealed record PropertyRoutineCostView(
    ItemId Property, string Name, string Currency, Guid CostId, string Category, string Amount, LocalDate DueOn, string? AlreadyEntered);

/// <summary>A tenancy's expected rent of the month, to confirm as an income <c>rent</c>; <c>AlreadyEntered</c> sums the month's rents.</summary>
public sealed record PropertyRoutineRentView(
    ItemId Property, string Name, string Currency, Guid TenancyId, string Label, string Amount, LocalDate OccurredOn, string? AlreadyEntered);

public sealed record PropertyRoutineView(
    LocalDate On, IReadOnlyList<PropertyRoutineEstimateView> Estimates, IReadOnlyList<PropertyRoutineCostView> Costs,
    IReadOnlyList<PropertyRoutineRentView> Rents) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems()
        => [.. Estimates.Select(e => e.Property), .. Costs.Select(c => c.Property), .. Rents.Select(r => r.Property)];
}

/// <summary>What the routine's requests share.</summary>
internal static class PropertyRoutine
{
    public static readonly StringComparer French = StringComparer.Create(new CultureInfo("fr-FR"), true);

    /// <summary>The statements' date (2E2 decision 1): the last day of the month before <paramref name="today"/>'s.</summary>
    public static LocalDate DefaultDate(LocalDate today) => new LocalDate(today.Year, today.Month, 1).PlusDays(-1);

    /// <summary>The first day of M, the month of <paramref name="on"/>.</summary>
    public static LocalDate FirstOf(LocalDate on) => new(on.Year, on.Month, 1);

    public static DateRange Range(LocalDate start, LocalDate endBefore)
        => DateRange.Create(start, endBefore).TryGetValue(out var range) ? range : throw new InvalidOperationException("A routine's range is never empty.");

    public static Failure Field(string code, string message, string field)
        => new(code, message, new Dictionary<string, object?> { ["field"] = field });

    /// <summary>Spec 3E2 §2: the month's flows of this kind and category, summed, as text; null when there is none.</summary>
    public static string? Entered(IEnumerable<Flow> month, FlowKind kind, string category)
    {
        var same = month.Where(f => f.Kind == kind && f.Category == category).ToList();
        return same.Count == 0 ? null : LoanReading.Amount(same.Sum(f => f.Amount.Amount));
    }
}

[RequestHandler<GetPropertyRoutineQuery, PropertyRoutineView>]
public sealed class GetPropertyRoutineHandler(
    IVisibleItems visible, IItemRegistry items, IPropertyStore properties, IAccessPolicy policy, IValuations valuations, IFlows flows,
    IActorAccessor actor, IHouseholdCalendar calendar)
    : IRequestHandler<GetPropertyRoutineQuery, PropertyRoutineView>
{
    public async ValueTask<Result<PropertyRoutineView>> HandleAsync(GetPropertyRoutineQuery query, CancellationToken ct = default)
    {
        var reader = actor.Current!.PersonId!.Value;
        var today = await calendar.TodayAsync(ct);
        var on = query.On ?? PropertyRoutine.DefaultDate(today);
        if (on > today)
            return Result.Failure<PropertyRoutineView>(PropertyRoutine.Field(CoreFactCodes.DateInFuture, "The routine's date cannot be after today.", "on"));
        var first = PropertyRoutine.FirstOf(on);

        // Batched reads (architecture.md §5.3): the properties, their items, the write checks, the estimates and the month's flows, each once.
        var found = await properties.FindManyAsync((await visible.ForAsync(reader, VisibilityScope.Me, ct)).ToList(), ct);
        var accesses = await items.FindManyAsync(found.Select(p => p.ItemId).ToList(), ct);
        var writable = await policy.CheckManyAsync(reader, accesses.Keys.ToList(), AccessKind.Write, ct);
        var listed = found.Where(p => writable.Contains(p.ItemId)).ToList();
        var ids = listed.Select(p => p.ItemId).ToList();
        var series = await valuations.SeriesManyAsync(ids, PropertyReading.Everything, ct);
        if (series.TryGetFailure(out var failure))
            return Result.Failure<PropertyRoutineView>(failure!);
        var month = await flows.ListManyAsync(ids, PropertyRoutine.Range(first, first.PlusMonths(1)), null, ct);
        if (month.TryGetFailure(out failure))
            return Result.Failure<PropertyRoutineView>(failure!);
        series.TryGetValue(out var values);
        month.TryGetValue(out var entered);

        var estimates = new List<PropertyRoutineEstimateView>();
        var costs = new List<PropertyRoutineCostView>();
        var rents = new List<PropertyRoutineRentView>();
        foreach (var property in listed)
        {
            var item = accesses[property.ItemId];
            var currency = LoanViews.CurrencyOf(item).Code;
            // « À faire »'s rule (PropertyMath.At): due without an estimate on or before `on`, or when that one is stale at `on`.
            var at = PropertyMath.At(property.Details, false, values![property.ItemId], [], [], [], on);
            estimates.Add(new(property.ItemId, item.DisplayName, currency, LoanReading.Amount(at.Value), at.ValueOn, at.Value is null || at.Stale));
            var ofMonth = entered![property.ItemId];
            foreach (var cost in property.Costs)
                if (PropertyMath.DueIn(cost.Cost, first) is { } dueOn)
                    costs.Add(new(property.ItemId, item.DisplayName, currency, cost.Id, cost.Cost.Category, LoanReading.Amount(cost.Cost.Amount), dueOn,
                        PropertyRoutine.Entered(ofMonth, FlowKind.Expense, cost.Cost.Category)));
            foreach (var tenancy in property.Tenancies)
                if (PropertyMath.RentIn(tenancy.Tenancy, first) is { } rent)
                    rents.Add(new(property.ItemId, item.DisplayName, currency, tenancy.Id, tenancy.Tenancy.Label, LoanReading.Amount(rent.Amount), rent.From,
                        PropertyRoutine.Entered(ofMonth, FlowKind.Income, PropertyCategories.Rent)));
        }
        return Result.Success(new PropertyRoutineView(on,
            estimates.OrderBy(e => e.Due ? 0 : 1).ThenBy(e => e.Name, PropertyRoutine.French).ThenBy(e => e.Property.Value).ToList(),
            costs.OrderBy(c => c.Name, PropertyRoutine.French).ThenBy(c => c.DueOn).ThenBy(c => c.CostId).ToList(),
            rents.OrderBy(r => r.Name, PropertyRoutine.French).ThenBy(r => r.OccurredOn).ThenBy(r => r.TenancyId).ToList()));
    }
}
