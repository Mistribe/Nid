using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Core.Application;

public sealed record SessionsRevoked(Guid PersonId);

/// <summary>"Déconnecter partout" for oneself (administration-and-audit.md §5, spec U16).</summary>
public sealed record RevokeMySessionsCommand : ICommand<SessionsRevoked>, IRecordsSecurityEvent
{
    public SecurityEvent EventFor(object response)
        => SecurityEvent.Of(SecurityEventNames.SessionRevoked, new PersonId(((SessionsRevoked)response).PersonId));
}

[RequestHandler<RevokeMySessionsCommand, SessionsRevoked>]
public sealed class RevokeMySessionsHandler(IPersonRegistry persons, IActorAccessor accessor)
    : IRequestHandler<RevokeMySessionsCommand, SessionsRevoked>
{
    public async ValueTask<Result<SessionsRevoked>> HandleAsync(RevokeMySessionsCommand command, CancellationToken ct = default)
    {
        if (accessor.Current?.PersonId is not { } me)
            return Result.Failure<SessionsRevoked>(new UnauthorizedFailure("Only a person has sessions.", null));
        return (await persons.RevokeSessionsAsync(me, ct)).Map(p => new SessionsRevoked(p.Id.Value));
    }
}
