using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Investments.Application;

public sealed record OpeningDraft(LocalDate OccurredOn, Money Amount);

public sealed record FirstValuationDraft(LocalDate AsOf, Money Amount);

public sealed record CreateEnvelopeCommand(
    string DisplayName, string Kind, Currency Currency, Visibility Visibility, IReadOnlyList<OwnerShare> Owners,
    string? Institution, string? AccountHint, LocalDate? OpenedOn, string? ValuationCadence, int? StaleAfterDays, string? Note,
    OpeningDraft? Opening, FirstValuationDraft? FirstValuation, decimal? PremiumsBefore2017 = null, ShareLevel ShareLevel = ShareLevel.View,
    string? Regulated = null)
    : ICommand<ItemId>, ICreatesItem;

/// <summary>#25: <paramref name="Visibility"/>, <paramref name="ShareLevel"/> and <paramref name="Owners"/> null keep the envelope's.</summary>
public sealed record EditEnvelopeCommand(ItemId ItemId, string? DisplayName, string? Kind, EnvelopeChange Change, string? ValuationCadence, string? Regulated = null,
    Visibility? Visibility = null, ShareLevel? ShareLevel = null, IReadOnlyList<OwnerShare>? Owners = null)
    : ICommand<ItemId>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

public sealed record GetEnvelopeQuery(ItemId ItemId) : IQuery<EnvelopeDetail>, IItemScoped
{
    public AccessKind Access => AccessKind.Read;
}

public sealed record ListEnvelopesQuery(VisibilityScope Scope, bool IncludeClosed) : IQuery<EnvelopeList>;

[RequestHandler<CreateEnvelopeCommand, ItemId>]
public sealed class CreateEnvelopeHandler(
    IItemRegistry items, IEnvelopeStore envelopes, IValuations valuations, IFlows flows, IUnitOfWork unitOfWork)
    : IRequestHandler<CreateEnvelopeCommand, ItemId>
{
    public async ValueTask<Result<ItemId>> HandleAsync(CreateEnvelopeCommand command, CancellationToken ct = default)
    {
        var kind = EnvelopeRules.Kind(command.Kind);
        var cadence = EnvelopeRules.Cadence(command.ValuationCadence);
        if (kind.TryGetFailure(out var failure) || cadence.TryGetFailure(out failure)
            || EnvelopeRules.Name(command.DisplayName).TryGetFailure(out failure)
            || EnvelopeRules.Check(command.Institution, command.AccountHint, command.StaleAfterDays, command.Note).TryGetFailure(out failure))
            return Result.Failure<ItemId>(failure!);
        kind.TryGetValue(out var k);
        if (EnvelopeRules.PremiumsBefore2017(command.PremiumsBefore2017, k).TryGetFailure(out failure))
            return Result.Failure<ItemId>(failure!);
        if (EnvelopeRules.Regulated(command.Regulated, k).TryGetFailure(out failure))
            return Result.Failure<ItemId>(failure!);
        cadence.TryGetValue(out var c);

        await using var transaction = await unitOfWork.BeginAsync(ct);
        var created = await items.CreateAsync(new NewItem("investments", "envelope", command.DisplayName, command.Visibility,
            command.Currency.Code, command.Owners, command.ShareLevel), ct);
        if (!created.TryGetValue(out var id))
            return created;
        await envelopes.CreateAsync(new Envelope(id, k, command.Institution, command.AccountHint, command.OpenedOn, c,
            command.StaleAfterDays, command.Note, PremiumsBefore2017: command.PremiumsBefore2017, Regulated: EnvelopeRules.ParseRegulated(command.Regulated)), ct);

        if (command.Opening is { } opening)
        {
            var recorded = await flows.RecordAsync(new NewFlow(id, opening.OccurredOn, FlowKind.Opening, opening.Amount, null, null, null), ct);
            if (recorded.TryGetFailure(out failure))
                return Result.Failure<ItemId>(failure!); // disposed without commit: nothing is left
        }

        if (command.FirstValuation is { } first)
        {
            var recorded = await valuations.RecordAsync(new NewValuation(id, first.AsOf, first.Amount, null), ct);
            if (recorded.TryGetFailure(out failure))
                return Result.Failure<ItemId>(failure!);
        }

        await transaction.CommitAsync(ct);
        return Result.Success(id);
    }
}

[RequestHandler<EditEnvelopeCommand, ItemId>]
public sealed class EditEnvelopeHandler(
    IItemRegistry items, IEnvelopeStore envelopes, IUnitOfWork unitOfWork, IItemRules rules, IActorAccessor actor, IPersonRegistry persons)
    : IRequestHandler<EditEnvelopeCommand, ItemId>
{
    public async ValueTask<Result<ItemId>> HandleAsync(EditEnvelopeCommand command, CancellationToken ct = default)
    {
        var loaded = await EnvelopeCheck.LoadAsync(envelopes, items, command.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<ItemId>(failure!);
        loaded.TryGetValue(out var envelope);
        if (EnvelopeCheck.RequireOpen(envelope.Item).TryGetFailure(out failure))
            return Result.Failure<ItemId>(failure!);
        if (command.DisplayName is not null && EnvelopeRules.Name(command.DisplayName).TryGetFailure(out failure))
            return Result.Failure<ItemId>(failure!);

        EnvelopeKind? kind = null;
        if (command.Kind is not null)
        {
            var parsed = EnvelopeRules.Kind(command.Kind);
            if (!parsed.TryGetValue(out var k))
                return Result.Failure<ItemId>(parsed.TryGetFailure(out failure) ? failure! : throw new InvalidOperationException());
            kind = k;
        }

        ValuationCadence? cadence = null;
        if (command.ValuationCadence is not null)
        {
            var parsed = EnvelopeRules.Cadence(command.ValuationCadence);
            if (!parsed.TryGetValue(out var c))
                return Result.Failure<ItemId>(parsed.TryGetFailure(out failure) ? failure! : throw new InvalidOperationException());
            cadence = c;
        }

        var change = command.Change with { Kind = kind, Cadence = cadence };
        // Off a life insurance or a tontine, premiums before 2017 mean nothing: the ones kept are dropped, the ones sent still refused.
        if (kind is { } changed && changed is not (EnvelopeKind.LifeInsurance or EnvelopeKind.Tontine) && change.PremiumsBefore2017 is null)
            change = change with { ClearPremiumsBefore2017 = true };
        // Issue #399: likewise, off a savings envelope its regulated livret goes.
        var finalKind = kind ?? envelope.Envelope.Kind;
        if (command.Regulated is not null)
        {
            if (EnvelopeRules.Regulated(command.Regulated, finalKind, command.Change.ClearRegulated).TryGetFailure(out failure))
                return Result.Failure<ItemId>(failure!);
            change = change with { Regulated = EnvelopeRules.ParseRegulated(command.Regulated) };
        }
        else if (finalKind != EnvelopeKind.Savings)
            change = change with { ClearRegulated = true };
        if (EnvelopeRules.Check(change.Institution, change.AccountHint, change.StaleAfterDays, change.Note).TryGetFailure(out failure)
            || EnvelopeRules.UcShareFloor(change.UcShareFloorPct).TryGetFailure(out failure)
            || EnvelopeRules.PremiumsBefore2017(
                change.ClearPremiumsBefore2017 ? null : change.PremiumsBefore2017 ?? envelope.Envelope.PremiumsBefore2017,
                change.Kind ?? envelope.Envelope.Kind).TryGetFailure(out failure))
            return Result.Failure<ItemId>(failure!);

        var item = envelope.Item;
        var target = OwnershipEdit.Of(item, command.Visibility, command.ShareLevel, command.Owners);
        if (await OwnershipEdit.RefusalAsync(rules, persons, actor.Current!.PersonId!.Value, item, target, "envelope", ct) is { } refused)
            return Result.Failure<ItemId>(refused);

        await using var transaction = await unitOfWork.BeginAsync(ct);
        var replaced = await OwnershipEdit.ReplaceAsync(items, item, command.DisplayName ?? item.DisplayName, target, ct);
        if (replaced.TryGetFailure(out failure))
            return Result.Failure<ItemId>(failure!);

        await envelopes.UpdateAsync(command.ItemId, change, ct);
        await transaction.CommitAsync(ct);
        return Result.Success(command.ItemId);
    }
}

[RequestHandler<GetEnvelopeQuery, EnvelopeDetail>]
public sealed class GetEnvelopeHandler(
    EnvelopePerformance performance, IHouseholdCalendar calendar, IAccessPolicy policy, IActorAccessor actor, IPersonRegistry persons, IInflation inflation,
    IItemRules rules)
    : IRequestHandler<GetEnvelopeQuery, EnvelopeDetail>
{
    public async ValueTask<Result<EnvelopeDetail>> HandleAsync(GetEnvelopeQuery query, CancellationToken ct = default)
    {
        var facts = await performance.FactsAsync(query.ItemId, ct);
        if (facts.TryGetFailure(out var failure))
            return Result.Failure<EnvelopeDetail>(failure!);
        facts.TryGetValue(out var f);
        var today = await calendar.TodayAsync(ct);
        var figures = Nid.Investments.Domain.EnvelopeMath.At(f!.Values, f.Flows, f.Currency, f.Envelope.Cadence, f.Envelope.StaleAfterDays, today);
        // Minor 2: a closed envelope reports no staleness — EnvelopeMath stays pure; "closed" is known here.
        if (f.Item.ArchivedAt is not null)
            figures = figures with { StaleDays = null };
        var me = actor.Current!.PersonId!.Value;
        var canWrite = (await policy.CheckAsync(me, query.ItemId, AccessKind.Write, ct)).IsSuccess;
        var e = f.Envelope;
        var open = f.Item.ArchivedAt is null;
        var latest = Nid.Investments.Domain.EnvelopeMath.LastOnOrBefore(f.Values, today);
        var real = latest is null ? null
            : (await inflation.ReadAsync(ct)).RealAnnualReturn(Nid.Investments.Domain.EnvelopeMath.CashFlows(f.Flows, latest), latest.AsOf, f.Currency);

        return Result.Success(new EnvelopeDetail(
            query.ItemId, f.Item.DisplayName, EnvelopeWire.Of(e.Kind), f.Currency.Code,
            f.Item.Visibility == Visibility.Private ? "private" : "shared", f.Item.ArchivedAt is not null, canWrite,
            e.Institution, e.AccountHint, e.OpenedOn, EnvelopeWire.Of(e.Cadence), e.StaleAfterDays, e.Note, e.UcShareFloorPct,
            e.PremiumsBefore2017 is { } premiums && Money.Create(premiums, f.Currency).TryGetValue(out var money) ? AmountView.From(money) : null,
            FiguresView.From(figures),
            f.Values.OrderBy(v => v.AsOf).Select(v => new ValuationView(v.Id, v.AsOf, AmountView.From(v.Value), v.Note, v.DocumentId)).ToList(),
            f.Flows.Select(x => new FlowView(x.Id, x.OccurredOn, EnvelopeWire.FlowWire(x.Kind), AmountView.From(x.Amount), x.Category, x.ExternalRef, x.Note, x.DocumentId)).ToList(),
            f.Item.OwnerViews(), f.Item.Visibility == Visibility.Shared && f.Item.ShareLevel == ShareLevel.Edit ? "edit" : "view",
            canWrite && rules.CanCreate(me, f.Item.Owners),
            e.Regulated is { } regulated ? EnvelopeWire.Of(regulated) : null,
            open ? EnvelopeInsights.Ceiling(f, today) : null,
            open ? EnvelopeInsights.Interest(f, today) : null,
            open && EnvelopeInsights.Milestone(f, await persons.ListAsync(ct), today) is { } m ? new TaxMilestoneView(m.Code, m.On) : null,
            real));
    }
}

[RequestHandler<ListEnvelopesQuery, EnvelopeList>]
public sealed class ListEnvelopesHandler(
    IVisibleItems visible, IEnvelopeStore envelopes, EnvelopePerformance performance, IHouseholdCalendar calendar, IAccessPolicy policy, IActorAccessor actor,
    IItemRegistry items)
    : IRequestHandler<ListEnvelopesQuery, EnvelopeList>
{
    public async ValueTask<Result<EnvelopeList>> HandleAsync(ListEnvelopesQuery query, CancellationToken ct = default)
    {
        // Controller ruling (Task 4): the 3-argument overload of IVisibleItems.ForAsync returns ACTIVE items
        // only, so a request that wants closed envelopes must ask includeArchived: true explicitly — otherwise
        // a closed envelope could never be listed, even with IncludeClosed set.
        var reader = actor.Current!.PersonId!.Value;
        var ids = await visible.ForAsync(reader, query.Scope, includeArchived: query.IncludeClosed, ct);
        var found = await envelopes.FindManyAsync(ids.ToList(), ct);
        // Batched reads spec §3: the items, facts and write rights read once for all; a block that cannot be read falls back
        // to each envelope's own read, which leaves out only the envelopes whose facts cannot be read.
        var accesses = await items.FindManyAsync(found.Select(e => e.ItemId).ToList(), ct);
        var block = await performance.FactsManyAsync(found, accesses, ct);
        var writable = await policy.CheckManyAsync(reader, accesses.Keys.ToList(), AccessKind.Write, ct);
        var today = await calendar.TodayAsync(ct);
        var summaries = new List<EnvelopeSummary>();
        foreach (var envelope in found)
        {
            EnvelopePerformance.Facts? f;
            if (block is not null)
                block.TryGetValue(envelope.ItemId, out f);
            else
                (await performance.FactsAsync(envelope.ItemId, ct)).TryGetValue(out f);
            if (f is null || (!query.IncludeClosed && f.Item.ArchivedAt is not null))
                continue;
            var figures = Nid.Investments.Domain.EnvelopeMath.At(f!.Values, f.Flows, f.Currency, f.Envelope.Cadence, f.Envelope.StaleAfterDays, today);
            // Minor 2: a closed envelope reports no staleness — EnvelopeMath stays pure; "closed" is known here.
            if (f.Item.ArchivedAt is not null)
                figures = figures with { StaleDays = null };
            var canWrite = writable.Contains(envelope.ItemId);
            summaries.Add(new EnvelopeSummary(envelope.ItemId, f.Item.DisplayName, EnvelopeWire.Of(envelope.Kind), f.Currency.Code,
                f.Item.Visibility == Visibility.Private ? "private" : "shared", f.Item.ArchivedAt is not null, canWrite, FiguresView.From(figures), f.Item.OwnerViews()));
        }

        // Minor 3: a stable order — the name alone ties on a duplicate, or across cultures/collations.
        return Result.Success(new EnvelopeList(summaries
            .OrderBy(s => s.DisplayName, StringComparer.Create(new System.Globalization.CultureInfo("fr-FR"), true))
            .ThenBy(s => s.Id.Value)
            .ToList()));
    }
}
