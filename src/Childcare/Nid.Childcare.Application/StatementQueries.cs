using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
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

/// <summary>The final settlement of an ended contract; not found while the contract has not been ended with its reason.</summary>
public sealed record GetSettlementQuery(ItemId ItemId) : IQuery<ChildcareSettlementView>, IItemScoped
{
    public AccessKind Access => AccessKind.Read;
}

[RequestHandler<GetSettlementQuery, ChildcareSettlementView>]
public sealed class GetSettlementHandler(StatementAssembly assembly, IPersonRegistry persons, IHouseholdSettings household)
    : IRequestHandler<GetSettlementQuery, ChildcareSettlementView>
{
    public async ValueTask<Result<ChildcareSettlementView>> HandleAsync(GetSettlementQuery query, CancellationToken ct = default)
    {
        var loaded = await assembly.ArrangementAsync(query.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<ChildcareSettlementView>(failure!);
        loaded.TryGetValue(out var found);
        if (found.Arrangement.End is not { } end || found.Revisions[^1].To is not { } last)
            return Result.Failure<ChildcareSettlementView>(new NotFoundFailure("childcare.settlement", query.ItemId.Value.ToString()));
        var context = await assembly.ContextAsync(query.ItemId, last.ToYearMonth(), ct);
        if (context.TryGetFailure(out failure))
            return Result.Failure<ChildcareSettlementView>(failure!);
        context.TryGetValue(out var month);
        var stored = month!.StoredMonth;
        var evaluation = stored is { Validated: true, Figures: { } frozen } ? new MonthEvaluation(frozen, [], []) : await assembly.EvaluateAsync(month,
            (await assembly.DaysAsync(month, ct)).Days, [], stored?.Mileage ?? 0m, stored?.Cmg, stored?.Declared, stored?.Contributions ?? 0m, stored?.Exemption, ct);

        var currency = await household.ReferenceCurrencyAsync(ct);
        ChildcareMoneyView Money(decimal amount) => ChildcareMoneyView.Of(amount, currency);
        SettlementLinesView? lines = null;
        if (evaluation.Figures is { } f)
        {
            var d = f.Declaration;
            lines = new SettlementLinesView(Money(d.NetPay - f.Regularisation), Money(f.Regularisation), d.LeaveIndemnity == 0m ? null : d.PaidLeaveDays,
                Money(d.LeaveIndemnity), Money(d.RuptureIndemnity), Money(d.Upkeep), Money(d.Meals), Money(d.Mileage),
                Money(d.NetPay + d.LeaveIndemnity + d.RuptureIndemnity + d.Upkeep + d.Meals + d.Mileage));
        }
        var child = (await persons.ListAsync(ct)).FirstOrDefault(p => p.Id == found.Arrangement.Child)?.DisplayName ?? "";
        return Result.Success(new ChildcareSettlementView(
            query.ItemId.Value, found.Item.DisplayName, child, found.Arrangement.CarerName, found.Revisions[0].From, last, ContractEndView.Of(end, found.Revisions),
            StatementAssembly.Format(month.Month), stored is { Validated: true }, lines,
            evaluation.Problems.Select(p => new FieldProblemView(p.Field, p.Code)).ToList(), evaluation.Missing.Select(StatementAssembly.Format).ToList()));
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
            ChildcareYear.NotValidated(start, end, today, stored, year).Select(StatementAssembly.Format).ToList(), Money(totals.Contributions), Money(totals.Exemption)));
    }
}
