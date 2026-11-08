using System.Text.Json.Serialization;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Core.Application;

/// <summary>A token as « Mes jetons » shows it (spec 4A §6): never its hash, never its secret.</summary>
public sealed record TokenView(
    Guid Id, string Name, string Preset, string Prefix, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt,
    DateTimeOffset? LastUsedAt, DateTimeOffset? RevokedAt, string Status)
{
    public static TokenView From(PersonalAccessToken t, Instant now) => new(
        t.Id, t.Name, TokenPresets.Of(t.Scopes), t.Prefix, t.CreatedAt.ToDateTimeOffset(), t.ExpiresAt.ToDateTimeOffset(),
        t.LastUsedAt?.ToDateTimeOffset(), t.RevokedAt?.ToDateTimeOffset(),
        t.RevokedAt is not null ? "revoked" : t.ExpiresAt <= now ? "expired" : "active");
}

/// <summary>
/// The only response that carries the secret (tokens-and-credentials.md §1.2). <see cref="PersonId"/> is the event's
/// subject (so « Mes derniers événements » shows it) and stays off the wire.
/// </summary>
public sealed record CreatedTokenView(TokenView Token, string Secret, [property: JsonIgnore] Guid PersonId);

public sealed record TokenRevokedView(Guid Id, bool Changed, [property: JsonIgnore] Guid PersonId);

public sealed record ListMyTokensQuery : IQuery<IReadOnlyList<TokenView>>, IBrowserOnly;

public sealed record CreateTokenCommand(string Name, string Preset, int? ExpiresInDays)
    : ICommand<CreatedTokenView>, IBrowserOnly, IRecordsSecurityEvent
{
    public SecurityEvent EventFor(object response)
    {
        var created = (CreatedTokenView)response;
        return new SecurityEvent(SecurityEventNames.TokenCreated, new PersonId(created.PersonId), [],
            new Dictionary<string, string> { ["token"] = created.Token.Id.ToString(), ["preset"] = created.Token.Preset });
    }
}

public sealed record RevokeMyTokenCommand(Guid Token) : ICommand<TokenRevokedView>, IBrowserOnly, IRecordsSecurityEvent
{
    /// <summary>A second revocation changed nothing: nothing worth an event.</summary>
    public SecurityEvent? EventFor(object response)
        => response is TokenRevokedView { Changed: true } revoked
            ? new SecurityEvent(SecurityEventNames.TokenRevoked, new PersonId(revoked.PersonId), [],
                new Dictionary<string, string> { ["token"] = revoked.Id.ToString() })
            : null;
}

[RequestHandler<ListMyTokensQuery, IReadOnlyList<TokenView>>]
public sealed class ListMyTokensHandler(IPersonalAccessTokens tokens, IActorAccessor accessor, IClock clock)
    : IRequestHandler<ListMyTokensQuery, IReadOnlyList<TokenView>>
{
    public async ValueTask<Result<IReadOnlyList<TokenView>>> HandleAsync(ListMyTokensQuery query, CancellationToken ct = default)
    {
        if (accessor.Current?.PersonId is not { } me)
            return Result.Failure<IReadOnlyList<TokenView>>(new UnauthorizedFailure("Only a person has tokens.", null));
        var now = clock.GetCurrentInstant();
        return Result.Success<IReadOnlyList<TokenView>>((await tokens.ListAsync(me, ct)).Select(t => TokenView.From(t, now)).ToList());
    }
}

[RequestHandler<CreateTokenCommand, CreatedTokenView>]
public sealed class CreateTokenHandler(IPersonalAccessTokens tokens, IActorAccessor accessor, IClock clock)
    : IRequestHandler<CreateTokenCommand, CreatedTokenView>
{
    public async ValueTask<Result<CreatedTokenView>> HandleAsync(CreateTokenCommand command, CancellationToken ct = default)
    {
        if (accessor.Current?.PersonId is not { } me)
            return Result.Failure<CreatedTokenView>(new UnauthorizedFailure("Only a person has tokens.", null));
        return (await tokens.CreateAsync(me, command.Name, command.Preset, command.ExpiresInDays, ct))
            .Map(created => new CreatedTokenView(TokenView.From(created.Token, clock.GetCurrentInstant()), created.Secret, me.Value));
    }
}

[RequestHandler<RevokeMyTokenCommand, TokenRevokedView>]
public sealed class RevokeMyTokenHandler(IPersonalAccessTokens tokens, IActorAccessor accessor)
    : IRequestHandler<RevokeMyTokenCommand, TokenRevokedView>
{
    public async ValueTask<Result<TokenRevokedView>> HandleAsync(RevokeMyTokenCommand command, CancellationToken ct = default)
    {
        if (accessor.Current?.PersonId is not { } me)
            return Result.Failure<TokenRevokedView>(new UnauthorizedFailure("Only a person has tokens.", null));
        return (await tokens.RevokeAsync(me, command.Token, ct)).Map(changed => new TokenRevokedView(command.Token, changed, me.Value));
    }
}
