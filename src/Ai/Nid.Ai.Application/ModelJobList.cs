using Nid.Ai.Contracts;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Ai.Application;

/// <summary>The jobs page (any adult): what each job is, where it stands, why it waits. Filters and pages are the server's.</summary>
public sealed record ListModelJobsQuery(string? Status, string? Type, int? Page) : IQuery<ModelJobPage>;

[RequestHandler<ListModelJobsQuery, ModelJobPage>]
public sealed class ListModelJobsHandler(IJobStore jobs, IWorkerStore workers) : IRequestHandler<ListModelJobsQuery, ModelJobPage>
{
    public async ValueTask<Result<ModelJobPage>> HandleAsync(ListModelJobsQuery query, CancellationToken ct = default)
    {
        var (page, skip) = ListPage.Of(query.Page, ModelJobPage.PageSize);
        var (items, total, types) = await jobs.PageAsync(query.Status, query.Type, skip, ModelJobPage.PageSize, ct);
        var names = (await workers.ListAsync(ct)).ToDictionary(w => w.Id, w => w.Name);
        return Result.Success(new ModelJobPage(
            items.Select(j => new ModelJobListItem(j.Id, j.Type, j.Status.ToString().ToLowerInvariant(), j.Attempts, j.WaitingReason, j.Error,
                j.WorkerId is { } id ? names.GetValueOrDefault(id) : null, j.CreatedAt, j.FinishedAt)).ToList(),
            total, page, types));
    }
}
