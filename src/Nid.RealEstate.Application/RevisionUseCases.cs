using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;
using NodaTime.Text;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;
using static Nid.RealEstate.Application.LoanReading;

namespace Nid.RealEstate.Application;

/// <summary>Rental spec §3.4: the revision of the moment of a tenancy, or why there is none.</summary>
public sealed record GetTenancyRevisionQuery(ItemId ItemId, Guid Tenancy) : IQuery<TenancyRevisionView>, IItemScoped
{
    public AccessKind Access => AccessKind.Read;
}

/// <summary>
/// Rental spec §3.3: the old tenancy ends on <paramref name="EffectiveOn"/>, a new one at the revised rent starts there; its id.
/// <paramref name="NewRent"/> makes it a free revision: no IRL or anniversary, any current last link but in energy class F or G.
/// </summary>
public sealed record AcceptRentRevisionCommand(ItemId ItemId, Guid Tenancy, LocalDate EffectiveOn, decimal? NewRent = null) : ICommand<Guid>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

/// <summary>Rental spec §3.3: removes the chain's last link and gives its predecessor back its end.</summary>
public sealed record UndoRentRevisionCommand(ItemId ItemId, Guid Tenancy) : ICommand<bool>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

/// <summary>Rental spec §3.4 (<c>get_rent_revisions</c>): the revisions due in the scope.</summary>
public sealed record GetRentRevisionsQuery(VisibilityScope Scope) : IQuery<RentRevisionList>;

/// <summary>
/// <c>State</c> is <c>none</c>, <c>due</c>, <c>irl_missing</c> (<c>MissingQuarter</c> names the quarter) or <c>forbidden_dpe</c>;
/// <c>LostAnniversary</c>: the previous anniversary passed without a revision. IRL values and rents as decimal strings.
/// <c>IrlChangePct</c> is the IRL change and <c>Cpi.Percent</c> the CPI change over the same quarters (not a real change), one decimal.
/// </summary>
public sealed record TenancyRevisionView(
    string State, LocalDate? Anniversary, string? FromQuarter, string? FromValue, string? ToQuarter, string? ToValue, string? CurrentRent,
    string? NewRent, LocalDate? DefaultEffectiveOn, LocalDate? LostAnniversary, bool DpeUnknown, string? MissingQuarter,
    decimal? IrlChangePct, RealPercentView Cpi);

/// <summary>A revision due, with its property; rents in the property's <c>Currency</c>.</summary>
public sealed record RentRevisionDueView(
    ItemId PropertyId, string PropertyName, string Currency, Guid TenancyId, LocalDate Anniversary, string FromQuarter, string FromValue,
    string ToQuarter, string ToValue, string CurrentRent, string NewRent, LocalDate DefaultEffectiveOn);

public sealed record RentRevisionList(IReadOnlyList<RentRevisionDueView> Items) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => Items.Select(i => i.PropertyId);
}

/// <summary>Where a chain's last link stands: its revision state and the anniversary it lost, if any.</summary>
internal sealed record ChainRevision(StoredTenancy Last, RevisionState State, LocalDate? Lost);

/// <summary>Rental spec §3.3 over a stored property: chains by <c>revised_from</c>, only their last link is revised.</summary>
internal static class RentRevisions
{
    /// <summary>Each chain's last link (no successor), in the property's order: the others' ends are revisions, not lease ends.</summary>
    public static IEnumerable<StoredTenancy> LastLinks(StoredProperty property)
        => property.Tenancies.Where(t => property.Tenancies.All(s => s.RevisedFrom != t.Id));

    public static IEnumerable<ChainRevision> Of(StoredProperty property, IReadOnlyList<IrlValue> irl, LocalDate today)
        => LastLinks(property).Select(t => Of(property, t, irl, today));

    /// <summary>A link with a successor is history: none. An ended tenancy is not revised either.</summary>
    public static ChainRevision Of(StoredProperty property, StoredTenancy link, IReadOnlyList<IrlValue> irl, LocalDate today)
    {
        var t = link.Tenancy;
        if (property.Tenancies.Any(s => s.RevisedFrom == link.Id) || t.EndsOn is { } end && end < today)
            return new ChainRevision(link, new RevisionNone(), null);
        var chain = new List<StoredTenancy> { link };
        while (chain[^1].RevisedFrom is { } previous && property.Tenancies.FirstOrDefault(p => p.Id == previous) is { } before)
            chain.Add(before);
        var start = chain[^1].Tenancy.StartsOn;
        var revisedThisYear = link.RevisedFrom is not null && t.StartsOn >= RentRevision.Current(start, today);
        var state = RentRevision.State(start, t, t.Indexed, t.IrlReferenceQuarter, revisedThisYear, property.Details.EnergyClass, irl, today);
        var lost = t is { Indexed: true, IrlReferenceQuarter: not null }
            ? RentRevision.Lost(start, chain.SkipLast(1).Select(l => l.Tenancy.StartsOn).ToList(), property.Details.EnergyClass, today)
            : null;
        return new ChainRevision(link, state, lost);
    }

    public static TenancyRevisionView View(ChainRevision c, InflationSeries series)
    {
        var rent = Amount(c.Last.Tenancy.RentMonthly);
        return c.State switch
        {
            RevisionDue d => new("due", d.Anniversary, d.From.Quarter.ToString(), Amount(d.From.Value), d.To.Quarter.ToString(), Amount(d.To.Value),
                rent, Amount(d.NewRent), d.DefaultEffectiveOn, c.Lost, d.DpeUnknown, null, Round1((d.To.Value / d.From.Value - 1) * 100), Cpi(series, d)),
            RevisionIrlMissing m => new("irl_missing", m.Anniversary, null, null, null, null, rent, null, null, c.Lost, false, m.Missing.ToString(), null, RealPercentView.None),
            RevisionForbiddenDpe f => new("forbidden_dpe", f.Anniversary, null, null, null, null, rent, null, null, c.Lost, false, null, null, RealPercentView.None),
            _ => new("none", null, null, null, null, null, rent, null, null, c.Lost, false, null, null, RealPercentView.None),
        };
    }

    private static decimal Round1(decimal x) => Math.Round(x, 1, MidpointRounding.AwayFromZero);

    /// <summary>The CPI change between the IRL's two quarters (means of their months), and the last month it covers.</summary>
    private static RealPercentView Cpi(InflationSeries series, RevisionDue d)
    {
        if (series.LastPublished is null) return new(null, null, RealMissing.Unavailable);
        var (from, to) = (d.From.Quarter, d.To.Quarter);
        var end = new YearMonth(to.Year, to.Quarter * 3);
        var stop = YearMonthPattern.Iso.Format(end < series.LastPublished!.Value ? end : series.LastPublished.Value);
        return series.QuarterMean(from.Year, from.Quarter) is { } a && series.QuarterMean(to.Year, to.Quarter) is { } b
            ? new(Round1((b / a - 1) * 100), stop, null)
            : new(null, stop, RealMissing.NotPublished);
    }

    public static Failure Refusal(string code, string message, string? field = null)
        => new(code, message, field is null ? null : new Dictionary<string, object?> { ["field"] = field });
}

[RequestHandler<GetTenancyRevisionQuery, TenancyRevisionView>]
public sealed class GetTenancyRevisionHandler(IPropertyStore properties, IItemRegistry items, IIrlStore irl, IHouseholdCalendar calendar, IInflation inflation)
    : IRequestHandler<GetTenancyRevisionQuery, TenancyRevisionView>
{
    public async ValueTask<Result<TenancyRevisionView>> HandleAsync(GetTenancyRevisionQuery query, CancellationToken ct = default)
    {
        var loaded = await PropertyCheck.LoadAsync(properties, items, query.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<TenancyRevisionView>(failure!);
        loaded.TryGetValue(out var found);
        if (found.Property.Tenancies.FirstOrDefault(t => t.Id == query.Tenancy) is not { } tenancy)
            return Result.Failure<TenancyRevisionView>(new NotFoundFailure("tenancy", query.Tenancy.ToString()));
        var today = await calendar.TodayAsync(ct);
        return Result.Success(RentRevisions.View(RentRevisions.Of(found.Property, tenancy, await irl.ListAsync(ct), today), await inflation.ReadAsync(ct)));
    }
}

/// <summary>
/// Rental spec §3.3: under the property's lock, the state is computed again from the tenancies as they now are; the old link's end
/// is the one write that passes the chain guard, since it is what makes it a predecessor.
/// </summary>
[RequestHandler<AcceptRentRevisionCommand, Guid>]
public sealed class AcceptRentRevisionHandler(
    IItemRegistry items, IPropertyStore properties, IIrlStore irl, IHouseholdCalendar calendar, IUnitOfWork unitOfWork)
    : IRequestHandler<AcceptRentRevisionCommand, Guid>
{
    public async ValueTask<Result<Guid>> HandleAsync(AcceptRentRevisionCommand command, CancellationToken ct = default)
    {
        await using var transaction = await unitOfWork.BeginAsync(ct);
        var loaded = await PropertyCheck.LockAsync(properties, items, command.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<Guid>(failure!);
        loaded.TryGetValue(out var found);
        if (found.Property.Tenancies.FirstOrDefault(t => t.Id == command.Tenancy) is not { } current)
            return Result.Failure<Guid>(new NotFoundFailure("tenancy", command.Tenancy.ToString()));

        var today = await calendar.TodayAsync(ct);
        var old = current.Tenancy;
        decimal newRent;
        if (command.NewRent is { } typed)
        {
            if (found.Property.Tenancies.Any(t => t.RevisedFrom == current.Id) || old.EndsOn is { } end && end < today)
                return Result.Failure<Guid>(RentRevisions.Refusal(PropertyCodes.RevisionNotDue, "Only the last link of a running tenancy is revised."));
            if (found.Property.Details.EnergyClass is "F" or "G")
                return Result.Failure<Guid>(RentRevisions.Refusal(PropertyCodes.RevisionForbiddenDpe, "A property in energy class F or G is not revised."));
            if (typed <= 0)
                return Result.Failure<Guid>(RentRevisions.Refusal(PropertyCodes.InvalidPropertyField, "The new rent is positive.", "newRent"));
            if (!RentRevision.EffectiveOnAllowed(command.EffectiveOn, old.StartsOn.PlusDays(1), today, old.EndsOn))
                return Result.Failure<Guid>(RentRevisions.Refusal(PropertyCodes.RevisionFreeEffectiveOn,
                    "The effective date lies after the tenancy's start, up to today, on or before its end.", "effectiveOn"));
            newRent = typed;
        }
        else
        {
            var state = RentRevisions.Of(found.Property, current, await irl.ListAsync(ct), today).State;
            if (state is RevisionForbiddenDpe)
                return Result.Failure<Guid>(RentRevisions.Refusal(PropertyCodes.RevisionForbiddenDpe, "A property in energy class F or G is not revised."));
            if (state is not RevisionDue due)
                return Result.Failure<Guid>(RentRevisions.Refusal(PropertyCodes.RevisionNotDue, "This tenancy has no revision due."));
            if (!RentRevision.EffectiveOnAllowed(command.EffectiveOn, due.Anniversary, today, old.EndsOn))
                return Result.Failure<Guid>(RentRevisions.Refusal(PropertyCodes.RevisionEffectiveOn,
                    "The effective date lies between the anniversary and today, on or before the tenancy's end.", "effectiveOn"));
            newRent = due.NewRent;
        }

        var id = Guid.CreateVersion7();
        await properties.UpdateTenancyAsync(command.ItemId, current.Id, old with { EndsOn = command.EffectiveOn.PlusDays(-1) }, ct);
        await properties.AddTenancyAsync(command.ItemId, id, old with { StartsOn = command.EffectiveOn, RentMonthly = newRent }, ct, revisedFrom: current.Id);
        await transaction.CommitAsync(ct);
        return Result.Success(id);
    }
}

[RequestHandler<UndoRentRevisionCommand, bool>]
public sealed class UndoRentRevisionHandler(IItemRegistry items, IPropertyStore properties, IFlows flows, IUnitOfWork unitOfWork)
    : IRequestHandler<UndoRentRevisionCommand, bool>
{
    public async ValueTask<Result<bool>> HandleAsync(UndoRentRevisionCommand command, CancellationToken ct = default)
    {
        await using var transaction = await unitOfWork.BeginAsync(ct);
        var loaded = await PropertyCheck.LockAsync(properties, items, command.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<bool>(failure!);
        loaded.TryGetValue(out var found);
        var tenancies = found.Property.Tenancies;
        if (tenancies.FirstOrDefault(t => t.Id == command.Tenancy) is not { } link)
            return Result.Failure<bool>(new NotFoundFailure("tenancy", command.Tenancy.ToString()));

        var refused = RentRevisions.Refusal(PropertyCodes.RevisionUndoRefused,
            "Only a chain's last revision is undone, while no rent or charges are recorded from its start.");
        if (link.RevisedFrom is not { } previousId || tenancies.Any(t => t.RevisedFrom == link.Id))
            return Result.Failure<bool>(refused);
        var since = DateRange.Create(link.Tenancy.StartsOn, null).TryGetValue(out var range) ? range : throw new InvalidOperationException();
        var cashed = await flows.ListAsync(command.ItemId, since, new HashSet<FlowKind> { FlowKind.Income }, ct);
        if (cashed.TryGetFailure(out failure))
            return Result.Failure<bool>(failure!);
        cashed.TryGetValue(out var received);
        if (received!.Any(f => f.Category is { } c && PropertyCategories.CashIn.Contains(c)))
            return Result.Failure<bool>(refused);

        var previous = tenancies.First(t => t.Id == previousId).Tenancy;
        await properties.DeleteTenancyAsync(command.ItemId, link.Id, ct);
        await properties.UpdateTenancyAsync(command.ItemId, previousId, previous with { EndsOn = link.Tenancy.EndsOn }, ct);
        await transaction.CommitAsync(ct);
        return Result.Success(true);
    }
}

[RequestHandler<GetRentRevisionsQuery, RentRevisionList>]
public sealed class GetRentRevisionsHandler(
    IVisibleItems visible, IItemRegistry items, IPropertyStore properties, IIrlStore irl, IHouseholdCalendar calendar, IActorAccessor actor)
    : IRequestHandler<GetRentRevisionsQuery, RentRevisionList>
{
    public async ValueTask<Result<RentRevisionList>> HandleAsync(GetRentRevisionsQuery query, CancellationToken ct = default)
    {
        var ids = await visible.ForAsync(actor.Current!.PersonId!.Value, query.Scope, ct);
        var today = await calendar.TodayAsync(ct);
        var found = await properties.FindManyAsync(ids.ToList(), ct);
        var accesses = await items.FindManyAsync(found.Select(p => p.ItemId).ToList(), ct);
        var series = await irl.ListAsync(ct);
        var due = from p in found
                  where accesses.ContainsKey(p.ItemId)
                  from c in RentRevisions.Of(p, series, today)
                  where c.State is RevisionDue
                  let d = (RevisionDue)c.State
                  let item = accesses[p.ItemId]
                  orderby d.Anniversary, item.DisplayName, c.Last.Id
                  select new RentRevisionDueView(p.ItemId, item.DisplayName, LoanViews.CurrencyOf(item).Code, c.Last.Id, d.Anniversary,
                      d.From.Quarter.ToString(), Amount(d.From.Value), d.To.Quarter.ToString(), Amount(d.To.Value),
                      Amount(c.Last.Tenancy.RentMonthly), Amount(d.NewRent), d.DefaultEffectiveOn);
        return Result.Success(new RentRevisionList(due.ToList()));
    }
}
