using System.Globalization;
using Nid.Core.Contracts;
using Nid.Income.Contracts;
using Nid.Income.Domain;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;
using Duration = Nid.Core.Contracts.Duration;

namespace Nid.Income.Application;

/// <summary>A phase as a body sends it: no id and no version yet. The label is trimmed when stored.</summary>
public sealed record PhaseDraft(
    string Label, LocalDate StartsOn, LocalDate? EndsOn, Money NetAmount, Duration Every, Money? GrossAmount, int? MaxCycles, string? Note, Money? TaxableAmount = null);

public sealed record CreateIncomeSourceCommand(
    string DisplayName, string Kind, Currency Currency, Visibility Visibility, IReadOnlyList<OwnerShare> Owners,
    string? PayerLabel, string? Note, IReadOnlyList<PhaseDraft> Phases, ShareLevel ShareLevel = ShareLevel.View) : ICommand<ItemId>, ICreatesItem;

/// <summary>#69: <paramref name="Visibility"/>, <paramref name="ShareLevel"/> and <paramref name="Owners"/> null keep the source's.</summary>
public sealed record EditIncomeSourceCommand(ItemId ItemId, string? DisplayName, string? Kind, IncomeSourceChange Change,
    Visibility? Visibility = null, ShareLevel? ShareLevel = null, IReadOnlyList<OwnerShare>? Owners = null)
    : ICommand<ItemId>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

/// <summary>
/// One source with its one-offs dated from <paramref name="From"/> to <paramref name="To"/>, inclusive: without either, the
/// last 12 months; with one, the other side is open (issue #22).
/// </summary>
public sealed record GetIncomeSourceQuery(ItemId ItemId, LocalDate? From = null, LocalDate? To = null) : IQuery<IncomeSourceDetail>, IItemScoped
{
    public AccessKind Access => AccessKind.Read;
}

public sealed record ListIncomeSourcesQuery(VisibilityScope Scope, bool IncludeEnded) : IQuery<IncomeSourceList>;

/// <summary>#23: an income source created by mistake, deleted with its phases and one-off incomes. <paramref name="Version"/>: the one the preview read.</summary>
public sealed record DeleteIncomeSourceCommand(ItemId ItemId, uint Version) : ICommand<ItemId>, IItemScoped, IConfirmedInBrowser
{
    public AccessKind Access => AccessKind.Write;
}

/// <summary>#23: what deleting an income source removes, read before the delete.</summary>
public sealed record IncomeSourceDeletionQuery(ItemId ItemId) : IQuery<ItemDeletionPreview>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

[RequestHandler<CreateIncomeSourceCommand, ItemId>]
public sealed class CreateIncomeSourceHandler(IItemRegistry items, ISourceStore sources, IPhaseStore phases, IUnitOfWork unitOfWork)
    : IRequestHandler<CreateIncomeSourceCommand, ItemId>
{
    public async ValueTask<Result<ItemId>> HandleAsync(CreateIncomeSourceCommand command, CancellationToken ct = default)
    {
        var kind = SourceRules.Kind(command.Kind);
        if (kind.TryGetFailure(out var failure)
            || SourceRules.Name(command.DisplayName).TryGetFailure(out failure)
            || SourceRules.Check(command.PayerLabel, command.Note).TryGetFailure(out failure))
            return Result.Failure<ItemId>(failure!);
        kind.TryGetValue(out var k);

        // Every phase alone, then each against the ones before it: the second of two overlapping phases is the one refused.
        var drafts = command.Phases ?? [];
        var accepted = new List<IncomePhase>();
        for (var i = 0; i < drafts.Count; i++)
        {
            var d = drafts[i];
            if (PhaseRules.Check(d.Label, d.StartsOn, d.EndsOn, d.NetAmount, d.Every, d.GrossAmount, d.TaxableAmount, d.MaxCycles, d.Note, command.Currency).TryGetFailure(out failure))
                return Result.Failure<ItemId>(PhaseRules.Under(failure!, $"phases[{i}]"));
            var label = d.Label.Trim();
            if (PhaseRules.NoOverlap(label, d.StartsOn, d.EndsOn, d.Every, d.MaxCycles, accepted).TryGetFailure(out failure))
                return Result.Failure<ItemId>(PhaseRules.Under(failure!, $"phases[{i}]"));
            accepted.Add(new IncomePhase(Guid.CreateVersion7(), default, label, d.StartsOn, d.EndsOn, d.NetAmount, d.Every,
                d.GrossAmount, d.MaxCycles, d.Note, TaxableAmount: d.TaxableAmount));
        }

        await using var transaction = await unitOfWork.BeginAsync(ct);
        var created = await items.CreateAsync(new NewItem("income", "source", command.DisplayName, command.Visibility,
            command.Currency.Code, command.Owners, command.ShareLevel), ct);
        if (!created.TryGetValue(out var id))
            return created; // disposed without commit: nothing is left
        await sources.CreateAsync(new IncomeSource(id, k, command.PayerLabel, command.Note), null, ct);
        foreach (var phase in accepted)
            await phases.AddAsync(phase with { Source = id }, null, ct);
        await transaction.CommitAsync(ct);
        return Result.Success(id);
    }
}

[RequestHandler<EditIncomeSourceCommand, ItemId>]
public sealed class EditIncomeSourceHandler(
    IItemRegistry items, ISourceStore sources, IUnitOfWork unitOfWork, IItemRules rules, IActorAccessor actor, IPersonRegistry persons)
    : IRequestHandler<EditIncomeSourceCommand, ItemId>
{
    public async ValueTask<Result<ItemId>> HandleAsync(EditIncomeSourceCommand command, CancellationToken ct = default)
    {
        var loaded = await SourceCheck.LoadAsync(sources, items, command.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<ItemId>(failure!);
        loaded.TryGetValue(out var source);
        if (SourceCheck.RequireOpen(source.Item).TryGetFailure(out failure))
            return Result.Failure<ItemId>(failure!);
        if (command.DisplayName is not null && SourceRules.Name(command.DisplayName).TryGetFailure(out failure))
            return Result.Failure<ItemId>(failure!);

        IncomeKind? kind = null;
        if (command.Kind is not null)
        {
            var parsed = SourceRules.Kind(command.Kind);
            if (!parsed.TryGetValue(out var k))
                return Result.Failure<ItemId>(parsed.TryGetFailure(out failure) ? failure! : throw new InvalidOperationException());
            kind = k;
        }

        var change = command.Change with { Kind = kind };
        if (SourceRules.Check(change.PayerLabel, change.Note).TryGetFailure(out failure))
            return Result.Failure<ItemId>(failure!);

        var item = source.Item;
        var target = OwnershipEdit.Of(item, command.Visibility, command.ShareLevel, command.Owners);
        if (await OwnershipEdit.RefusalAsync(rules, persons, actor.Current!.PersonId!.Value, item, target, "income source", ct) is { } refused)
            return Result.Failure<ItemId>(refused);

        await using var transaction = await unitOfWork.BeginAsync(ct);
        var replaced = await OwnershipEdit.ReplaceAsync(items, item, command.DisplayName ?? item.DisplayName, target, ct);
        if (replaced.TryGetFailure(out failure))
            return Result.Failure<ItemId>(failure!);

        await sources.UpdateAsync(command.ItemId, change, ct);
        await transaction.CommitAsync(ct);
        return Result.Success(command.ItemId);
    }
}

[RequestHandler<GetIncomeSourceQuery, IncomeSourceDetail>]
public sealed class GetIncomeSourceHandler(
    IItemRegistry items, ISourceStore sources, IPhaseStore phases, IFlows flows, IOneOffTaxableStore taxables, IHouseholdCalendar calendar,
    IAccessPolicy policy, IActorAccessor actor, IItemRules rules)
    : IRequestHandler<GetIncomeSourceQuery, IncomeSourceDetail>
{
    public async ValueTask<Result<IncomeSourceDetail>> HandleAsync(GetIncomeSourceQuery query, CancellationToken ct = default)
    {
        var loaded = await SourceCheck.LoadAsync(sources, items, query.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<IncomeSourceDetail>(failure!);
        loaded.TryGetValue(out var source);
        var (s, item) = source;
        var list = await phases.ForSourceAsync(query.ItemId, ct);
        var today = await calendar.TodayAsync(ct);
        var currency = SourceCheck.CurrencyOf(item);
        var canWrite = (await policy.CheckAsync(actor.Current!.PersonId!.Value, query.ItemId, AccessKind.Write, ct)).IsSuccess;

        // By default the one-offs of the last 12 months: a year before today, inclusive, to today.
        var window = query.From is null && query.To is null
            ? DateRange.Create(today.PlusYears(-1), today.PlusDays(1))
            : DateRange.Create(query.From ?? LocalDate.MinIsoValue, query.To?.PlusDays(1));
        if (!window.TryGetValue(out var range))
            return Result.Failure<IncomeSourceDetail>(new Failure(IncomeCodes.InvalidPeriod, "A period starts on or before its end.", null));
        var oneOffs = await OneOffsAsync(flows, query.ItemId, range!, ct);
        if (oneOffs.TryGetFailure(out failure))
            return Result.Failure<IncomeSourceDetail>(failure!);
        oneOffs.TryGetValue(out var recent);
        var taxable = await taxables.ForFlowsAsync(recent!.Select(f => f.Id).ToList(), ct);

        return Result.Success(new IncomeSourceDetail(
            query.ItemId, item.DisplayName, IncomeWire.Of(s.Kind), s.PayerLabel, s.Note, currency.Code, Wire(item.Visibility),
            canWrite, item.ArchivedAt is not null, s.EndedOn, IncomeAmountView.Of(IncomeMath.MonthlyEquivalent(list, today), currency),
            list.Select(p => IncomePhaseView.Of(p, today, currency)).ToList(),
            IncomeMath.Gaps(list, today).Select(g => new IncomeGapView(g.Label, g.EndsOn)).ToList(),
            recent!.Select(f => IncomeOneOffView.Of(f, taxable)).ToList(),
            item.OwnerViews(), item.Visibility == Visibility.Shared && item.ShareLevel == ShareLevel.Edit ? "edit" : "view",
            canWrite && rules.CanCreate(actor.Current!.PersonId!.Value, item.Owners)));
    }

    /// <summary>The source's one-off income in <paramref name="range"/>: its Core flows <c>income</c> of category <c>one_off</c>.</summary>
    internal static async Task<Result<IReadOnlyList<Flow>>> OneOffsAsync(IFlows flows, ItemId source, DateRange range, CancellationToken ct)
    {
        var listed = await flows.ListAsync(source, range, new HashSet<FlowKind> { FlowKind.Income }, ct);
        if (!listed.TryGetValue(out var all))
            return listed;
        return Result.Success<IReadOnlyList<Flow>>(all!.Where(f => f.Category == IncomeWire.OneOffCategory).ToList());
    }

    internal static string Wire(Visibility visibility) => visibility == Visibility.Private ? "private" : "shared";
}

[RequestHandler<ListIncomeSourcesQuery, IncomeSourceList>]
public sealed class ListIncomeSourcesHandler(
    IVisibleItems visible, IItemRegistry items, ISourceStore sources, IPhaseStore phases, IHouseholdCalendar calendar,
    IAccessPolicy policy, IActorAccessor actor)
    : IRequestHandler<ListIncomeSourcesQuery, IncomeSourceList>
{
    private static readonly StringComparer French = StringComparer.Create(new CultureInfo("fr-FR"), true);

    public async ValueTask<Result<IncomeSourceList>> HandleAsync(ListIncomeSourcesQuery query, CancellationToken ct = default)
    {
        // As the envelopes' list: the 3-argument ForAsync returns active items only, so ended sources are asked for explicitly.
        var person = actor.Current!.PersonId!.Value;
        var ids = await visible.ForAsync(person, query.Scope, includeArchived: query.IncludeEnded, ct);
        var found = await sources.FindManyAsync(ids.ToList(), ct);
        var bySource = (await phases.ForSourcesAsync(found.Select(s => s.ItemId).ToList(), ct)).ToLookup(p => p.Source);
        var today = await calendar.TodayAsync(ct);

        var summaries = new List<IncomeSourceSummary>();
        foreach (var source in found)
        {
            var item = await items.FindAsync(source.ItemId, ct);
            if (item is null || (!query.IncludeEnded && item.ArchivedAt is not null))
                continue;
            var currency = SourceCheck.CurrencyOf(item);
            var own = bySource[source.ItemId].ToList();
            var canWrite = (await policy.CheckAsync(person, source.ItemId, AccessKind.Write, ct)).IsSuccess;
            summaries.Add(new IncomeSourceSummary(
                source.ItemId, item.DisplayName, IncomeWire.Of(source.Kind), source.PayerLabel, currency.Code,
                GetIncomeSourceHandler.Wire(item.Visibility),
                IncomeAmountView.Of(IncomeMath.MonthlyEquivalent(own, today), currency),
                own.Count(p => IncomeMath.IsActive(p, today)),
                IncomeMath.Gaps(own, today).Select(g => g.Label).Distinct(StringComparer.Ordinal).ToList(),
                canWrite, item.ArchivedAt is not null, source.EndedOn, item.OwnerViews()));
        }

        // A stable order: the name alone ties on a duplicate (as the envelopes' list).
        return Result.Success(new IncomeSourceList(summaries.OrderBy(s => s.DisplayName, French).ThenBy(s => s.Id.Value).ToList()));
    }
}

[RequestHandler<DeleteIncomeSourceCommand, ItemId>]
public sealed class DeleteIncomeSourceHandler(IItemDeletions deletions) : IRequestHandler<DeleteIncomeSourceCommand, ItemId>
{
    public async ValueTask<Result<ItemId>> HandleAsync(DeleteIncomeSourceCommand command, CancellationToken ct = default)
        => (await deletions.DeleteAsync(command.ItemId, "income", null, command.Version, ct)).Map(() => command.ItemId);
}

[RequestHandler<IncomeSourceDeletionQuery, ItemDeletionPreview>]
public sealed class IncomeSourceDeletionHandler(IItemDeletions deletions) : IRequestHandler<IncomeSourceDeletionQuery, ItemDeletionPreview>
{
    public async ValueTask<Result<ItemDeletionPreview>> HandleAsync(IncomeSourceDeletionQuery query, CancellationToken ct = default)
        => await deletions.PreviewAsync(query.ItemId, "income", null, ct);
}
