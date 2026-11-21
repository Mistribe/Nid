using System.Globalization;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.RealEstate.Application;

/// <summary>Spec 3A2 §4: <c>scope</c> is the Foyer / Moi selector; ended loans only when asked for; #180: <c>Envelope</c>, the loans financing it.</summary>
public sealed record ListLoansQuery(VisibilityScope Scope, bool IncludeEnded, ItemId? Envelope = null) : IQuery<LoanList>;

public sealed record GetLoanQuery(ItemId ItemId) : IQuery<LoanDetail>, IItemScoped
{
    public AccessKind Access => AccessKind.Read;
}

public sealed record GetLoanScheduleQuery(ItemId ItemId) : IQuery<LoanScheduleView>, IItemScoped
{
    public AccessKind Access => AccessKind.Read;
}

/// <summary>Spec 3A2 §4: stores nothing and needs read access only; no <paramref name="Effect"/> compares both.</summary>
public sealed record SimulateLoanRepaymentQuery(ItemId ItemId, LocalDate On, decimal Amount, RepaymentEffect? Effect, decimal Penalty)
    : IQuery<LoanSimulationView>, IItemScoped
{
    public AccessKind Access => AccessKind.Read;
}

[RequestHandler<ListLoansQuery, LoanList>]
public sealed class ListLoansHandler(
    IVisibleItems visible, IItemRegistry items, ILoanStore loans, IHouseholdCalendar calendar, IAccessPolicy policy, IActorAccessor actor)
    : IRequestHandler<ListLoansQuery, LoanList>
{
    private static readonly StringComparer French = StringComparer.Create(new CultureInfo("fr-FR"), true);

    public async ValueTask<Result<LoanList>> HandleAsync(ListLoansQuery query, CancellationToken ct = default)
    {
        var person = actor.Current!.PersonId!.Value;
        IEnumerable<ItemId> ids = await visible.ForAsync(person, query.Scope, ct);
        if (query.Envelope is { } envelope)
            ids = ids.Intersect(await loans.FinancingAsync(envelope, ct));
        var today = await calendar.TodayAsync(ct);

        // Batched reads spec §3: the items, the write checks and the linked properties' names, each read once for the list.
        var found = await loans.FindManyAsync(ids.ToList(), ct);
        var loanIds = found.Select(l => l.ItemId).ToList();
        var accesses = await items.FindManyAsync(loanIds, ct);
        var writable = await policy.CheckManyAsync(person, loanIds, AccessKind.Write, ct);
        // As LoanCheck.PropertyNameAsync: a linked property's name only when the reader can read it.
        var linkedIds = found.Select(l => l.PropertyId).Concat(found.Select(l => l.EnvelopeId)).OfType<ItemId>().Distinct().ToList();
        var readable = await policy.CheckManyAsync(person, linkedIds, AccessKind.Read, ct);
        var properties = await items.FindManyAsync(readable.ToList(), ct);

        var rows = new List<LoanSummary>();
        foreach (var loan in found)
        {
            if (!accesses.TryGetValue(loan.ItemId, out var item))
                continue;
            var propertyName = loan.PropertyId is { } property && properties.TryGetValue(property, out var linked) ? linked.DisplayName : null;
            var envelopeName = loan.EnvelopeId is { } financed && properties.TryGetValue(financed, out var scpi) ? scpi.DisplayName : null;
            var row = LoanViews.Summary(item, loan, today, writable.Contains(loan.ItemId), propertyName, envelopeName);
            if (query.IncludeEnded || !row.Ended)
                rows.Add(row);
        }

        // A stable order: the name alone ties on a duplicate (as the income sources' list).
        return Result.Success(new LoanList(rows.OrderBy(r => r.Name, French).ThenBy(r => r.Id.Value).ToList()));
    }
}

[RequestHandler<GetLoanQuery, LoanDetail>]
public sealed class GetLoanHandler(ILoanStore loans, IItemRegistry items, IHouseholdCalendar calendar, IAccessPolicy policy, IActorAccessor actor)
    : IRequestHandler<GetLoanQuery, LoanDetail>
{
    public async ValueTask<Result<LoanDetail>> HandleAsync(GetLoanQuery query, CancellationToken ct = default)
    {
        var loaded = await LoanCheck.LoadAsync(loans, items, query.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<LoanDetail>(failure!);
        loaded.TryGetValue(out var found);
        var person = actor.Current!.PersonId!.Value;
        var canWrite = (await policy.CheckAsync(person, query.ItemId, AccessKind.Write, ct)).IsSuccess;
        var propertyName = await LoanCheck.PropertyNameAsync(policy, items, person, found.Loan.PropertyId, ct);
        var envelopeName = await LoanCheck.PropertyNameAsync(policy, items, person, found.Loan.EnvelopeId, ct);
        return Result.Success(LoanViews.Detail(found.Item, found.Loan, await calendar.TodayAsync(ct), canWrite, propertyName, envelopeName));
    }
}

[RequestHandler<GetLoanScheduleQuery, LoanScheduleView>]
public sealed class GetLoanScheduleHandler(ILoanStore loans, IItemRegistry items) : IRequestHandler<GetLoanScheduleQuery, LoanScheduleView>
{
    public async ValueTask<Result<LoanScheduleView>> HandleAsync(GetLoanScheduleQuery query, CancellationToken ct = default)
    {
        var loaded = await LoanCheck.LoadAsync(loans, items, query.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<LoanScheduleView>(failure!);
        loaded.TryGetValue(out var found);
        var terms = LoanReading.Terms(LoanViews.CurrencyOf(found.Item), found.Loan.Contract, found.Loan.Fees, found.Loan.Repayments);
        return Result.Success(LoanViews.ScheduleOf(terms, LoanReading.Schedule(terms, found.Loan)));
    }
}

[RequestHandler<SimulateLoanRepaymentQuery, LoanSimulationView>]
public sealed class SimulateLoanRepaymentHandler(ILoanStore loans, IItemRegistry items)
    : IRequestHandler<SimulateLoanRepaymentQuery, LoanSimulationView>
{
    public async ValueTask<Result<LoanSimulationView>> HandleAsync(SimulateLoanRepaymentQuery query, CancellationToken ct = default)
    {
        if ((LoanRules.Amount(query.Amount, "amount") ?? LoanRules.Amount(query.Penalty, "penalty")) is { } refused)
            return Result.Failure<LoanSimulationView>(refused);

        var loaded = await LoanCheck.LoadAsync(loans, items, query.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<LoanSimulationView>(failure!);
        loaded.TryGetValue(out var found);

        var terms = LoanReading.Terms(LoanViews.CurrencyOf(found.Item), found.Loan.Contract, found.Loan.Fees, found.Loan.Repayments);
        var simulated = LoanMath.SimulateEarlyRepayment(terms, query.On, query.Amount, query.Effect, query.Penalty);
        if (simulated.TryGetFailure(out failure))
            return Result.Failure<LoanSimulationView>(failure!); // the engine answers at on, amount, effect or penalty
        simulated.TryGetValue(out var simulation);
        return Result.Success(LoanViews.SimulationOf(simulation!));
    }
}
