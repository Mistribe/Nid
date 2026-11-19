using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Platform.Admin;

/// <summary>Each value is <c>healthy</c> or <c>unhealthy</c>; <c>IdentityProvider</c> is <c>not_used</c> without one (issue 215).</summary>
public sealed record SystemHealthView(string Database, string IdentityProvider);

/// <summary>
/// Issue 215: how adults sign in (<c>local</c>, <c>oidc</c>, <c>both</c>), who decides the admin role
/// (<c>identity_provider</c>, <c>nid</c>), and whether plain HTTP is allowed, which the admin area warns about.
/// </summary>
public sealed record SystemAuthView(string Mode, string AdminSource, bool InsecureHttp);

/// <summary>No item count on purpose: a household total would reveal the other adult's private items (authorization.md §8).</summary>
public sealed record SystemStatusView(string Version, DateTimeOffset StartedAt, SystemHealthView Health, long? DatabaseSizeBytes, SystemAuthView Auth);

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
