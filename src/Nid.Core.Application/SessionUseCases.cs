using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Core.Application;

public sealed record SessionsRevoked(Guid PersonId, int TokensRevoked);

/// <summary>"Déconnecter partout" for oneself (administration-and-audit.md §5, spec U16); 4A: optionally the tokens too.</summary>
public sealed record RevokeMySessionsCommand(bool RevokeTokens = false) : ICommand<SessionsRevoked>, IRecordsSecurityEvent, IBrowserOnly
{
    public SecurityEvent EventFor(object response)
        => SecurityEvent.Of(SecurityEventNames.SessionRevoked, new PersonId(((SessionsRevoked)response).PersonId));
}

[RequestHandler<RevokeMySessionsCommand, SessionsRevoked>]
public sealed class RevokeMySessionsHandler(IPersonRegistry persons, IPersonalAccessTokens tokens, ISecurityEvents events, IActorAccessor accessor)
    : IRequestHandler<RevokeMySessionsCommand, SessionsRevoked>
{
    public async ValueTask<Result<SessionsRevoked>> HandleAsync(RevokeMySessionsCommand command, CancellationToken ct = default)
    {
        if (accessor.Current?.PersonId is not { } me)
            return Result.Failure<SessionsRevoked>(new UnauthorizedFailure("Only a person has sessions.", null));
        var revoked = await persons.RevokeSessionsAsync(me, ct);
        if (revoked.TryGetFailure(out var failure))
            return Result.Failure<SessionsRevoked>(failure!);

        var count = command.RevokeTokens ? await tokens.RevokeAllAsync(me, ct) : 0;
        if (count > 0)
            await events.RecordAsync(new SecurityEvent(SecurityEventNames.TokenRevoked, me, [],
                new Dictionary<string, string> { ["count"] = count.ToString(System.Globalization.CultureInfo.InvariantCulture) }), ct);
        return Result.Success(new SessionsRevoked(me.Value, count));
    }
}
