using Nid.Childcare.Contracts;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Childcare.Application;

public sealed record CreateNannyCommand(string Name, Visibility Visibility, IReadOnlyList<OwnerShare> Owners, ShareLevel ShareLevel = ShareLevel.View)
    : ICommand<ItemId>, ICreatesItem, ITransactional;

public sealed record RenameNannyCommand(ItemId ItemId, string Name) : ICommand<ItemId>, IItemScoped, ITransactional
{
    public AccessKind Access => AccessKind.Write;
}

/// <summary>Records a month, or corrects the one recorded.</summary>
public sealed record PutNannyMonthCommand(ItemId ItemId, YearMonth Month, decimal Paid, decimal Cmg, decimal? Hours) : ICommand<ItemId>, IItemScoped, ITransactional
{
    public AccessKind Access => AccessKind.Write;
}

public sealed record DeleteNannyMonthCommand(ItemId ItemId, YearMonth Month) : ICommand<ItemId>, IItemScoped, ITransactional
{
    public AccessKind Access => AccessKind.Write;
}

/// <summary>The nanny no longer works for the household: archived, no longer projected to December.</summary>
public sealed record CloseNannyCommand(ItemId ItemId) : ICommand<ItemId>, IItemScoped, ITransactional
{
    public AccessKind Access => AccessKind.Write;
}

public sealed record ReopenNannyCommand(ItemId ItemId) : ICommand<ItemId>, IItemScoped, ITransactional
{
    public AccessKind Access => AccessKind.Write;
}

public sealed record DeleteNannyCommand(ItemId ItemId, uint Version) : ICommand<ItemId>, IItemScoped, IConfirmedInBrowser
{
    public AccessKind Access => AccessKind.Write;
}

public sealed record NannyDeletionQuery(ItemId ItemId) : IQuery<ItemDeletionPreview>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

public sealed record ListNanniesQuery(VisibilityScope Scope, bool Closed) : IQuery<NannyList>;

/// <summary>A nanny's months of <paramref name="Year"/>, the current year when null.</summary>
public sealed record GetNannyQuery(ItemId ItemId, int? Year) : IQuery<NannyDetail>, IItemScoped
{
    public AccessKind Access => AccessKind.Read;
}

internal static class NannyCheck
{
    public const decimal Max = 1_000_000m;

    public static async Task<Result<ItemAccess>> LoadAsync(INannyStore store, IItemRegistry items, ItemId id, CancellationToken ct)
        => (await store.FindManyAsync([id], ct)).Count == 1 && await items.FindAsync(id, ct) is { } item
            ? Result.Success(item)
            : Result.Failure<ItemAccess>(new NotFoundFailure("nanny", id.ToString()));

    public static async Task<Result<ItemAccess>> LoadOpenAsync(INannyStore store, IItemRegistry items, ItemId id, CancellationToken ct)
    {
        var loaded = await LoadAsync(store, items, id, ct);
        return loaded.TryGetValue(out var item) && item.ArchivedAt is not null
            ? Result.Failure<ItemAccess>(new ConflictFailure("This nanny is closed: reopen it first.") { Code = ChildcareCodes.Closed })
            : loaded;
    }

    public static List<FieldError> Month(YearMonth month, decimal paid, decimal cmg, decimal? hours, LocalDate today)
    {
        var errors = new List<FieldError>();
        if (month > today.ToYearMonth()) errors.Add(new("month", ChildcareCodes.NannyMonthInFuture, "A month is recorded once begun."));
        if (paid is <= 0 or > Max) errors.Add(new("paid", FieldErrors.ShapeCode, "The amount paid is above 0 and at most 1,000,000."));
        if (cmg < 0) errors.Add(new("cmg", FieldErrors.ShapeCode, "The CMG is 0 or more."));
        else if (cmg > paid) errors.Add(new("cmg", ChildcareCodes.CmgAbovePaid, "The CMG is at most the amount paid."));
        if (hours is <= 0 or > 744) errors.Add(new("hours", FieldErrors.ShapeCode, "The hours are above 0 and at most 744."));
        return errors;
    }
}

[RequestHandler<CreateNannyCommand, ItemId>]
public sealed class CreateNannyHandler(IItemRegistry items, INannyStore store) : IRequestHandler<CreateNannyCommand, ItemId>
{
    public async ValueTask<Result<ItemId>> HandleAsync(CreateNannyCommand command, CancellationToken ct = default)
    {
        if (ChildcareCheck.Fail(ChildcareCheck.Name(command.Name)).TryGetFailure(out var failure))
            return Result.Failure<ItemId>(failure!);
        var created = await items.CreateAsync(new NewItem(ChildcareModule.Key, ChildcareKinds.Nanny, command.Name.Trim(), command.Visibility, null, command.Owners, command.ShareLevel), ct);
        if (!created.TryGetValue(out var id))
            return created; // a failure rolls the transaction back: nothing is left
        await store.CreateAsync(id, ct);
        return Result.Success(id);
    }
}

[RequestHandler<RenameNannyCommand, ItemId>]
public sealed class RenameNannyHandler(IItemRegistry items, INannyStore store) : IRequestHandler<RenameNannyCommand, ItemId>
{
    public async ValueTask<Result<ItemId>> HandleAsync(RenameNannyCommand command, CancellationToken ct = default)
    {
        var loaded = await NannyCheck.LoadOpenAsync(store, items, command.ItemId, ct);
        if (!loaded.TryGetValue(out var item))
            return loaded.Map(_ => command.ItemId);
        if (ChildcareCheck.Fail(ChildcareCheck.Name(command.Name)).TryGetFailure(out var failure))
            return Result.Failure<ItemId>(failure!);
        if (command.Name.Trim() == item.DisplayName)
            return Result.Success(command.ItemId);
        return (await items.ReplaceAsync(command.ItemId, command.Name.Trim(), item.Visibility, item.Owners.Select(o => new OwnerShare(o.PersonId, o.ShareBp)).ToList(), ct))
            .Map(() => command.ItemId);
    }
}

[RequestHandler<PutNannyMonthCommand, ItemId>]
public sealed class PutNannyMonthHandler(IItemRegistry items, INannyStore store, IHouseholdCalendar calendar) : IRequestHandler<PutNannyMonthCommand, ItemId>
{
    public async ValueTask<Result<ItemId>> HandleAsync(PutNannyMonthCommand command, CancellationToken ct = default)
    {
        var loaded = await NannyCheck.LoadOpenAsync(store, items, command.ItemId, ct);
        if (!loaded.TryGetValue(out _))
            return loaded.Map(_ => command.ItemId);
        var errors = NannyCheck.Month(command.Month, command.Paid, command.Cmg, command.Hours, await calendar.TodayAsync(ct));
        if (ChildcareCheck.Fail(errors).TryGetFailure(out var failure))
            return Result.Failure<ItemId>(failure!);
        await store.PutMonthAsync(new NannyMonth(command.ItemId, command.Month, command.Paid, command.Cmg, command.Hours), ct);
        return Result.Success(command.ItemId);
    }
}

[RequestHandler<DeleteNannyMonthCommand, ItemId>]
public sealed class DeleteNannyMonthHandler(IItemRegistry items, INannyStore store) : IRequestHandler<DeleteNannyMonthCommand, ItemId>
{
    public async ValueTask<Result<ItemId>> HandleAsync(DeleteNannyMonthCommand command, CancellationToken ct = default)
    {
        var loaded = await NannyCheck.LoadOpenAsync(store, items, command.ItemId, ct);
        if (!loaded.TryGetValue(out _))
            return loaded.Map(_ => command.ItemId);
        return await store.DeleteMonthAsync(command.ItemId, command.Month, ct)
            ? Result.Success(command.ItemId)
            : Result.Failure<ItemId>(new NotFoundFailure("nanny month", NannyViews.Format(command.Month)));
    }
}

[RequestHandler<CloseNannyCommand, ItemId>]
public sealed class CloseNannyHandler(IItemRegistry items, INannyStore store, IClock clock) : IRequestHandler<CloseNannyCommand, ItemId>
{
    public async ValueTask<Result<ItemId>> HandleAsync(CloseNannyCommand command, CancellationToken ct = default)
    {
        var loaded = await NannyCheck.LoadOpenAsync(store, items, command.ItemId, ct);
        if (!loaded.TryGetValue(out _))
            return loaded.Map(_ => command.ItemId);
        return (await items.ArchiveAsync(command.ItemId, clock.GetCurrentInstant(), ct)).Map(() => command.ItemId);
    }
}

[RequestHandler<ReopenNannyCommand, ItemId>]
public sealed class ReopenNannyHandler(IItemRegistry items, INannyStore store) : IRequestHandler<ReopenNannyCommand, ItemId>
{
    public async ValueTask<Result<ItemId>> HandleAsync(ReopenNannyCommand command, CancellationToken ct = default)
    {
        var loaded = await NannyCheck.LoadAsync(store, items, command.ItemId, ct);
        if (!loaded.TryGetValue(out var item))
            return loaded.Map(_ => command.ItemId);
        if (item.ArchivedAt is null)
            return Result.Failure<ItemId>(new ConflictFailure("This nanny is not closed.") { Code = ChildcareCodes.Open });
        return (await items.UnarchiveAsync(command.ItemId, ct)).Map(() => command.ItemId);
    }
}

[RequestHandler<DeleteNannyCommand, ItemId>]
public sealed class DeleteNannyHandler(IItemDeletions deletions) : IRequestHandler<DeleteNannyCommand, ItemId>
{
    public async ValueTask<Result<ItemId>> HandleAsync(DeleteNannyCommand command, CancellationToken ct = default)
        => (await deletions.DeleteAsync(command.ItemId, ChildcareModule.Key, ChildcareKinds.Nanny, command.Version, ct)).Map(() => command.ItemId);
}

[RequestHandler<NannyDeletionQuery, ItemDeletionPreview>]
public sealed class NannyDeletionHandler(IItemDeletions deletions) : IRequestHandler<NannyDeletionQuery, ItemDeletionPreview>
{
    public async ValueTask<Result<ItemDeletionPreview>> HandleAsync(NannyDeletionQuery query, CancellationToken ct = default)
        => await deletions.PreviewAsync(query.ItemId, ChildcareModule.Key, ChildcareKinds.Nanny, ct);
}

[RequestHandler<ListNanniesQuery, NannyList>]
public sealed class ListNanniesHandler(
    IVisibleItems visible, IItemRegistry items, INannyStore store, IAccessPolicy policy, IHouseholdSettings household, IHouseholdCalendar calendar, IActorAccessor actor)
    : IRequestHandler<ListNanniesQuery, NannyList>
{
    public async ValueTask<Result<NannyList>> HandleAsync(ListNanniesQuery query, CancellationToken ct = default)
    {
        var person = actor.Current!.PersonId!.Value;
        var ids = await store.FindManyAsync((await visible.ForAsync(person, query.Scope, includeArchived: query.Closed, ct)).ToList(), ct);
        var registered = await items.FindManyAsync(ids, ct);
        var writable = await policy.CheckManyAsync(person, ids, AccessKind.Write, ct);
        var months = (await store.MonthsOfAsync(ids, ct)).ToLookup(m => m.ItemId);
        var (today, currency) = (await calendar.TodayAsync(ct), await household.ReferenceCurrencyAsync(ct));
        var summaries = registered.Values.Select(item => NannyViews.Summary(item, writable.Contains(item.Id), months[item.Id].ToList(), today, currency));
        return Result.Success(new NannyList(summaries.OrderBy(s => s.Closed).ThenBy(s => s.Name, StringComparer.Create(new System.Globalization.CultureInfo("fr-FR"), true)).ToList()));
    }
}

[RequestHandler<GetNannyQuery, NannyDetail>]
public sealed class GetNannyHandler(
    IItemRegistry items, INannyStore store, IAccessPolicy policy, IHouseholdSettings household, IHouseholdCalendar calendar, IActorAccessor actor)
    : IRequestHandler<GetNannyQuery, NannyDetail>
{
    public async ValueTask<Result<NannyDetail>> HandleAsync(GetNannyQuery query, CancellationToken ct = default)
    {
        var loaded = await NannyCheck.LoadAsync(store, items, query.ItemId, ct);
        if (loaded.TryGetFailure(out var failure) || !loaded.TryGetValue(out var item))
            return Result.Failure<NannyDetail>(failure!);
        var writable = (await policy.CheckManyAsync(actor.Current!.PersonId!.Value, [item.Id], AccessKind.Write, ct)).Contains(item.Id);
        var months = await store.MonthsOfAsync([item.Id], ct);
        var (today, currency) = (await calendar.TodayAsync(ct), await household.ReferenceCurrencyAsync(ct));
        return Result.Success(NannyViews.Detail(NannyViews.Summary(item, writable, months, today, currency), query.Year ?? today.Year, months, today, currency));
    }
}
