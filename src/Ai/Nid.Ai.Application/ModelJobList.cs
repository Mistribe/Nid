using Nid.Ai.Contracts;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Ai.Application;

/// <summary>The jobs page (an admin, #585): what each job is, where it stands, why it waits. Filters and pages are the server's.</summary>
public sealed record ListModelJobsQuery(string? Status, string? Type, int? Page) : IQuery<ModelJobPage>, IAdminOnly;

[RequestHandler<ListModelJobsQuery, ModelJobPage>]
public sealed class ListModelJobsHandler(IJobStore jobs, IWorkerStore workers, IEnumerable<IJobFileSource> files)
    : IRequestHandler<ListModelJobsQuery, ModelJobPage>
{
    public async ValueTask<Result<ModelJobPage>> HandleAsync(ListModelJobsQuery query, CancellationToken ct = default)
    {
        var (page, skip) = ListPage.Of(query.Page, ModelJobPage.PageSize);
        var (items, total, types, counts) = await jobs.PageAsync(query.Status, query.Type, skip, ModelJobPage.PageSize, ct);
        var names = (await workers.ListAsync(ct)).ToDictionary(w => w.Id, w => w.Name);
        var dispatches = (await jobs.DispatchesOfAsync(items.Select(j => j.Id).ToList(), ct)).ToLookup(d => d.JobId);
        var fileOf = items.Where(j => j.FileRef is not null).ToDictionary(j => j.Id, j => JobDispatcher.FileOf(j.FileRef!));
        // #602: a file's reference only for the admin who reads it, as its module says.
        var readable = new HashSet<(string, string)>();
        foreach (var module in fileOf.Values.GroupBy(f => f.Module))
            if (files.FirstOrDefault(f => f.Module == module.Key) is { } source)
                readable.UnionWith((await source.ReadableAsync(module.Select(f => f.Reference).Distinct().ToList(), ct)).Select(r => (module.Key, r)));

        string? Name(Guid? worker) => worker is { } id ? names.GetValueOrDefault(id) : null;
        return Result.Success(new ModelJobPage(
            items.Select(j => new ModelJobListItem(j.Id, j.Type, j.Status.ToString().ToLowerInvariant(), j.Attempts, j.WaitingReason, j.Error,
                Name(j.WorkerId), j.CreatedAt, j.FinishedAt,
                fileOf.TryGetValue(j.Id, out var f) ? new ModelJobFile(f.Module, readable.Contains(f) ? f.Reference : null) : null,
                dispatches[j.Id].Select(d => new ModelJobDispatch(Name(d.WorkerId), d.SentAt, d.FinishedAt, d.Outcome)).ToList())).ToList(),
            total, page, types, counts));
    }
}
