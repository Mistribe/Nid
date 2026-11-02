using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Platform.Admin;

/// <summary>Each value is <c>healthy</c> or <c>unhealthy</c>.</summary>
public sealed record SystemHealthView(string Database, string IdentityProvider);

/// <summary>No item count on purpose: a household total would reveal the other adult's private items (authorization.md §8).</summary>
public sealed record SystemStatusView(string Version, DateTimeOffset StartedAt, SystemHealthView Health, long? DatabaseSizeBytes);

/// <summary>Implemented by the host: version, start time, database and identity-provider probes.</summary>
public interface ISystemStatusSource
{
    Task<SystemStatusView> ReadAsync(CancellationToken ct);
}

public sealed record GetSystemStatusQuery : IQuery<SystemStatusView>, IAdminOnly;

[RequestHandler<GetSystemStatusQuery, SystemStatusView>]
public sealed class GetSystemStatusHandler(ISystemStatusSource source) : IRequestHandler<GetSystemStatusQuery, SystemStatusView>
{
    public async ValueTask<Result<SystemStatusView>> HandleAsync(GetSystemStatusQuery query, CancellationToken ct = default)
        => Result.Success(await source.ReadAsync(ct));
}
