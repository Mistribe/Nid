using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Platform.Admin;

/// <summary>A scheduled job as the admin area shows it; <see cref="State"/> is <c>running</c> or <c>idle</c>.</summary>
public sealed record JobStatusView(string Key, DateTimeOffset? LastRunAt, DateTimeOffset? NextRunAt, string State);

/// <summary>Each value is <c>healthy</c> or <c>unhealthy</c>.</summary>
public sealed record SystemHealthView(string Database, string IdentityProvider);

/// <summary>No item count on purpose: a household total would reveal the other adult's private items (authorization.md §8).</summary>
public sealed record SystemStatusView(string Version, DateTimeOffset StartedAt, SystemHealthView Health, long? DatabaseSizeBytes);

/// <summary>The scheduler's jobs; empty when jobs are disabled on this instance. Implemented by the host (Quartz).</summary>
public interface IJobStatusReader
{
    Task<IReadOnlyList<JobStatusView>> ListAsync(CancellationToken ct);
}

/// <summary>Implemented by the host: version, start time, database and identity-provider probes.</summary>
public interface ISystemStatusSource
{
    Task<SystemStatusView> ReadAsync(CancellationToken ct);
}

public sealed record ListJobsQuery : IQuery<IReadOnlyList<JobStatusView>>, IAdminOnly;

public sealed record GetSystemStatusQuery : IQuery<SystemStatusView>, IAdminOnly;

[RequestHandler<ListJobsQuery, IReadOnlyList<JobStatusView>>]
public sealed class ListJobsHandler(IJobStatusReader jobs) : IRequestHandler<ListJobsQuery, IReadOnlyList<JobStatusView>>
{
    public async ValueTask<Result<IReadOnlyList<JobStatusView>>> HandleAsync(ListJobsQuery query, CancellationToken ct = default)
        => Result.Success(await jobs.ListAsync(ct));
}

[RequestHandler<GetSystemStatusQuery, SystemStatusView>]
public sealed class GetSystemStatusHandler(ISystemStatusSource source) : IRequestHandler<GetSystemStatusQuery, SystemStatusView>
{
    public async ValueTask<Result<SystemStatusView>> HandleAsync(GetSystemStatusQuery query, CancellationToken ct = default)
        => Result.Success(await source.ReadAsync(ct));
}
