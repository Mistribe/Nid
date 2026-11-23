using Nid.Core.Contracts;
using Nid.Tax.Contracts;
using Nid.Tax.Domain;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Tax.Application;

/// <summary>Sale tax spec §6: with <c>ItemId</c>, that item's page among everything the reader sees; without, the scope's total.</summary>
public sealed record GetSaleTaxQuery(VisibilityScope Scope, ItemId? ItemId) : IQuery<SaleTaxView>, IOptionalItemScoped
{
    public AccessKind Access => AccessKind.Read;
}

[RequestHandler<GetSaleTaxQuery, SaleTaxView>]
public sealed class GetSaleTaxHandler(SaleTaxEstimates estimates, IHouseholdCalendar calendar, IActorAccessor actor)
    : IRequestHandler<GetSaleTaxQuery, SaleTaxView>
{
    public async ValueTask<Result<SaleTaxView>> HandleAsync(GetSaleTaxQuery query, CancellationToken ct = default)
        => await estimates.ComputeAsync(actor.Current!.PersonId!.Value, query.Scope, query.ItemId, await calendar.TodayAsync(ct), ct);
}
