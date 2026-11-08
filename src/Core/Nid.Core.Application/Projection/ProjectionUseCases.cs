using Nid.Core.Contracts;
using Nid.Core.Domain.Projection;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Core.Application.Projection;

/// <summary>Spec §7: the household's base projection in the reader's scope, from today to the horizon.</summary>
public sealed record GetProjectionQuery(VisibilityScope Scope, ProjectionHorizon Horizon) : IQuery<ProjectionView>;

[RequestHandler<GetProjectionQuery, ProjectionView>]
public sealed class GetProjectionHandler(ProjectionFactsReader facts) : IRequestHandler<GetProjectionQuery, ProjectionView>
{
    public async ValueTask<Result<ProjectionView>> HandleAsync(GetProjectionQuery query, CancellationToken ct = default)
    {
        var read = await facts.ReadAsync(query.Scope, query.Horizon, ct);
        if (read.TryGetFailure(out var failure)) return Result.Failure<ProjectionView>(failure!);
        read.TryGetValue(out var reading);
        return Result.Success(ProjectionView.From(query.Horizon, reading!, HouseholdProjection.Project(reading!.Inputs)));
    }
}
