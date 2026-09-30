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
    OpeningDraft? Opening, FirstValuationDraft? FirstValuation) : ICommand<ItemId>, ICreatesItem;

public sealed record EditEnvelopeCommand(ItemId ItemId, string? DisplayName, string? Kind, EnvelopeChange Change, string? ValuationCadence)
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
        cadence.TryGetValue(out var c);

        await using var transaction = await unitOfWork.BeginAsync(ct);
        var created = await items.CreateAsync(new NewItem("investments", "envelope", command.DisplayName, command.Visibility,
            command.Currency.Code, command.Owners), ct);
        if (!created.TryGetValue(out var id))
            return created;
        await envelopes.CreateAsync(new Envelope(id, k, command.Institution, command.AccountHint, command.OpenedOn, c,
            command.StaleAfterDays, command.Note), ct);

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
public sealed class EditEnvelopeHandler(IItemRegistry items, IEnvelopeStore envelopes, IUnitOfWork unitOfWork)
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
        if (EnvelopeRules.Check(change.Institution, change.AccountHint, change.StaleAfterDays, change.Note).TryGetFailure(out failure))
            return Result.Failure<ItemId>(failure!);

        await using var transaction = await unitOfWork.BeginAsync(ct);
        if (command.DisplayName is { } name && name != envelope.Item.DisplayName)
        {
            var renamed = await items.ReplaceAsync(command.ItemId, name, envelope.Item.Visibility,
                envelope.Item.Owners.Select(o => new OwnerShare(o.PersonId, o.ShareBp)).ToList(), ct);
            if (renamed.TryGetFailure(out failure))
                return Result.Failure<ItemId>(failure!);
        }

        await envelopes.UpdateAsync(command.ItemId, change, ct);
        await transaction.CommitAsync(ct);
        return Result.Success(command.ItemId);
    }
}

[RequestHandler<GetEnvelopeQuery, EnvelopeDetail>]
public sealed class GetEnvelopeHandler(
    EnvelopePerformance performance, IHouseholdCalendar calendar, IAccessPolicy policy, IActorAccessor actor)
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
        var canWrite = (await policy.CheckAsync(actor.Current!.PersonId!.Value, query.ItemId, AccessKind.Write, ct)).IsSuccess;
        var e = f.Envelope;

        return Result.Success(new EnvelopeDetail(
            query.ItemId, f.Item.DisplayName, EnvelopeWire.Of(e.Kind), f.Currency.Code,
            f.Item.Visibility == Visibility.Private ? "private" : "shared", f.Item.ArchivedAt is not null, canWrite,
            e.Institution, e.AccountHint, e.OpenedOn, EnvelopeWire.Of(e.Cadence), e.StaleAfterDays, e.Note,
            FiguresView.From(figures),
            f.Values.OrderBy(v => v.AsOf).Select(v => new ValuationView(v.Id, v.AsOf, AmountView.From(v.Value), v.Note)).ToList(),
            f.Flows.Select(x => new FlowView(x.Id, x.OccurredOn, EnvelopeWire.FlowWire(x.Kind), AmountView.From(x.Amount), x.Category, x.ExternalRef, x.Note)).ToList()));
    }
}

[RequestHandler<ListEnvelopesQuery, EnvelopeList>]
public sealed class ListEnvelopesHandler(
    IVisibleItems visible, IEnvelopeStore envelopes, EnvelopePerformance performance, IHouseholdCalendar calendar, IAccessPolicy policy, IActorAccessor actor)
    : IRequestHandler<ListEnvelopesQuery, EnvelopeList>
{
    public async ValueTask<Result<EnvelopeList>> HandleAsync(ListEnvelopesQuery query, CancellationToken ct = default)
    {
        // Controller ruling (Task 4): the 3-argument overload of IVisibleItems.ForAsync returns ACTIVE items
        // only, so a request that wants closed envelopes must ask includeArchived: true explicitly — otherwise
        // a closed envelope could never be listed, even with IncludeClosed set.
        var ids = await visible.ForAsync(actor.Current!.PersonId!.Value, query.Scope, includeArchived: query.IncludeClosed, ct);
        var found = await envelopes.FindManyAsync(ids.ToList(), ct);
        var today = await calendar.TodayAsync(ct);
        var summaries = new List<EnvelopeSummary>();
        foreach (var envelope in found)
        {
            var facts = await performance.FactsAsync(envelope.ItemId, ct);
            if (!facts.TryGetValue(out var f) || (!query.IncludeClosed && f!.Item.ArchivedAt is not null))
                continue;
            var figures = Nid.Investments.Domain.EnvelopeMath.At(f!.Values, f.Flows, f.Currency, f.Envelope.Cadence, f.Envelope.StaleAfterDays, today);
            // Minor 2: a closed envelope reports no staleness — EnvelopeMath stays pure; "closed" is known here.
            if (f.Item.ArchivedAt is not null)
                figures = figures with { StaleDays = null };
            var canWrite = (await policy.CheckAsync(actor.Current!.PersonId!.Value, envelope.ItemId, AccessKind.Write, ct)).IsSuccess;
            summaries.Add(new EnvelopeSummary(envelope.ItemId, f.Item.DisplayName, EnvelopeWire.Of(envelope.Kind), f.Currency.Code,
                f.Item.Visibility == Visibility.Private ? "private" : "shared", f.Item.ArchivedAt is not null, canWrite, FiguresView.From(figures)));
        }

        // Minor 3: a stable order — the name alone ties on a duplicate, or across cultures/collations.
        return Result.Success(new EnvelopeList(summaries
            .OrderBy(s => s.DisplayName, StringComparer.Create(new System.Globalization.CultureInfo("fr-FR"), true))
            .ThenBy(s => s.Id.Value)
            .ToList()));
    }
}
