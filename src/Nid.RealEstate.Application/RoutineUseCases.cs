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

/// <summary>
/// A tenancy's expected rent of the month, to confirm as an income: its bare rent (<c>rent</c>) and, when it has charges, a second line for them
/// (<c>recoverable_charges</c>). <c>AlreadyEntered</c> sums the month's flows of the line's category.
/// </summary>
public sealed record PropertyRoutineRentView(
    ItemId Property, string Name, string Currency, Guid TenancyId, string Label, string Category, string Amount, LocalDate OccurredOn, string? AlreadyEntered);

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

    /// <summary>Spec 3E2 §3: a routine's income is a rent; its expenses are the planned costs' categories.</summary>
    private static readonly IReadOnlyList<string> Rents = [PropertyCategories.Rent, PropertyCategories.RecoverableCharges];

    /// <summary>Spec 3E2 §3: the request's own refusals, before any read — each estimate, then each flow, in order; the first wins.</summary>
    public static Failure? Refusal(RecordPropertyRoutineCommand command, LocalDate today)
    {
        var first = FirstOf(command.On);
        var estimated = new HashSet<ItemId>();
        for (var i = 0; i < command.Estimates.Count; i++)
        {
            var estimate = command.Estimates[i];
            if (!estimated.Add(estimate.Property))
                return Field(PropertyCodes.RoutineDuplicate, "A property is estimated once in a routine.", $"estimates[{i}]");
            if (PropertyRules.Positive(estimate.Amount, $"estimates[{i}].amount") is { } amount)
                return amount;
        }
        for (var i = 0; i < command.Flows.Count; i++)
        {
            var flow = command.Flows[i];
            var path = $"flows[{i}]";
            var allowed = flow.Kind switch { "expense" => PropertyRules.RecurringCategories, "income" => Rents, _ => null };
            if (allowed is null)
                return Field(PropertyCodes.InvalidCategory, $"A routine's flow is an expense or an income, not '{flow.Kind}'.", $"{path}.kind");
            if (flow.Category is null || !allowed.Contains(flow.Category))
                return Field(PropertyCodes.InvalidCategory, $"This {flow.Kind}'s category is one of {string.Join(", ", allowed)}.", $"{path}.category");
            if (flow.OccurredOn < first || flow.OccurredOn >= first.PlusMonths(1))
                return Field(PropertyCodes.InvalidPropertyField, "A routine's flow is dated in the routine's month.", $"{path}.occurredOn");
            if (flow.OccurredOn > today)
                return Field(CoreFactCodes.DateInFuture, "A flow is not dated after today.", $"{path}.occurredOn");
            if (PropertyRules.Positive(flow.Amount, $"{path}.amount") is { } amount)
                return amount;
        }
        return null;
    }

    /// <summary>
    /// Spec 3E2 §3: the properties a routine names, each one a property the reader writes, not archived; anything else —
    /// unreadable, read-only, not a property, unknown — answers « not found », never a 403 that would tell them apart.
    /// </summary>
    public static async Task<Result<IReadOnlyDictionary<ItemId, ItemAccess>>> WritableAsync(
        IItemRegistry items, IPropertyStore properties, IAccessPolicy policy, PersonId reader, IEnumerable<ItemId> named, CancellationToken ct)
    {
        var ids = named.Distinct().ToList();
        var writable = await policy.CheckManyAsync(reader, ids, AccessKind.Write, ct);
        var found = (await properties.FindManyAsync(ids.Where(writable.Contains).ToList(), ct)).Select(p => p.ItemId).ToList();
        var accesses = await items.FindManyAsync(found, ct);
        foreach (var id in ids)
            if (!accesses.TryGetValue(id, out var item) || item.ArchivedAt is not null)
                return Result.Failure<IReadOnlyDictionary<ItemId, ItemAccess>>(new NotFoundFailure("property", id.ToString()));
        return Result.Success(accesses);
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
            // Before the two lines, a month was one `rent` flow of rent + charges: with no charges flow, what the month's rents exceed the property's bare rents by
            // stands for the charges, handed out to its charges lines in their order.
            var covering = property.Tenancies.Select(t => (t, Bare: PropertyMath.RentIn(t.Tenancy, first, charges: false))).Where(x => x.Bare is not null)
                .OrderBy(x => x.Bare!.Value.From).ThenBy(x => x.t.Id).ToList();
            var chargesEntered = PropertyRoutine.Entered(ofMonth, FlowKind.Income, PropertyCategories.RecoverableCharges);
            var remaining = ofMonth.Where(f => f.Kind == FlowKind.Income && f.Category == PropertyCategories.Rent).Sum(f => f.Amount.Amount)
                - covering.Sum(x => x.Bare!.Value.Amount);
            foreach (var (tenancy, bare) in covering)
            {
                var rent = bare!.Value;
                rents.Add(new(property.ItemId, item.DisplayName, currency, tenancy.Id, tenancy.Tenancy.Label, PropertyCategories.Rent, LoanReading.Amount(rent.Amount), rent.From,
                    PropertyRoutine.Entered(ofMonth, FlowKind.Income, PropertyCategories.Rent)));
                var charges = PropertyMath.RentIn(tenancy.Tenancy, first, charges: true)!.Value.Amount - rent.Amount;
                if (charges <= 0)
                    continue;
                var legacy = Math.Min(charges, Math.Max(remaining, 0m));
                remaining -= legacy;
                rents.Add(new(property.ItemId, item.DisplayName, currency, tenancy.Id, tenancy.Tenancy.Label, PropertyCategories.RecoverableCharges, LoanReading.Amount(charges), rent.From,
                    chargesEntered ?? (legacy > 0 ? LoanReading.Amount(legacy) : null)));
            }
        }
        return Result.Success(new PropertyRoutineView(on,
            estimates.OrderBy(e => e.Due ? 0 : 1).ThenBy(e => e.Name, PropertyRoutine.French).ThenBy(e => e.Property.Value).ToList(),
            costs.OrderBy(c => c.Name, PropertyRoutine.French).ThenBy(c => c.DueOn).ThenBy(c => c.CostId).ToList(),
            rents.OrderBy(r => r.Name, PropertyRoutine.French).ThenBy(r => r.OccurredOn).ThenBy(r => r.TenancyId).ToList()));
    }
}

/// <summary>An estimate to record at the routine's date, in the property's currency.</summary>
public sealed record PropertyRoutineEstimate(ItemId Property, decimal Amount);

/// <summary>A cost or a rent to record: kind and category as sent, checked against the routine's convention (spec 3E2 §3).</summary>
public sealed record PropertyRoutineFlow(ItemId Property, string? Kind, string? Category, LocalDate OccurredOn, decimal Amount);

/// <summary>Spec 3E2 §3: every estimate and flow in one transaction, all or nothing; answers the recap.</summary>
public sealed record RecordPropertyRoutineCommand(
    LocalDate On, IReadOnlyList<PropertyRoutineEstimate> Estimates, IReadOnlyList<PropertyRoutineFlow> Flows) : ICommand<PropertyRoutineRecap>;

public sealed record PropertyRoutineRecapFlow(Guid FlowId, string Kind, string Category, LocalDate OccurredOn, string Amount);

/// <summary>What was recorded on one property: its estimate (null when none) and its flows, with the created facts' ids. No gain (spec §3).</summary>
public sealed record PropertyRoutineRecapLine(
    ItemId Property, string Name, string Currency, Guid? ValuationId, string? Estimate, IReadOnlyList<PropertyRoutineRecapFlow> Flows);

public sealed record PropertyRoutineRecap(LocalDate On, IReadOnlyList<PropertyRoutineRecapLine> Lines) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => Lines.Select(l => l.Property);
}

public sealed record PropertyRoutineFact(ItemId Property, Guid? ValuationId, Guid? FlowId);

/// <summary>Spec 3E2 §3: voids what a routine recorded, in one transaction (history kept).</summary>
public sealed record UndoPropertyRoutineCommand(IReadOnlyList<PropertyRoutineFact> Facts) : ICommand<PropertyRoutineUndone>;

public sealed record PropertyRoutineUndone(int Voided);

[RequestHandler<RecordPropertyRoutineCommand, PropertyRoutineRecap>]
public sealed class RecordPropertyRoutineHandler(
    IItemRegistry items, IPropertyStore properties, IAccessPolicy policy, IValuations valuations, IFlows flows,
    IActorAccessor actor, IHouseholdCalendar calendar, IUnitOfWork unitOfWork)
    : IRequestHandler<RecordPropertyRoutineCommand, PropertyRoutineRecap>
{
    public async ValueTask<Result<PropertyRoutineRecap>> HandleAsync(RecordPropertyRoutineCommand command, CancellationToken ct = default)
    {
        if (command.Estimates.Count == 0 && command.Flows.Count == 0)
            return Fail(PropertyRoutine.Field(PropertyCodes.RoutineEmpty, "A routine records at least one estimate or flow.", "estimates"));
        var today = await calendar.TodayAsync(ct);
        if (command.On > today)
            return Fail(PropertyRoutine.Field(CoreFactCodes.DateInFuture, "The routine's date cannot be after today.", "on"));
        if (PropertyRoutine.Refusal(command, today) is { } refused)
            return Fail(refused);

        // Everything is checked before any write: one refusal leaves nothing written.
        var named = command.Estimates.Select(e => e.Property).Concat(command.Flows.Select(f => f.Property));
        var writable = await PropertyRoutine.WritableAsync(items, properties, policy, actor.Current!.PersonId!.Value, named, ct);
        if (!writable.TryGetValue(out var accesses))
            return Fail(writable.TryGetFailure(out var missing) ? missing! : throw new InvalidOperationException());

        // Spec 3E2 §3: a normal estimate on the same date would replace the old one, which « Annuler » could not restore.
        var atOn = await valuations.SeriesManyAsync(command.Estimates.Select(e => e.Property).ToList(), PropertyRoutine.Range(command.On, command.On.PlusDays(1)), ct);
        if (atOn.TryGetFailure(out var failure))
            return Fail(failure!);
        atOn.TryGetValue(out var present);
        for (var i = 0; i < command.Estimates.Count; i++)
            if (present![command.Estimates[i].Property].Count > 0)
                return Fail(PropertyRoutine.Field(PropertyCodes.RoutineValueExists,
                    "This property already has an estimate at this date: correct it from its page.", $"estimates[{i}].amount"));

        Currency CurrencyOf(ItemId id) => LoanViews.CurrencyOf(accesses![id]);
        var valuationIds = new Dictionary<ItemId, Guid>();
        var recorded = new List<Flow>();
        await using (var transaction = await unitOfWork.BeginAsync(ct))
        {
            foreach (var estimate in command.Estimates)
            {
                var written = await valuations.RecordAsync(
                    new NewValuation(estimate.Property, command.On, PropertyFacts.MoneyOf(estimate.Amount, CurrencyOf(estimate.Property)), null), ct);
                if (!written.TryGetValue(out var valuation))
                    return Fail(written.TryGetFailure(out var refusedWrite) ? refusedWrite! : throw new InvalidOperationException());
                valuationIds[estimate.Property] = valuation!.Id;
            }
            foreach (var flow in command.Flows)
            {
                var written = await flows.RecordAsync(new NewFlow(flow.Property, flow.OccurredOn, PropertyRules.KindOf(flow.Kind!),
                    PropertyFacts.MoneyOf(flow.Amount, CurrencyOf(flow.Property)), flow.Category, null, null), ct);
                if (!written.TryGetValue(out var created))
                    return Fail(written.TryGetFailure(out var refusedWrite) ? refusedWrite! : throw new InvalidOperationException());
                recorded.Add(created!);
            }
            await transaction.CommitAsync(ct);
        }

        var lines = accesses!.Values
            .Select(item => new PropertyRoutineRecapLine(item.Id, item.DisplayName, LoanViews.CurrencyOf(item).Code,
                valuationIds.TryGetValue(item.Id, out var valuationId) ? valuationId : (Guid?)null,
                command.Estimates.FirstOrDefault(e => e.Property == item.Id) is { } sent ? LoanReading.Amount(sent.Amount) : null,
                recorded.Where(f => f.Item == item.Id)
                    .Select(f => new PropertyRoutineRecapFlow(f.Id, LoanWire.Of(f.Kind), f.Category!, f.OccurredOn, LoanReading.Amount(f.Amount.Amount)))
                    .ToList()))
            .OrderBy(l => l.Name, PropertyRoutine.French).ThenBy(l => l.Property.Value).ToList();
        return Result.Success(new PropertyRoutineRecap(command.On, lines));
    }

    private static Result<PropertyRoutineRecap> Fail(Failure failure) => Result.Failure<PropertyRoutineRecap>(failure);
}

[RequestHandler<UndoPropertyRoutineCommand, PropertyRoutineUndone>]
public sealed class UndoPropertyRoutineHandler(
    IItemRegistry items, IPropertyStore properties, IAccessPolicy policy, IValuations valuations, IFlows flows, IActorAccessor actor,
    IUnitOfWork unitOfWork)
    : IRequestHandler<UndoPropertyRoutineCommand, PropertyRoutineUndone>
{
    public async ValueTask<Result<PropertyRoutineUndone>> HandleAsync(UndoPropertyRoutineCommand command, CancellationToken ct = default)
    {
        if (command.Facts.Count == 0)
            return Result.Failure<PropertyRoutineUndone>(PropertyRoutine.Field(PropertyCodes.RoutineEmpty, "Nothing to undo.", "facts"));
        // The recording's checks: only properties the reader writes, never another item's facts (an envelope's, an income source's).
        var writable = await PropertyRoutine.WritableAsync(items, properties, policy, actor.Current!.PersonId!.Value, command.Facts.Select(f => f.Property), ct);
        if (writable.TryGetFailure(out var missing))
            return Result.Failure<PropertyRoutineUndone>(missing!);

        var voided = 0;
        await using (var transaction = await unitOfWork.BeginAsync(ct))
        {
            foreach (var fact in command.Facts)
            {
                if (fact.ValuationId is { } valuation)
                {
                    if ((await valuations.VoidAsync(fact.Property, valuation, ct)).TryGetFailure(out var failure))
                        return Result.Failure<PropertyRoutineUndone>(failure!);
                    voided++;
                }
                if (fact.FlowId is { } flow)
                {
                    if ((await flows.VoidAsync(fact.Property, flow, ct)).TryGetFailure(out var failure))
                        return Result.Failure<PropertyRoutineUndone>(failure!);
                    voided++;
                }
            }
            await transaction.CommitAsync(ct);
        }
        return Result.Success(new PropertyRoutineUndone(voided));
    }
}
