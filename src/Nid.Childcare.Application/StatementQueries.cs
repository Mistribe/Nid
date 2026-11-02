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
