using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Childcare.Application;

public sealed record ListStatementsQuery(ItemId ItemId) : IQuery<ChildcareStatementList>, IItemScoped
{
    public AccessKind Access => AccessKind.Read;
}

public sealed record GetStatementQuery(ItemId ItemId, YearMonth Month) : IQuery<ChildcareStatementView>, IItemScoped
{
    public AccessKind Access => AccessKind.Read;
}

[RequestHandler<ListStatementsQuery, ChildcareStatementList>]
public sealed class ListStatementsHandler(StatementAssembly assembly) : IRequestHandler<ListStatementsQuery, ChildcareStatementList>
{
    public async ValueTask<Result<ChildcareStatementList>> HandleAsync(ListStatementsQuery query, CancellationToken ct = default)
    {
        var loaded = await assembly.ArrangementAsync(query.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<ChildcareStatementList>(failure!);
        loaded.TryGetValue(out var found);
        return Result.Success(await assembly.ListAsync(query.ItemId, found.Revisions, found.Stored, found.Today, ct));
    }
}

[RequestHandler<GetStatementQuery, ChildcareStatementView>]
public sealed class GetStatementHandler(StatementAssembly assembly, IActorAccessor actor) : IRequestHandler<GetStatementQuery, ChildcareStatementView>
{
    public async ValueTask<Result<ChildcareStatementView>> HandleAsync(GetStatementQuery query, CancellationToken ct = default)
    {
        var context = await assembly.ContextAsync(query.ItemId, query.Month, ct);
        if (context.TryGetFailure(out var failure))
            return Result.Failure<ChildcareStatementView>(failure!);
        context.TryGetValue(out var found);
        return Result.Success(await assembly.ViewAsync(actor.Current!.PersonId!.Value, found!, ct));
    }
}

/// <summary>#411: the arrangement's year for the tax return; <paramref name="Year"/> null is the year just ended (<see cref="ChildcareYear.DefaultYear"/>).</summary>
public sealed record GetChildcareYearQuery(ItemId ItemId, int? Year) : IQuery<ChildcareYearView>, IItemScoped
{
    public AccessKind Access => AccessKind.Read;
}

[RequestHandler<GetChildcareYearQuery, ChildcareYearView>]
public sealed class GetChildcareYearHandler(
    StatementAssembly assembly, IVisibleItems visible, IChildcareStore store, IPersonRegistry persons, IHouseholdSettings household, IActorAccessor actor)
    : IRequestHandler<GetChildcareYearQuery, ChildcareYearView>
{
    public async ValueTask<Result<ChildcareYearView>> HandleAsync(GetChildcareYearQuery query, CancellationToken ct = default)
    {
        var loaded = await assembly.ArrangementAsync(query.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<ChildcareYearView>(failure!);
        loaded.TryGetValue(out var found);
        var (arrangement, item, revisions, stored, today) = found;
        var (start, end) = (revisions[0].From, revisions[^1].To);
        var years = ChildcareYear.Years(start, end, today);
        var year = query.Year ?? ChildcareYear.DefaultYear(years, today);
        var counted = ChildcareYear.Counted(stored, year);
        var totals = ChildcareYear.Totals(counted);

        // The box ranks the child among the children of every arrangement the reader sees with costs that year (closed ones too).
        var people = (await persons.ListAsync(ct)).ToDictionary(p => p.Id);
        var children = new List<PersonId> { arrangement.Child };
        var ids = (await visible.ForAsync(actor.Current!.PersonId!.Value, VisibilityScope.Me, includeArchived: true, ct)).Where(i => i != query.ItemId).ToList();
        foreach (var other in await store.FindManyAsync(ids, ct))
            if (ChildcareYear.Counted(await store.StatementsOfAsync(other.ItemId, ct), year).Count > 0)
                children.Add(other.Child);
        var box = ChildcareYear.Box(arrangement.Child,
            children.Select(c => (c, people.GetValueOrDefault(c)?.BirthDate, people.GetValueOrDefault(c)?.DisplayName ?? "")).ToList());

        var currency = await household.ReferenceCurrencyAsync(ct);
        ChildcareMoneyView Money(decimal amount) => ChildcareMoneyView.Of(amount, currency);
        return Result.Success(new ChildcareYearView(
            arrangement.ItemId.Value, item.DisplayName, people.GetValueOrDefault(arrangement.Child)?.DisplayName ?? "", arrangement.CarerName, year, years, box,
            Money(totals.NetPay), Money(totals.Cmg), Money(totals.Upkeep), Money(totals.Meals), Money(totals.TaxBase),
            counted.Select(s => StatementAssembly.Format(s.Month)).ToList(),
            ChildcareYear.NotValidated(start, end, today, stored, year).Select(StatementAssembly.Format).ToList()));
    }
}
